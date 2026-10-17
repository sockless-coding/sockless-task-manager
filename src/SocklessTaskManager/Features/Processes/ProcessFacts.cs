using System.Buffers.Binary;
using System.Diagnostics;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>What the details pane shows about a process beyond the sampled figures. Null where it can't be read.</summary>
/// <param name="IsCritical">Windows stops (bug check) if this process ends.</param>
internal sealed record ProcessFactsReading(
    string? CommandLine,
    string? User,
    bool? IsElevated,
    string? Description,
    string? Company,
    bool IsCritical);

/// <summary>
/// One-off reads for the details pane, taken when a process is selected. Run off the UI thread:
/// resolving a domain account's name can wait on the network.
/// </summary>
internal static unsafe class ProcessFacts
{
    private const int ProcessBreakOnTermination = 29;
    private const int ProcessCommandLineInformation = 60;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;

    /// <summary>
    /// Ending these stops Windows, or leaves the session unusable. Most of them are protected
    /// processes whose critical flag can't be read, so they are named here as well.
    /// </summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
    };

    /// <summary>Suspending these freezes the desktop, this window included, so nothing could resume them.</summary>
    private static readonly HashSet<string> UnsafeToSuspendNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm.exe", "fontdrvhost.exe",
    };

    public static ProcessFactsReading Read(ProcessKey key, string name, string? path)
    {
        string? commandLine = null, user = null;
        bool? elevated = null;
        if (ProcessHandle.Open(key, 0, out var handle) == Outcome.Done)
        {
            using (handle)
            {
                commandLine = CommandLine(handle.Value);
                (user, elevated) = Token(handle.Value);
            }
        }

        var (description, company) = path is null ? (null, null) : VersionInfo(path);
        return new ProcessFactsReading(commandLine, user, elevated, description, company, IsCritical(key, name));
    }

    /// <summary>The executable's description and publisher from its version resource.</summary>
    public static (string? Description, string? Company) VersionInfo(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return (NullIfBlank(version.FileDescription), NullIfBlank(version.CompanyName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The account the process runs as, without the domain ("jimmy", "SYSTEM", "LOCAL SERVICE"),
    /// as the User column shows it. Names are cached by SID: a few accounts own every process.
    /// Fails for other users' processes unless elevated.
    /// </summary>
    public static string? Owner(nint process, Dictionary<string, string?> accounts)
    {
        if (!ProcessNative.OpenProcessToken(process, TokenQuery, out var token))
            return null;

        try
        {
            var buffer = stackalloc byte[256];
            if (!ProcessNative.GetTokenInformation(token, TokenUser, buffer, 256, out _))
                return null;

            var sid = *(byte**)buffer;
            var sidLength = 8 + 4 * sid[1];
            var key = Convert.ToHexString(new ReadOnlySpan<byte>(sid, sidLength));
            if (accounts.TryGetValue(key, out var cached))
                return cached;

            var name = stackalloc char[256];
            var domain = stackalloc char[256];
            uint nameLength = 256, domainLength = 256;
            var account = ProcessNative.LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out _)
                ? new string(name, 0, (int)nameLength)
                : null;
            accounts[key] = account;
            return account;
        }
        finally
        {
            ProcessNative.CloseHandle(token);
        }
    }

    public static bool IsCritical(ProcessKey key, string name)
    {
        if (CriticalNames.Contains(name))
            return true;
        if (ProcessHandle.Open(key, ProcessNative.QueryInformation, out var handle) != Outcome.Done)
            return false;

        using (handle)
        {
            uint flag = 0;
            return ProcessNative.NtQueryInformationProcess(handle.Value, ProcessBreakOnTermination, &flag, sizeof(uint), out _) == 0
                   && flag != 0;
        }
    }

    public static bool IsUnsafeToSuspend(ProcessKey key, string name, bool isCritical) =>
        isCritical || key.Id == Environment.ProcessId || UnsafeToSuspendNames.Contains(name) || CriticalNames.Contains(name);

    /// <summary>A UNICODE_STRING header followed by the text, in one buffer.</summary>
    public static string? CommandLine(nint process)
    {
        var buffer = new byte[2048];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint status, returned;
            fixed (byte* p = buffer)
            {
                status = ProcessNative.NtQueryInformationProcess(process, ProcessCommandLineInformation, p, (uint)buffer.Length, out returned);
                if (status == 0)
                {
                    var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
                    var text = (long)BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)) - (long)p;
                    if (length == 0 || text < 16 || text + length > returned)
                        return null;

                    return NullIfBlank(new string((char*)(p + text), 0, length / 2));
                }
            }

            if (status != ProcessNative.StatusInfoLengthMismatch || returned <= buffer.Length)
                return null;
            buffer = new byte[returned];
        }

        return null;
    }

    private static (string? User, bool? Elevated) Token(nint process)
    {
        if (!ProcessNative.OpenProcessToken(process, TokenQuery, out var token))
            return (null, null);

        try
        {
            bool? elevated = null;
            uint isElevated = 0;
            if (ProcessNative.GetTokenInformation(token, TokenElevation, &isElevated, sizeof(uint), out _))
                elevated = isElevated != 0;

            var buffer = stackalloc byte[256];
            if (!ProcessNative.GetTokenInformation(token, TokenUser, buffer, 256, out _))
                return (null, elevated);

            // TOKEN_USER starts with a pointer to the SID, which lies further on in the same buffer.
            var sid = *(void**)buffer;
            var name = stackalloc char[256];
            var domain = stackalloc char[256];
            uint nameLength = 256, domainLength = 256;
            if (!ProcessNative.LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out _))
                return (null, elevated);

            var user = new string(name, 0, (int)nameLength);
            return (domainLength > 0 ? $"{new string(domain, 0, (int)domainLength)}\\{user}" : user, elevated);
        }
        finally
        {
            ProcessNative.CloseHandle(token);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
