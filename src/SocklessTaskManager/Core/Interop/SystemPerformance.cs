using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>System-wide counters from <c>GetPerformanceInfo</c>, used by more than one slice.</summary>
public readonly partial record struct SystemPerformance(
    ulong PhysicalTotalBytes,
    ulong CommitTotalBytes,
    ulong CommitLimitBytes,
    ulong SystemCacheBytes,
    ulong PagedPoolBytes,
    ulong NonPagedPoolBytes,
    uint HandleCount,
    uint ProcessCount,
    uint ThreadCount)
{
    public static unsafe SystemPerformance Read()
    {
        var info = new PerformanceInformation { Size = (uint)sizeof(PerformanceInformation) };
        if (!GetPerformanceInfo(ref info, info.Size))
            return default;

        var page = (ulong)info.PageSize;
        return new SystemPerformance(
            info.PhysicalTotal * page,
            info.CommitTotal * page,
            info.CommitLimit * page,
            info.SystemCache * page,
            info.KernelPaged * page,
            info.KernelNonpaged * page,
            info.HandleCount,
            info.ProcessCount,
            info.ThreadCount);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [LibraryImport("psapi.dll", EntryPoint = "GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPerformanceInfo(ref PerformanceInformation info, uint size);
}
