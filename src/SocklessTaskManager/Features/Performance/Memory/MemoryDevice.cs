using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance.Memory;

public sealed class MemoryDevice : PerformanceDevice
{
    private MemoryReading? _reading;
    private double _maximumGb = 1;
    private string _scale = string.Empty;

    private readonly Readout _inUse = new("In use", "SlSeries3");
    private readonly Readout _available = new("Available");
    private readonly Readout _committed = new("Committed");
    private readonly Readout _cached = new("Cached");
    private readonly Readout _pagedPool = new("Paged pool");
    private readonly Readout _nonPagedPool = new("Non-paged pool");

    public static IEnumerable<DeviceSlot> Scan()
    {
        yield return new DeviceSlot("memory", () => new MemoryDevice { Id = "memory" });
    }

    private MemoryDevice()
    {
        var info = MemoryInfo.Read();
        var first = MemorySensor.Read();
        Readouts = [_inUse, _available, _committed, _cached, _pagedPool, _nonPagedPool];

        Title = "Memory";
        Model = $"{Format.Bytes(info.InstalledBytes)} {info.Type}".Trim();

        var details = new List<Detail>();
        if (info.SpeedMts > 0)
            details.Add(new("Speed", $"{info.SpeedMts} MT/s"));
        if (info.SlotsTotal > 0)
            details.Add(new("Slots used", $"{info.SlotsUsed} of {info.SlotsTotal}"));
        if (info.FormFactor.Length > 0)
            details.Add(new("Form factor", info.FormFactor));
        if (info.InstalledBytes > first.TotalBytes)
            details.Add(new("Hardware reserved", Format.Bytes(info.InstalledBytes - first.TotalBytes)));
        Details = details;
    }

    protected override string AccentKey => "SlSeries3";

    /// <summary>Memory in use, in GB.</summary>
    public History InUse { get; } = new();

    public override History Sparkline => InUse;

    public override IReadOnlyList<Readout> Readouts { get; }

    public double MaximumGb
    {
        get => _maximumGb;
        private set => Set(ref _maximumGb, value);
    }

    public string Scale
    {
        get => _scale;
        private set => Set(ref _scale, value);
    }

    public override void Sample() => _reading = MemorySensor.Read();

    public override void Apply()
    {
        if (_reading is not { } r)
            return;

        const double gb = 1024d * 1024 * 1024;
        MaximumGb = r.TotalBytes / gb;
        SparklineMaximum = MaximumGb;
        Scale = Format.Bytes(r.TotalBytes);
        InUse.Push(r.InUseBytes / gb);

        var percent = 100d * r.InUseBytes / r.TotalBytes;
        _inUse.Value = Format.Bytes(r.InUseBytes);
        _available.Value = Format.Bytes(r.AvailableBytes);
        _committed.Value = $"{Format.Bytes(r.CommittedBytes)} / {Format.Bytes(r.CommitLimitBytes)}";
        _cached.Value = Format.Bytes(r.CachedBytes);
        _pagedPool.Value = Format.Bytes(r.PagedPoolBytes);
        _nonPagedPool.Value = Format.Bytes(r.NonPagedPoolBytes);
        Summary = $"{(r.InUseBytes / gb):F1}/{MaximumGb:F1} GB ({Format.Percent(percent)})";
    }

    protected override Control CreateView() => new MemoryView { DataContext = this };
}
