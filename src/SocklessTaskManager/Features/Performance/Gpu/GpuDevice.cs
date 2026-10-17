using System.Text;
using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance.Gpu;

public sealed class GpuDevice : PerformanceDevice
{
    private const int EngineGraphs = 4;

    // Task Manager's default engine picks, in its order. Other types fill any remaining slots.
    private static readonly string[] PreferredEngines = ["3D", "Copy", "VideoEncode", "VideoDecode"];

    private readonly GpuAdapter _adapter;
    private readonly GpuSensor _sensor;
    private GpuReading? _reading;

    private readonly Readout _utilization = new("Utilization", "SlSeries1");
    private readonly Readout _dedicated = new("Dedicated GPU memory", "SlSeries4");
    private readonly Readout _shared = new("Shared GPU memory");

    public static IEnumerable<DeviceSlot> Scan() =>
        GpuAdapter.Enumerate().Select(adapter =>
        {
            var id = $"gpu:{adapter.CounterLuid}";
            return new DeviceSlot(id, () => new GpuDevice(adapter) { Id = id });
        });

    private GpuDevice(GpuAdapter adapter)
    {
        _adapter = adapter;
        _sensor = new GpuSensor(adapter.CounterLuid);
        Readouts = [_utilization, _dedicated, _shared];

        Engines =
        [
            .. PreferredEngines.Where(_sensor.EngineTypes.Contains)
                .Concat(_sensor.EngineTypes.Except(PreferredEngines).Order(StringComparer.Ordinal))
                .Take(EngineGraphs)
                .Select(type => new GpuEngine(type, Humanize(type))),
        ];

        Title = $"GPU {adapter.Index}";
        Subtitle = adapter.Name;
        Model = adapter.Name;
        DedicatedScale = Format.Bytes(adapter.DedicatedBytes);

        var details = new List<Detail>
        {
            new("Dedicated memory", Format.Bytes(adapter.DedicatedBytes)),
            new("Shared memory", Format.Bytes(adapter.SharedBytes)),
        };
        if (adapter.DriverVersion.Length > 0)
            details.Add(new("Driver version", adapter.DriverVersion));
        Details = details;
    }

    protected override string AccentKey => "SlSeries1";

    public History Utilization { get; } = new();

    public History DedicatedMemory { get; } = new();

    public double DedicatedMaximum => Math.Max(_adapter.DedicatedBytes, 1);

    public string DedicatedScale { get; }

    public IReadOnlyList<GpuEngine> Engines { get; }

    public override History Sparkline => Utilization;

    public override IReadOnlyList<Readout> Readouts { get; }

    public override void Sample() => _reading = _sensor.Read();

    public override void Apply()
    {
        if (_reading is not { } r)
            return;

        Utilization.Push(r.Utilization);
        DedicatedMemory.Push(r.DedicatedUsedBytes);
        foreach (var engine in Engines)
            engine.Push(r.EngineTypes.GetValueOrDefault(engine.Type));

        _utilization.Value = Format.Percent(r.Utilization);
        _dedicated.Value = $"{Format.Bytes(r.DedicatedUsedBytes)} / {Format.Bytes(_adapter.DedicatedBytes)}";
        _shared.Value = $"{Format.Bytes(r.SharedUsedBytes)} / {Format.Bytes(_adapter.SharedBytes)}";
        Summary = Format.Percent(r.Utilization);
    }

    /// <summary>"VideoDecode" → "Video Decode", "Compute_0" → "Compute 0".</summary>
    private static string Humanize(string type)
    {
        var text = new StringBuilder(type.Length + 4);
        for (var i = 0; i < type.Length; i++)
        {
            var c = type[i];
            if (c == '_')
            {
                text.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && char.IsLower(type[i - 1]))
                text.Append(' ');
            text.Append(c);
        }

        return text.ToString();
    }

    protected override Control CreateView() => new GpuView { DataContext = this };

    public override void Dispose() => _sensor.Dispose();
}

/// <summary>One engine-type graph ("3D", "Copy"…) on the GPU page.</summary>
public sealed class GpuEngine(string type, string name) : ObservableObject
{
    private string _percent = "0%";

    public string Type { get; } = type;

    public string Name { get; } = name;

    public History History { get; } = new();

    public string Percent
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public void Push(double value)
    {
        History.Push(value);
        Percent = Format.Percent(value);
    }
}
