using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Services;

/// <summary>
/// The Services page: every Windows service (and, on request, driver), grouped by who made it —
/// Windows, Microsoft, Bitdefender… — or in one sorted list. A service can be started, stopped,
/// restarted, paused, edited (name, description, startup type, command line, account) and
/// uninstalled from its details, and its process opened on the Processes page. Samples only while
/// the page is on screen.
/// </summary>
public sealed class ServicesFeature : Feature, ISampled
{
    public ServicesFeature(SampleClock clock, Navigator navigator)
    {
        clock.Subscribe(this);
        List = new ServiceList(navigator);
    }

    public override string Title => "Services";

    public override string IconKey => "IconServices";

    public ServiceList List { get; }

    protected override Control CreateView() => new ServicesView { DataContext = List };

    void ISampled.Sample() => List.Sample();

    void ISampled.Apply() => List.Apply();
}
