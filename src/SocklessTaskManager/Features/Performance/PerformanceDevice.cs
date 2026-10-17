using Avalonia.Controls;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance;

/// <summary>
/// One entry in the Performance device list (a CPU, a disk, a network adapter…).
/// Each device kind is its own slice under <c>Features/Performance/&lt;Kind&gt;/</c> with a
/// device class deriving from this, a scanner, its native calls and its view.
/// </summary>
public abstract class PerformanceDevice : ObservableObject, IDisposable
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _summary = string.Empty;
    private string _model = string.Empty;
    private IReadOnlyList<Detail> _details = [];
    private Control? _view;
    private IBrush? _accent;
    private double _sparklineMaximum = 100;

    /// <summary>Stable across rescans; a device whose id disappears is removed.</summary>
    public required string Id { get; init; }

    /// <summary>Key of the series brush in <c>Theme/Tokens.axaml</c> (<c>SlSeries1</c>…).</summary>
    protected abstract string AccentKey { get; }

    public IBrush Accent => _accent ??= ThemeResource.Brush(AccentKey);

    /// <summary>The device list sparkline; usually the same series as the main graph.</summary>
    public abstract History Sparkline { get; }

    /// <summary>Top of the sparkline's scale; 100 for percentages.</summary>
    public double SparklineMaximum
    {
        get => _sparklineMaximum;
        protected set => Set(ref _sparklineMaximum, value);
    }

    /// <summary>"CPU", "Disk 0 (C:)", "Ethernet".</summary>
    public string Title
    {
        get => _title;
        protected set => Set(ref _title, value);
    }

    /// <summary>Optional second line in the device list: "SSD (NVMe)", "Wi-Fi".</summary>
    public string Subtitle
    {
        get => _subtitle;
        protected set => Set(ref _subtitle, value);
    }

    /// <summary>The live one-liner in the device list: "12% 3.21 GHz".</summary>
    public string Summary
    {
        get => _summary;
        protected set => Set(ref _summary, value);
    }

    /// <summary>Hardware name shown at the top right of the detail page.</summary>
    public string Model
    {
        get => _model;
        protected set => Set(ref _model, value);
    }

    /// <summary>Live readouts under the graph (large mono values).</summary>
    public abstract IReadOnlyList<Readout> Readouts { get; }

    /// <summary>Mostly-static facts in the key/value table beside the readouts.</summary>
    public IReadOnlyList<Detail> Details
    {
        get => _details;
        protected set => Set(ref _details, value);
    }

    public Control View => _view ??= CreateView();

    /// <summary>Read the hardware. Background thread; store the result, don't raise changes.</summary>
    public abstract void Sample();

    /// <summary>Push the last sample into bindable properties and histories. UI thread.</summary>
    public abstract void Apply();

    protected abstract Control CreateView();

    /// <summary>Accessible name of the device list item.</summary>
    public override string ToString() => Title;

    public virtual void Dispose()
    {
    }
}

/// <summary>A discovered device: its id, and how to create it if it's new.</summary>
public sealed record DeviceSlot(string Id, Func<PerformanceDevice> Create);

/// <summary>Lists the devices of one kind that are present right now. Called every few seconds.</summary>
public delegate IEnumerable<DeviceSlot> DeviceScanner();

/// <summary>
/// A live labelled value. When the value is plotted, <paramref name="seriesKey"/> names the
/// series token so the readout doubles as the graph's legend.
/// </summary>
public sealed class Readout(string label, string? seriesKey = null, bool dashed = false) : ObservableObject
{
    private string _value = "—";
    private IBrush? _series;

    public string Label { get; } = label;

    /// <summary>Legend swatch colour; resolved on first use, which is on the UI thread.</summary>
    public IBrush? Series => seriesKey is null ? null : _series ??= ThemeResource.Brush(seriesKey);

    public bool HasSeries => seriesKey is not null;

    /// <summary>The swatch is dashed to match a secondary series, so colour isn't the only cue.</summary>
    public bool Dashed { get; } = dashed;

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }
}

public sealed record Detail(string Label, string Value);
