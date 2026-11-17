using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// The Drivers page: every device and its driver, with Device Manager's problems in words; the
/// kernel modules loaded now, with their signatures; driver updates from Windows Update, and
/// drivers installed from an INF file; a history of device and driver changes; and the crashes
/// drivers caused. Device changes are followed from the moment the app starts (the history covers
/// the session), but only Windows' own notifications drive that: nothing runs per tick.
/// </summary>
public sealed class DriversFeature : Feature
{
    private readonly DriverMonitor _monitor = new();

    public DriversFeature() => List = new DriverList(_monitor);

    public override string Title => "Drivers";

    public override string IconKey => "IconDrivers";

    public DriverList List { get; }

    protected override Control CreateView() => new DriversView { DataContext = List };

    public override void Dispose() => _monitor.Dispose();
}
