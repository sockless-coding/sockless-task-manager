using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Performance.Gpu;

internal sealed record GpuReading(
    double Utilization,
    IReadOnlyDictionary<string, double> EngineTypes,
    double DedicatedUsedBytes,
    double SharedUsedBytes);

/// <summary>
/// GPU counters for one adapter, computed like Task Manager: each engine's utilisation is the
/// sum over processes, an engine type shows its busiest engine, and the overall figure is the
/// busiest engine of any type.
/// </summary>
internal sealed class GpuSensor : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _engines;
    private readonly PdhCounter? _dedicated;
    private readonly PdhCounter? _shared;
    private readonly string _luid;
    private readonly Dictionary<string, double> _instances = [];

    public GpuSensor(string counterLuid)
    {
        _luid = counterLuid;
        _engines = _query.TryAdd(@"\GPU Engine(*)\Utilization Percentage");
        _dedicated = _query.TryAdd(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _shared = _query.TryAdd(@"\GPU Adapter Memory(*)\Shared Usage");
        _query.Collect();
        EngineTypes = ReadEngineTypes();
    }

    /// <summary>Engine types this adapter exposes ("3D", "Copy", "VideoDecode"…).</summary>
    public IReadOnlyList<string> EngineTypes { get; }

    public GpuReading Read()
    {
        _query.Collect();

        var perEngine = new Dictionary<string, (string Type, double Value)>(StringComparer.Ordinal);
        if (_engines is not null)
        {
            _engines.ReadInstances(_instances);
            foreach (var (name, value) in _instances)
            {
                if (!OwnsInstance(name))
                    continue;

                // "pid_1234_luid_…_phys_0_eng_3_engtype_3D": key by everything after the pid.
                var engine = name[name.IndexOf("_luid_", StringComparison.Ordinal)..];
                var current = perEngine.GetValueOrDefault(engine, (EngineType(name), 0));
                perEngine[engine] = (current.Type, current.Value + value);
            }
        }

        var byType = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (type, value) in perEngine.Values)
            byType[type] = Math.Max(byType.GetValueOrDefault(type), Math.Min(value, 100));

        return new GpuReading(
            byType.Count > 0 ? byType.Values.Max() : 0,
            byType,
            SumForAdapter(_dedicated),
            SumForAdapter(_shared));
    }

    /// <summary>
    /// Utilisation is a rate and has no instances until a second collect, so engine types are
    /// discovered from the cumulative "Running Time" counter, which is readable straight away.
    /// </summary>
    private string[] ReadEngineTypes()
    {
        using var query = new PdhQuery();
        var runningTime = query.TryAdd(@"\GPU Engine(*)\Running Time");
        if (runningTime is null || !query.Collect())
            return [];

        return
        [
            .. runningTime.ReadInstanceNames()
                .Where(OwnsInstance)
                .Select(EngineType)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private double SumForAdapter(PdhCounter? counter)
    {
        if (counter is null)
            return 0;

        counter.ReadInstances(_instances);
        return _instances.Where(i => OwnsInstance(i.Key)).Sum(i => i.Value);
    }

    private bool OwnsInstance(string name) => name.Contains(_luid, StringComparison.OrdinalIgnoreCase);

    private static string EngineType(string instance)
    {
        var at = instance.IndexOf("engtype_", StringComparison.Ordinal);
        return at < 0 ? string.Empty : instance[(at + "engtype_".Length)..];
    }

    public void Dispose() => _query.Dispose();
}
