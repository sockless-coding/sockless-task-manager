using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// The Apps page: everything installed, however it was installed. Desktop programs from setup
/// programs and Windows Installer (for everyone and for this user, 64- and 32-bit), Store and
/// MSIX packages, apps from winget, Scoop and Chocolatey, and Windows' optional features. Each is
/// uninstalled, changed or repaired the way it was installed; entries left behind by programs
/// deleted by hand can be removed. Scans when opened, and follows the uninstall registry while
/// on screen; costs nothing per tick.
/// </summary>
public sealed class AppsFeature(Navigator navigator) : Feature
{
    public override string Title => "Apps";

    public override string IconKey => "IconApps";

    public AppList List { get; } = new(navigator);

    protected override Control CreateView() => new AppsView { DataContext = List };

    public override void Dispose() => List.Dispose();
}
