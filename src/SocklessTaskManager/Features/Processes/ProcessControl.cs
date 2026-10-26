using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// What the user can do to a running process: end it, suspend or resume it, change its priority
/// and processor affinity, and write a dump of it. Every call opens the process by key, so it
/// acts on that process or on nothing.
/// </summary>
internal static class ProcessControl
{
    /// <summary>Priority classes, highest first.</summary>
    public const uint Realtime = 0x100;
    public const uint High = 0x80;
    public const uint AboveNormal = 0x8000;
    public const uint Normal = 0x20;
    public const uint BelowNormal = 0x4000;
    public const uint Idle = 0x40;

    /// <summary>Task Manager's exit code for "End task".</summary>
    private const uint EndTaskExitCode = 1;

    /// <summary>
    /// A full dump, as Task Manager and dotnet-dump write them: all memory, plus handles, threads,
    /// unloaded modules and the memory map, so Visual Studio, WinDbg and dotnet-dump can open it.
    /// </summary>
    private const uint FullDump =
        0x00001     // MiniDumpWithDataSegs
        | 0x00002   // MiniDumpWithFullMemory
        | 0x00004   // MiniDumpWithHandleData
        | 0x00020   // MiniDumpWithUnloadedModules
        | 0x00800   // MiniDumpWithFullMemoryInfo
        | 0x01000   // MiniDumpWithThreadInfo
        | 0x40000;  // MiniDumpWithTokenInformation

    public static Outcome End(ProcessKey key)
    {
        var opened = ProcessHandle.Open(key, ProcessNative.Terminate, out var handle);
        if (opened != Outcome.Done)
            return opened;

        using (handle)
        {
            if (ProcessNative.TerminateProcess(handle.Value, EndTaskExitCode))
                return Outcome.Done;

            // Ending a process that is already on its way out fails with access denied.
            return Marshal.GetLastPInvokeError() == ProcessNative.ErrorAccessDenied && HasExited(handle.Value)
                ? Outcome.Done
                : Error();
        }
    }

    public static Outcome Suspend(ProcessKey key, bool suspend)
    {
        var opened = ProcessHandle.Open(key, ProcessNative.SuspendResume, out var handle);
        if (opened != Outcome.Done)
            return opened;

        using (handle)
        {
            var status = suspend ? ProcessNative.NtSuspendProcess(handle.Value) : ProcessNative.NtResumeProcess(handle.Value);
            return status switch
            {
                0 => Outcome.Done,
                ProcessNative.StatusAccessDenied => Outcome.Denied,
                _ => Outcome.Failed,
            };
        }
    }

    public static uint? Priority(ProcessKey key)
    {
        if (ProcessHandle.Open(key, 0, out var handle) != Outcome.Done)
            return null;

        using (handle)
        {
            var priority = ProcessNative.GetPriorityClass(handle.Value);
            return priority == 0 ? null : priority;
        }
    }

    /// <summary>Real-time without the right to it is quietly set as High: read it back.</summary>
    public static Outcome SetPriority(ProcessKey key, uint priorityClass)
    {
        var opened = ProcessHandle.Open(key, ProcessNative.SetInformation, out var handle);
        if (opened != Outcome.Done)
            return opened;

        using (handle)
            return ProcessNative.SetPriorityClass(handle.Value, priorityClass) ? Outcome.Done : Error();
    }

    /// <summary>The processors the process may run on, and the processors there are (both as bit masks).</summary>
    public static (ulong Process, ulong System)? Affinity(ProcessKey key)
    {
        if (ProcessHandle.Open(key, 0, out var handle) != Outcome.Done)
            return null;

        using (handle)
        {
            return ProcessNative.GetProcessAffinityMask(handle.Value, out var process, out var system)
                ? (process, system)
                : null;
        }
    }

    public static Outcome SetAffinity(ProcessKey key, ulong mask)
    {
        var opened = ProcessHandle.Open(key, ProcessNative.SetInformation, out var handle);
        if (opened != Outcome.Done)
            return opened;

        using (handle)
            return ProcessNative.SetProcessAffinityMask(handle.Value, (nuint)mask) ? Outcome.Done : Error();
    }

    /// <summary>
    /// Writes a full dump to <paramref name="path"/>. Windows pauses the process while it does,
    /// which takes as long as copying its memory. A failed dump leaves no file.
    /// </summary>
    public static Outcome WriteDump(ProcessKey key, string path)
    {
        var opened = ProcessHandle.Open(key,
            ProcessNative.QueryInformation | ProcessNative.VmRead | ProcessNative.DuplicateHandle, out var handle);
        if (opened != Outcome.Done)
            return opened;

        using (handle)
        {
            bool written;
            int error;
            try
            {
                using var file = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite);
                written = ProcessNative.MiniDumpWriteDump(handle.Value, (uint)key.Id, file, FullDump, 0, 0, 0);
                error = Marshal.GetLastPInvokeError();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Outcome.Failed;
            }

            if (written)
                return Outcome.Done;

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            // The error is an HRESULT wrapping a Win32 code.
            if (HasExited(handle.Value))
                return Outcome.Gone;
            return (error & 0xFFFF) == ProcessNative.ErrorAccessDenied ? Outcome.Denied : Outcome.Failed;
        }
    }

    private static bool HasExited(nint process) =>
        ProcessNative.GetProcessTimes(process, out _, out var exit, out _, out _) && exit != 0;

    private static Outcome Error() =>
        Marshal.GetLastPInvokeError() == ProcessNative.ErrorAccessDenied ? Outcome.Denied : Outcome.Failed;
}
