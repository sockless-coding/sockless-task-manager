using System.Runtime.InteropServices;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Performance.Memory;

internal sealed record MemoryReading(
    ulong TotalBytes,
    ulong AvailableBytes,
    ulong CommittedBytes,
    ulong CommitLimitBytes,
    ulong CachedBytes,
    ulong PagedPoolBytes,
    ulong NonPagedPoolBytes)
{
    public ulong InUseBytes => TotalBytes - AvailableBytes;
}

internal static partial class MemorySensor
{
    public static MemoryReading Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref status);
        var system = SystemPerformance.Read();

        return new MemoryReading(
            status.TotalPhys,
            status.AvailPhys,
            system.CommitTotalBytes,
            system.CommitLimitBytes,
            system.SystemCacheBytes,
            system.PagedPoolBytes,
            system.NonPagedPoolBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
