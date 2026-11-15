using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// The Autostart page: everything Windows starts or loads on its own, from the Run keys and Startup
/// folders Task Manager shows to scheduled tasks, services, drivers, shell and browser add-ins, WMI
/// subscriptions and the system hooks only tools like Autoruns list. Each entry's file is checked
/// for a signature; entries can be turned off (undoably) or removed (with a backup). It scans when
/// opened and on Refresh, and costs nothing per tick.
/// </summary>
public sealed class AutostartFeature : Feature
{
    public override string Title => "Autostart";

    public override string IconKey => "IconAutostart";

    public AutostartList List { get; } = new();

    protected override Control CreateView() => new AutostartView { DataContext = List };
}
