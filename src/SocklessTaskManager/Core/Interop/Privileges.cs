using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>
/// Turns on a privilege the process token holds but has off: SeBackupPrivilege for the disk scan,
/// SeShutdownPrivilege for a service recovery that restarts the computer. Without elevation the
/// token holds neither, and this says no.
/// </summary>
internal static unsafe partial class Privileges
{
    public const string Backup = "SeBackupPrivilege";
    public const string Shutdown = "SeShutdownPrivilege";

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    private static readonly ConcurrentDictionary<string, bool> Enabled = new(StringComparer.Ordinal);

    /// <summary>True once the privilege is on for the process. Asked again, it answers from memory.</summary>
    public static bool Enable(string name) => Enabled.GetOrAdd(name, static name =>
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            return false;

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                return false;

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };

            // It succeeds even when the token doesn't hold the privilege, and says so in the last error.
            return AdjustTokenPrivileges(token, false, &privileges, 0, null, null)
                && Marshal.GetLastPInvokeError() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    });

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TokenPrivileges* state, uint length, TokenPrivileges* previous, uint* returned);
}
