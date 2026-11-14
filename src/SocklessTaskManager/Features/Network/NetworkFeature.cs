using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// The Network page: open connections and listening ports with the processes that own them, where
/// the other ends are (host names; locations and network owners from a database the user adds),
/// and a record of every connection seen while monitoring, with traffic per connection and per
/// process when elevated. Monitors while the page is shown, and in the background for watched
/// processes or everything if the user chooses; otherwise it costs nothing.
/// </summary>
public sealed class NetworkFeature : Feature, ISampled
{
    private readonly HostNames _hosts = new();
    private readonly GeoLocator _geo = new();
    private readonly NetworkMonitor _monitor;
    private NetworkSnapshot? _pending;
    private bool _wasMonitoring;

    public NetworkFeature(SampleClock clock, Navigator navigator)
    {
        _monitor = new NetworkMonitor(_hosts);
        Page = new NetworkPage(navigator, NetworkSettings.Load(), _hosts, _geo);
        clock.Subscribe(this);
    }

    public override string Title => "Network";

    public override string IconKey => "IconGlobe";

    public NetworkPage Page { get; }

    protected override Control CreateView() => new NetworkView { DataContext = Page };

    void ISampled.Sample()
    {
        var monitoring = Page.ShouldMonitor;
        if (monitoring != _wasMonitoring)
        {
            _wasMonitoring = monitoring;
            _monitor.SetTracing(monitoring);
        }

        if (!monitoring)
            return;

        _monitor.SetTracing(true);
        if (Page.TakeClearRequest())
            _monitor.ClearClosed();
        Volatile.Write(ref _pending, _monitor.Sample(Page.RecordFilter));
    }

    void ISampled.Apply()
    {
        if (Interlocked.Exchange(ref _pending, null) is { } snapshot)
            Page.Apply(snapshot);
    }

    public override void Dispose()
    {
        _monitor.Dispose();
        _geo.Dispose();
        base.Dispose();
    }
}
