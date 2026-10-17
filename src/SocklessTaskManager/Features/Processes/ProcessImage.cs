using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// A process's executable path. Needs a handle, so it fails for protected processes (System,
/// Registry, some services) and, without elevation, for other users' processes.
/// </summary>
internal static partial class ProcessImage
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int MaxPath = 32767;

    public static string? Path(int id)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)id);
        if (process == 0)
            return null;

        try
        {
            // Almost every path fits on the stack; only a long-path executable needs the heap.
            Span<char> stack = stackalloc char[512];
            return Query(process, stack) ?? Query(process, new char[MaxPath]);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static unsafe string? Query(nint process, Span<char> buffer)
    {
        var size = (uint)buffer.Length;
        fixed (char* p = buffer)
            return QueryFullProcessImageName(process, 0, p, ref size) ? new string(buffer[..(int)size]) : null;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
