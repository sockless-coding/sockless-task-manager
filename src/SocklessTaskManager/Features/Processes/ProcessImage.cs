namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// A process's executable path. Needs a handle, so it fails for protected processes (System,
/// Registry, some services) and, without elevation, for other users' processes.
/// </summary>
internal static class ProcessImage
{
    private const int MaxPath = 32767;

    /// <param name="process">A handle with at least query-limited-information access.</param>
    public static string? Path(nint process)
    {
        // Almost every path fits on the stack; only a long-path executable needs the heap.
        Span<char> stack = stackalloc char[512];
        return Query(process, stack) ?? Query(process, new char[MaxPath]);
    }

    private static unsafe string? Query(nint process, Span<char> buffer)
    {
        var size = (uint)buffer.Length;
        fixed (char* p = buffer)
            return ProcessNative.QueryFullProcessImageName(process, 0, p, ref size) ? new string(buffer[..(int)size]) : null;
    }
}
