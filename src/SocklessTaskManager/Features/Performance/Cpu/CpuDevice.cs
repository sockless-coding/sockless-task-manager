using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance.Cpu;

public sealed class CpuDevice : PerformanceDevice
{
    private readonly CpuInfo _info;
    private readonly CpuSensor _sensor;
    private CpuReading? _reading;
    private bool _showLogicalProcessors;

    private readonly Readout _utilization = new("Utilization", "SlSeries1");
    private readonly Readout _speed = new("Speed");
    private readonly Readout _processes = new("Processes");
    private readonly Readout _threads = new("Threads");
    private readonly Readout _handles = new("Handles");
    private readonly Readout _uptime = new("Up time");

    public static IEnumerable<DeviceSlot> Scan()
    {
        yield return new DeviceSlot("cpu", () => new CpuDevice { Id = "cpu" });
    }

    private CpuDevice()
    {
        _info = CpuInfo.Read();
        _sensor = new CpuSensor(_info.BaseMhz);
        LogicalProcessors = [.. Enumerable.Range(0, _info.LogicalProcessors).Select(_ => new History())];
        Readouts = [_utilization, _speed, _processes, _threads, _handles, _uptime];

        Title = "CPU";
        Model = _info.Name;
        Details =
        [
            new("Base speed", Format.Gigahertz(_info.BaseMhz)),
            new("Sockets", _info.Sockets.ToString()),
            new("Cores", _info.Cores.ToString()),
            new("Logical processors", _info.LogicalProcessors.ToString()),
            new("Virtualization", _info.VirtualizationEnabled ? "Enabled" : "Disabled"),
            new("L1 cache", Format.Bytes(_info.L1Bytes)),
            new("L2 cache", Format.Bytes(_info.L2Bytes)),
            new("L3 cache", Format.Bytes(_info.L3Bytes)),
        ];
    }

    protected override string AccentKey => "SlSeries1";

    public History Utilization { get; } = new();

    public override History Sparkline => Utilization;

    public IReadOnlyList<History> LogicalProcessors { get; }

    public override IReadOnlyList<Readout> Readouts { get; }

    /// <summary>Overall graph, or one small graph per logical processor.</summary>
    public bool ShowLogicalProcessors
    {
        get => _showLogicalProcessors;
        set => Set(ref _showLogicalProcessors, value);
    }

    public override void Sample() => _reading = _sensor.Read();

    public override void Apply()
    {
        if (_reading is not { } r)
            return;

        Utilization.Push(r.Utilization);
        for (var i = 0; i < Math.Min(r.PerProcessor.Length, LogicalProcessors.Count); i++)
            LogicalProcessors[i].Push(r.PerProcessor[i]);

        _utilization.Value = Format.Percent(r.Utilization);
        _speed.Value = Format.Gigahertz(r.SpeedMhz);
        _processes.Value = Format.Number(r.Processes);
        _threads.Value = Format.Number(r.Threads);
        _handles.Value = Format.Number(r.Handles);
        _uptime.Value = Format.Uptime(r.Uptime);
        Summary = $"{Format.Percent(r.Utilization)}  {Format.Gigahertz(r.SpeedMhz)}";
    }

    protected override Control CreateView() => new CpuView { DataContext = this };

    public override void Dispose() => _sensor.Dispose();
}
