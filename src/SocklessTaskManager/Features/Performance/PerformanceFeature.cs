using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Performance;

/// <summary>
/// The Performance page: a list of devices with sparklines and the selected device's detail.
/// Device kinds are plugged in as <see cref="DeviceScanner"/>s (see <see cref="PerformanceModule"/>).
/// Scanners re-run every few ticks, so hot-plugged disks and adapters come and go.
/// </summary>
public sealed class PerformanceFeature : Feature, ISampled
{
    private const int RescanEveryTicks = 5;

    private readonly IReadOnlyList<DeviceScanner> _scanners;
    private readonly List<DeviceSlot>[] _lastScan;

    // Owned by the sampling thread: the devices in display order.
    private List<PerformanceDevice> _sampling = [];
    private List<PerformanceDevice>? _pendingOrder;
    private int _tick;

    private PerformanceDevice? _selectedDevice;

    public PerformanceFeature(SampleClock clock, IReadOnlyList<DeviceScanner> scanners)
    {
        _scanners = scanners;
        _lastScan = [.. scanners.Select(_ => new List<DeviceSlot>())];
        clock.Subscribe(this);
    }

    public override string Title => "Performance";

    public override string IconKey => "IconPerformance";

    public ObservableCollection<PerformanceDevice> Devices { get; } = [];

    public PerformanceDevice? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    protected override Control CreateView() => new PerformanceView { DataContext = this };

    void ISampled.Sample()
    {
        if (_tick++ % RescanEveryTicks == 0)
            Rescan();

        foreach (var device in _sampling)
        {
            try
            {
                device.Sample();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Sampling {device.Id} failed: {ex}");
            }
        }
    }

    void ISampled.Apply()
    {
        if (_pendingOrder is { } order)
        {
            _pendingOrder = null;
            Reconcile(order);
        }

        foreach (var device in Devices)
        {
            try
            {
                device.Apply();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Applying {device.Id} failed: {ex}");
            }
        }
    }

    private void Rescan()
    {
        var existing = _sampling.ToDictionary(d => d.Id);
        var order = new List<PerformanceDevice>();

        for (var i = 0; i < _scanners.Count; i++)
        {
            try
            {
                _lastScan[i] = [.. _scanners[i]()];
            }
            catch (Exception ex)
            {
                // Keep what this scanner found last time rather than dropping its devices.
                Debug.WriteLine($"Device scan failed: {ex}");
            }

            foreach (var slot in _lastScan[i])
            {
                if (existing.Remove(slot.Id, out var device))
                {
                    order.Add(device);
                    continue;
                }

                try
                {
                    order.Add(slot.Create());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Creating device {slot.Id} failed: {ex}");
                }
            }
        }

        if (!order.SequenceEqual(_sampling))
        {
            _sampling = order;
            _pendingOrder = order;
        }
    }

    /// <summary>Make <see cref="Devices"/> match <paramref name="order"/> with minimal changes.</summary>
    private void Reconcile(List<PerformanceDevice> order)
    {
        var keep = order.ToHashSet();
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Devices[i]))
            {
                var removed = Devices[i];
                Devices.RemoveAt(i);
                removed.Dispose();
            }
        }

        for (var i = 0; i < order.Count; i++)
        {
            if (i < Devices.Count && ReferenceEquals(Devices[i], order[i]))
                continue;

            var current = Devices.IndexOf(order[i]);
            if (current >= 0)
                Devices.Move(current, i);
            else
                Devices.Insert(i, order[i]);
        }

        if (SelectedDevice is null || !keep.Contains(SelectedDevice))
            SelectedDevice = Devices.FirstOrDefault();
    }

    public override void Dispose()
    {
        foreach (var device in _sampling)
            device.Dispose();
    }
}
