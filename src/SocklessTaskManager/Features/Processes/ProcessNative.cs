using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>The Win32 and native calls the Processes slice makes on single processes.</summary>
internal static unsafe partial class ProcessNative
{
    public const uint Terminate = 0x0001;
    public const uint SetInformation = 0x0200;
    public const uint QueryInformation = 0x0400;
    public const uint SuspendResume = 0x0800;
    public const uint QueryLimitedInformation = 0x1000;

    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidParameter = 87;
    public const uint StatusAccessDenied = 0xC0000022;
    public const uint StatusInfoLengthMismatch = 0xC0000004;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetPriorityClass(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessAffinityMask(nint process, out nuint processMask, out nuint systemMask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessAffinityMask(nint process, nuint mask);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW")]
    public static partial uint QueryDosDevice(char* device, char* target, uint max);

    [LibraryImport("ntdll.dll")]
    public static partial uint NtSuspendProcess(nint process);

    [LibraryImport("ntdll.dll")]
    public static partial uint NtResumeProcess(nint process);

    [LibraryImport("ntdll.dll")]
    public static partial uint NtQueryInformationProcess(nint process, int infoClass, void* buffer, uint length, out uint returned);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(nint token, int infoClass, void* buffer, uint length, out uint returned);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupAccountSidW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupAccountSid(char* system, void* sid, char* name, ref uint nameLength, char* domain, ref uint domainLength, out int use);
}

/// <summary>Why an action on a process didn't happen.</summary>
internal enum Outcome
{
    Done,

    /// <summary>The process has exited, or its id now belongs to another process.</summary>
    Gone,

    /// <summary>Protected, or another user's and this app isn't elevated.</summary>
    Denied,

    Failed,
}

/// <summary>
/// An open handle to the one process a <see cref="ProcessKey"/> names. Ids are reused, so the
/// handle is only handed out once the process's start time matches the key: an action can never
/// land on a newer process that happens to have the same id.
/// </summary>
internal readonly struct ProcessHandle : IDisposable
{
    private ProcessHandle(nint value) => Value = value;

    public nint Value { get; }

    public static Outcome Open(ProcessKey key, uint access, out ProcessHandle handle)
    {
        handle = default;
        var value = ProcessNative.OpenProcess(access | ProcessNative.QueryLimitedInformation, false, (uint)key.Id);
        if (value == 0)
        {
            return Marshal.GetLastPInvokeError() switch
            {
                ProcessNative.ErrorAccessDenied => Outcome.Denied,
                ProcessNative.ErrorInvalidParameter => Outcome.Gone,
                _ => Outcome.Failed,
            };
        }

        if (!ProcessNative.GetProcessTimes(value, out var created, out _, out _, out _) || created != key.CreateTime)
        {
            ProcessNative.CloseHandle(value);
            return Outcome.Gone;
        }

        handle = new ProcessHandle(value);
        return Outcome.Done;
    }

    public void Dispose()
    {
        if (Value != 0)
            ProcessNative.CloseHandle(Value);
    }
}
