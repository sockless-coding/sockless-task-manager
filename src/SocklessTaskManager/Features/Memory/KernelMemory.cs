using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>Kernel memory that is resident in RAM, by kind.</summary>
internal readonly record struct KernelReading(
    ulong PagedPoolBytes,
    ulong NonPagedPoolBytes,
    ulong DriverCodeBytes,
    ulong KernelCodeBytes,
    ulong FileCacheBytes);

/// <summary>
/// The <c>\Memory</c> counters that split the system working set. All are plain gauges, so one
/// collect is enough. Sampling thread only.
/// </summary>
internal sealed class KernelMemory : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _pagedPool;
    private readonly PdhCounter? _nonPagedPool;
    private readonly PdhCounter? _driverCode;
    private readonly PdhCounter? _kernelCode;
    private readonly PdhCounter? _fileCache;

    public KernelMemory()
    {
        _pagedPool = _query.TryAdd(@"\Memory\Pool Paged Resident Bytes");
        _nonPagedPool = _query.TryAdd(@"\Memory\Pool Nonpaged Bytes");
        _driverCode = _query.TryAdd(@"\Memory\System Driver Resident Bytes");
        _kernelCode = _query.TryAdd(@"\Memory\System Code Resident Bytes");
        _fileCache = _query.TryAdd(@"\Memory\System Cache Resident Bytes");
    }

    public KernelReading Read()
    {
        if (!_query.Collect())
            return default;

        static ulong Bytes(PdhCounter? counter) => (ulong)Math.Max(0, counter?.Value ?? 0);

        return new KernelReading(
            Bytes(_pagedPool),
            Bytes(_nonPagedPool),
            Bytes(_driverCode),
            Bytes(_kernelCode),
            Bytes(_fileCache));
    }

    public void Dispose() => _query.Dispose();
}
