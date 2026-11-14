using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>What a SID names, told from its well-known prefix.</summary>
internal enum AccountKind
{
    /// <summary>A person: a local, domain, Microsoft or Entra ID account.</summary>
    Person,

    /// <summary>LocalSystem: Windows itself.</summary>
    System,

    /// <summary>LOCAL SERVICE, NETWORK SERVICE, or a service's own virtual account (NT SERVICE\…).</summary>
    Service,

    /// <summary>Desktop Window Manager's account in each session (Window Manager\DWM-n).</summary>
    WindowManager,

    /// <summary>The user-mode font driver host's account in each session (Font Driver Host\UMFD-n).</summary>
    FontDriver,

    /// <summary>An IIS application pool (IIS APPPOOL\…).</summary>
    AppPool,

    Other,
}

/// <summary>An account that owns processes.</summary>
/// <param name="Sid">In its text form, S-1-5-18.</param>
/// <param name="Name">Without the domain ("jimmy", "SYSTEM"). The SID when Windows can't name it (a deleted account).</param>
internal sealed record Account(string Sid, string Name, string? Domain, AccountKind Kind)
{
    /// <summary>DOMAIN\name, as Windows writes it.</summary>
    public string FullName => Domain is { Length: > 0 } domain ? $"{domain}\\{Name}" : Name;
}

/// <summary>
/// Who a process runs as, from its token. Opening another user's or a system process's token
/// needs administrator rights. Naming a domain account can wait on the network, so callers that
/// ask every tick keep a cache: a few accounts own every process.
/// </summary>
internal static unsafe partial class Accounts
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;
    private const int SidTypeUser = 1;
    private const int TokenUserBuffer = 256;

    /// <summary>The account of a process opened with at least <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.</summary>
    /// <param name="cache">Accounts by SID, kept by the caller; null to look the name up every time.</param>
    public static Account? Owner(nint process, Dictionary<string, Account>? cache = null)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
            return null;

        try
        {
            return OfToken(token, cache);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>The owner and whether the process runs elevated, from one token.</summary>
    public static (Account? Owner, bool? IsElevated) OwnerAndElevation(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
            return (null, null);

        try
        {
            bool? elevated = null;
            uint isElevated = 0;
            if (GetTokenInformation(token, TokenElevation, &isElevated, sizeof(uint), out _))
                elevated = isElevated != 0;

            return (OfToken(token, null), elevated);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static Account? OfToken(nint token, Dictionary<string, Account>? cache)
    {
        var buffer = stackalloc byte[TokenUserBuffer];
        if (!GetTokenInformation(token, TokenUser, buffer, TokenUserBuffer, out _))
            return null;

        // TOKEN_USER starts with a pointer to the SID, which lies further on in the same buffer.
        var sid = *(byte**)buffer;
        var length = 8 + 4 * sid[1];
        if ((nint)sid < (nint)buffer || (nint)sid + length > (nint)buffer + TokenUserBuffer)
            return null;

        var bytes = new ReadOnlySpan<byte>(sid, length);
        var text = SidText(bytes);
        if (cache is not null && cache.TryGetValue(text, out var cached))
            return cached;

        var name = stackalloc char[256];
        var domain = stackalloc char[256];
        uint nameLength = 256, domainLength = 256;
        var account = LookupAccountSid(null, sid, name, ref nameLength, domain, ref domainLength, out var use)
            ? new Account(text, new string(name, 0, (int)nameLength),
                domainLength > 0 ? new string(domain, 0, (int)domainLength) : null, Classify(text, use))
            : new Account(text, text, null, Classify(text, 0));

        if (cache is not null)
            cache[text] = account;
        return account;
    }

    /// <summary>S-1-{authority}-{sub-authority}…, as <c>ConvertSidToStringSid</c> writes it.</summary>
    public static string SidText(ReadOnlySpan<byte> sid)
    {
        ulong authority = 0;
        for (var i = 2; i < 8; i++)
            authority = authority << 8 | sid[i];

        var text = new StringBuilder("S-", 64);
        text.Append(sid[0].ToString(CultureInfo.InvariantCulture)).Append('-').Append(authority.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < sid[1]; i++)
            text.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(sid[(8 + 4 * i)..]).ToString(CultureInfo.InvariantCulture));
        return text.ToString();
    }

    public static AccountKind Classify(string sid, int use) => sid switch
    {
        "S-1-5-18" => AccountKind.System,
        "S-1-5-19" or "S-1-5-20" => AccountKind.Service,
        _ when sid.StartsWith("S-1-5-80-", StringComparison.Ordinal) => AccountKind.Service,
        _ when sid.StartsWith("S-1-5-82-", StringComparison.Ordinal) => AccountKind.AppPool,
        _ when sid.StartsWith("S-1-5-90-", StringComparison.Ordinal) => AccountKind.WindowManager,
        _ when sid.StartsWith("S-1-5-96-", StringComparison.Ordinal) => AccountKind.FontDriver,
        _ when sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)
               || sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)
               || sid.StartsWith("S-1-11-", StringComparison.Ordinal) => AccountKind.Person,
        _ => use == SidTypeUser ? AccountKind.Person : AccountKind.Other,
    };

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, void* buffer, uint length, out uint returned);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupAccountSid(char* system, void* sid, char* name, ref uint nameLength, char* domain, ref uint domainLength, out int use);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
