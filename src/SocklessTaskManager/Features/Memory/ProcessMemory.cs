using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Memory;

/// <param name="PrivateWorkingSet">RAM only this process uses; what Task Manager shows as "Memory".</param>
/// <param name="WorkingSet">Everything the process has in RAM, including pages shared with others.</param>
/// <param name="Commit">Private bytes the process has committed, in RAM or in the page file.</param>
internal readonly record struct ProcessUsage(int Id, string Name, ulong PrivateWorkingSet, ulong WorkingSet, ulong Commit);

/// <summary>Every process's memory counters, from <see cref="SystemProcesses"/>.</summary>
internal static class ProcessMemory
{
    /// <summary>The kernel process that holds compressed pages. Its working set is the compression store.</summary>
    public const string CompressionName = "Memory Compression";

    public static List<ProcessUsage> Read(ref byte[] buffer) =>
        [.. SystemProcesses.Read(ref buffer).Select(p => new ProcessUsage(p.Id, p.Name, p.PrivateWorkingSet, p.WorkingSet, p.Commit))];
}
