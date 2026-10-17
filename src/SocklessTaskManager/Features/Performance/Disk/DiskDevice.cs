using System.Globalization;
using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance.Disk;

public sealed class DiskDevice : PerformanceDevice
{
    private const double MinimumScaleKb = 100;

    private readonly DiskSensor _sensor;
    private DiskReading? _reading;
    private double _transferMaximum = MinimumScaleKb * 1024;
    private string _transferScale = string.Empty;

    private readonly Readout _activeTime = new("Active time", "SlSeries2");
    private readonly Readout _response = new("Average response time");
    private readonly Readout _readSpeed = new("Read speed", "SlSeries2");
    private readonly Readout _writeSpeed = new("Write speed", "SlSeries3", dashed: true);

    /// <summary>One device per PhysicalDisk counter instance ("0 C:", "1 D: E:").</summary>
    public static IEnumerable<DeviceSlot> Scan() =>
        DiskSensor.Instances().Select(instance =>
            new DeviceSlot($"disk:{instance}", () => new DiskDevice(instance) { Id = $"disk:{instance}" }));

    private DiskDevice(string instance)
    {
        var parts = instance.Split(' ', 2);
        var index = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var letters = parts.Length > 1 ? parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

        var info = DiskInfo.Read(index);
        _sensor = new DiskSensor(instance);
        Readouts = [_activeTime, _response, _readSpeed, _writeSpeed];

        Title = letters.Length > 0 ? $"Disk {index} ({string.Join(' ', letters)})" : $"Disk {index}";
        Subtitle = info.TypeLabel;
        Model = info.Model;

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        Details =
        [
            new("Capacity", info.SizeBytes > 0 ? Format.Bytes(info.SizeBytes) : "—"),
            new("Formatted", FormattedSize(letters) is > 0 and var formatted ? Format.Bytes(formatted) : "—"),
            new("System disk", letters.Contains(systemRoot, StringComparer.OrdinalIgnoreCase) ? "Yes" : "No"),
            new("Type", info.TypeLabel),
        ];
    }

    protected override string AccentKey => "SlSeries2";

    public History ActiveTime { get; } = new();

    public History ReadRate { get; } = new();

    public History WriteRate { get; } = new();

    public override History Sparkline => ActiveTime;

    public override IReadOnlyList<Readout> Readouts { get; }

    public double TransferMaximum
    {
        get => _transferMaximum;
        private set => Set(ref _transferMaximum, value);
    }

    public string TransferScale
    {
        get => _transferScale;
        private set => Set(ref _transferScale, value);
    }

    public override void Sample() => _reading = _sensor.Read();

    public override void Apply()
    {
        if (_reading is not { } r)
            return;

        ActiveTime.Push(r.ActiveTime);
        ReadRate.Push(r.ReadBytesPerSecond);
        WriteRate.Push(r.WriteBytesPerSecond);

        // Scale in KB steps (100 KB/s, 200 KB/s, 500 KB/s…) so the label reads cleanly.
        var peakKb = Math.Max(ReadRate.Max(), WriteRate.Max()) / 1024;
        TransferMaximum = Format.NiceCeiling(peakKb, MinimumScaleKb) * 1024;
        TransferScale = $"{Format.Bytes(TransferMaximum, 0)}/s";

        _activeTime.Value = Format.Percent(r.ActiveTime);
        _response.Value = Format.Milliseconds(r.ResponseMs);
        _readSpeed.Value = Format.BytesPerSecond(r.ReadBytesPerSecond);
        _writeSpeed.Value = Format.BytesPerSecond(r.WriteBytesPerSecond);
        Summary = Format.Percent(r.ActiveTime);
    }

    private static long FormattedSize(string[] letters)
    {
        long total = 0;
        foreach (var letter in letters)
        {
            try
            {
                var drive = new DriveInfo(letter);
                if (drive.IsReady)
                    total += drive.TotalSize;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    protected override Control CreateView() => new DiskView { DataContext = this };

    public override void Dispose() => _sensor.Dispose();
}
