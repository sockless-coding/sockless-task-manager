using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Performance.Disk;

internal sealed record DiskReading(double ActiveTime, double ReadBytesPerSecond, double WriteBytesPerSecond, double ResponseMs);

/// <summary>PhysicalDisk counters for one disk instance ("0 C:").</summary>
internal sealed class DiskSensor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _idle;
    private readonly PdhCounter? _read;
    private readonly PdhCounter? _write;
    private readonly PdhCounter? _transfer;

    public DiskSensor(string instance)
    {
        _idle = _query.TryAdd($@"\PhysicalDisk({instance})\% Idle Time");
        _read = _query.TryAdd($@"\PhysicalDisk({instance})\Disk Read Bytes/sec");
        _write = _query.TryAdd($@"\PhysicalDisk({instance})\Disk Write Bytes/sec");
        _transfer = _query.TryAdd($@"\PhysicalDisk({instance})\Avg. Disk sec/Transfer");
        _query.Collect();
    }

    /// <summary>The PhysicalDisk instances present now, without "_Total".</summary>
    public static IReadOnlyList<string> Instances()
    {
        using var query = new PdhQuery();
        var counter = query.TryAdd(@"\PhysicalDisk(*)\Current Disk Queue Length");
        if (counter is null || !query.Collect())
            return [];

        return [.. counter.ReadInstanceNames().Where(n => n != "_Total").Order(StringComparer.Ordinal)];
    }

    public DiskReading Read()
    {
        _query.Collect();
        return new DiskReading(
            // Task Manager's "active time" is the complement of idle time.
            Math.Clamp(100 - (_idle?.Value ?? 100), 0, 100),
            _read?.Value ?? 0,
            _write?.Value ?? 0,
            (_transfer?.Value ?? 0) * 1000);
    }

    public void Dispose() => _query.Dispose();
}
