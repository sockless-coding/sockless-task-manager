using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance.Network;

public sealed class NetworkDevice : PerformanceDevice
{
    private const double MinimumScaleKbps = 100;

    private readonly NetworkInterface _adapter;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastSent;
    private long _lastReceived;
    private TimeSpan _lastTime;
    private (double Send, double Receive)? _reading;
    private double _maximum = MinimumScaleKbps * 1000;
    private string _scale = string.Empty;

    private readonly Readout _receive = new("Receive", "SlSeries4");
    private readonly Readout _send = new("Send", "SlSeries3", dashed: true);

    /// <summary>
    /// Adapters that are up, excluding loopback, tunnels and lightweight filter instances.
    /// Windows names a filter bound to an adapter "&lt;adapter&gt;-&lt;filter&gt;-0000"
    /// ("Ethernet-WFP Native MAC Layer LightWeight Filter-0000"); it mirrors the adapter's traffic.
    /// </summary>
    public static IEnumerable<DeviceSlot> Scan()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();

        return adapters
            .Where(n => !adapters.Any(other => other != n && n.Name.StartsWith(other.Name + "-", StringComparison.Ordinal)))
            .Select(n => new DeviceSlot($"net:{n.Id}", () => new NetworkDevice(n) { Id = $"net:{n.Id}" }));
    }

    private NetworkDevice(NetworkInterface adapter)
    {
        _adapter = adapter;
        var stats = adapter.GetIPStatistics();
        (_lastSent, _lastReceived, _lastTime) = (stats.BytesSent, stats.BytesReceived, _clock.Elapsed);
        Readouts = [_receive, _send];

        var connection = adapter.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
            NetworkInterfaceType.Ppp => "PPP",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Cellular",
            var other => other.ToString(),
        };

        Title = adapter.Name;
        Subtitle = string.Equals(adapter.Name, connection, StringComparison.OrdinalIgnoreCase) ? string.Empty : connection;
        Model = adapter.Description;

        var addresses = adapter.GetIPProperties().UnicastAddresses;
        Details =
        [
            new("Adapter name", adapter.Name),
            new("Connection type", connection),
            new("Link speed", adapter.Speed > 0 ? Format.BitsPerSecond(adapter.Speed) : "—"),
            new("IPv4 address", Join(addresses, AddressFamily.InterNetwork)),
            new("IPv6 address", Join(addresses, AddressFamily.InterNetworkV6)),
        ];
    }

    protected override string AccentKey => "SlSeries4";

    /// <summary>Received bits per second.</summary>
    public History Receive { get; } = new();

    /// <summary>Sent bits per second.</summary>
    public History Send { get; } = new();

    public override History Sparkline => Receive;

    public override IReadOnlyList<Readout> Readouts { get; }

    public double Maximum
    {
        get => _maximum;
        private set => Set(ref _maximum, value);
    }

    public string Scale
    {
        get => _scale;
        private set => Set(ref _scale, value);
    }

    public override void Sample()
    {
        var stats = _adapter.GetIPStatistics();
        var now = _clock.Elapsed;
        var seconds = (now - _lastTime).TotalSeconds;
        if (seconds <= 0)
            return;

        // Counters can reset when an adapter reconnects; treat a drop as zero traffic.
        var sent = Math.Max(0, stats.BytesSent - _lastSent);
        var received = Math.Max(0, stats.BytesReceived - _lastReceived);
        (_lastSent, _lastReceived, _lastTime) = (stats.BytesSent, stats.BytesReceived, now);
        _reading = (sent * 8 / seconds, received * 8 / seconds);
    }

    public override void Apply()
    {
        if (_reading is not { } r)
            return;

        Receive.Push(r.Receive);
        Send.Push(r.Send);

        var peakKbps = Math.Max(Receive.Max(), Send.Max()) / 1000;
        Maximum = Format.NiceCeiling(peakKbps, MinimumScaleKbps) * 1000;
        Scale = Format.BitsPerSecond(Maximum);
        SparklineMaximum = Maximum;

        _receive.Value = Format.BitsPerSecond(r.Receive);
        _send.Value = Format.BitsPerSecond(r.Send);
        Summary = Format.BitsPerSecondPair("S", r.Send, "R", r.Receive);
    }

    private static string Join(UnicastIPAddressInformationCollection addresses, AddressFamily family)
    {
        var matches = addresses
            .Where(a => a.Address.AddressFamily == family)
            .Select(a => a.Address.ToString())
            .ToArray();
        return matches.Length > 0 ? string.Join(Environment.NewLine, matches) : "—";
    }

    protected override Control CreateView() => new NetworkView { DataContext = this };
}
