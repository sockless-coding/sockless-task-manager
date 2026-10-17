using System.Globalization;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Performance.Cpu;

internal sealed record CpuReading(
    double Utilization,
    double[] PerProcessor,
    double SpeedMhz,
    uint Processes,
    uint Threads,
    uint Handles,
    TimeSpan Uptime);

/// <summary>
/// Reads utilisation the way Task Manager does: "% Processor Utility" (frequency-aware,
/// so it matches Task Manager's number) and "% Processor Performance" for the live speed.
/// Falls back to "% Processor Time" on systems without the utility counter.
/// </summary>
internal sealed class CpuSensor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _total;
    private readonly PdhCounter? _perProcessor;
    private readonly PdhCounter? _performance;
    private readonly Dictionary<string, double> _instances = [];
    private readonly double _baseMhz;

    public CpuSensor(double baseMhz)
    {
        _baseMhz = baseMhz;
        _total = _query.TryAdd(@"\Processor Information(_Total)\% Processor Utility")
                 ?? _query.TryAdd(@"\Processor Information(_Total)\% Processor Time");
        _perProcessor = _query.TryAdd(@"\Processor Information(*)\% Processor Utility")
                        ?? _query.TryAdd(@"\Processor Information(*)\% Processor Time");
        _performance = _query.TryAdd(@"\Processor Information(_Total)\% Processor Performance");
        _query.Collect();
    }

    public CpuReading Read()
    {
        _query.Collect();

        var perProcessor = Array.Empty<double>();
        if (_perProcessor is not null)
        {
            _perProcessor.ReadInstances(_instances);
            // Instances are "group,index" plus "_Total" rows per group; order by group then index.
            perProcessor = _instances
                .Where(i => !i.Key.Contains("_Total", StringComparison.Ordinal))
                .Select(i => (Key: ParseInstance(i.Key), i.Value))
                .OrderBy(i => i.Key)
                .Select(i => Math.Clamp(i.Value, 0, 100))
                .ToArray();
        }

        var performance = _performance?.Value;
        var system = SystemPerformance.Read();

        return new CpuReading(
            Math.Clamp(_total?.Value ?? 0, 0, 100),
            perProcessor,
            performance is { } p ? _baseMhz * p / 100 : _baseMhz,
            system.ProcessCount,
            system.ThreadCount,
            system.HandleCount,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static (int Group, int Index) ParseInstance(string name)
    {
        var comma = name.IndexOf(',');
        return comma > 0
               && int.TryParse(name.AsSpan(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
               && int.TryParse(name.AsSpan(comma + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? (group, index)
            : (int.MaxValue, int.MaxValue);
    }

    public void Dispose() => _query.Dispose();
}
