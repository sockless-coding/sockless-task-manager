using System.Diagnostics;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// Follows devices while the app runs: connected and disconnected, new devices, a driver that
/// changed, a problem that appeared or cleared. It reads the device list once at start (in the
/// background), then only the devices Windows says changed, a second after a burst settles. The
/// changes are kept for the session; the Kernel-PnP log (<see cref="DriverHistory"/>) holds the rest.
/// </summary>
internal sealed class DriverMonitor : IDisposable
{
    private const int MaxChanges = 1000;
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceInfo> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DriverChange> _changes = [];
    private readonly Timer _timer;
    private readonly DeviceWatcher _watcher;
    private bool _hasBaseline;
    private bool _disposed;

    public DriverMonitor()
    {
        _timer = new Timer(_ => Flush());
        _watcher = new DeviceWatcher(OnDevice);
        _ = Task.Run(() =>
        {
            try
            {
                var devices = DeviceTree.Read();
                lock (_gate)
                {
                    foreach (var device in devices)
                        _known.TryAdd(device.InstanceId, device);
                    _hasBaseline = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Driver monitor baseline failed: {ex}");
            }
        });
    }

    /// <summary>A device changed and was read again. Raised on a pool thread.</summary>
    public event Action? Changed;

    public bool IsWatching => _watcher.IsWatching;

    /// <summary>Everything seen this session, oldest first.</summary>
    public List<DriverChange> Changes()
    {
        lock (_gate)
            return [.. _changes];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _watcher.Dispose();
        _timer.Dispose();
    }

    private void OnDevice(string instanceId)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending.Add(instanceId);
            _timer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] ids;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
                return;
            ids = [.. _pending];
            _pending.Clear();
        }

        var read = new List<(string Id, DeviceInfo? Device)>(ids.Length);
        foreach (var id in ids)
        {
            try
            {
                read.Add((id, DeviceTree.Read(id)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reading device {id} failed: {ex}");
            }
        }

        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var (id, device) in read)
            {
                _known.TryGetValue(id, out var before);
                if (_hasBaseline)
                {
                    foreach (var change in Compare(now, id, before, device))
                        _changes.Add(change);
                }

                if (device is null)
                    _known.Remove(id);
                else
                    _known[id] = device;
            }

            if (_changes.Count > MaxChanges)
                _changes.RemoveRange(0, _changes.Count - MaxChanges);
        }

        Changed?.Invoke();
    }

    private static IEnumerable<DriverChange> Compare(DateTime now, string id, DeviceInfo? before, DeviceInfo? after)
    {
        var name = after?.Name ?? before?.Name ?? id;
        if (after is null)
        {
            if (before is not null)
                yield return new DriverChange(now, ChangeKind.Removed, id, name, "Windows no longer knows this device", IsLive: true);
            yield break;
        }

        if (before is null)
        {
            yield return new DriverChange(now, after.IsPresent ? ChangeKind.Added : ChangeKind.Disconnected, id, name,
                after.IsPresent ? $"New device: {After(after)}" : "Seen briefly, then gone", IsLive: true);
            yield break;
        }

        if (before.IsPresent != after.IsPresent)
        {
            yield return new DriverChange(now, after.IsPresent ? ChangeKind.Connected : ChangeKind.Disconnected, id, name,
                after.IsPresent ? "Connected" : "Disconnected", IsLive: true);
        }

        if (!string.Equals(before.Version, after.Version, StringComparison.Ordinal) || !string.Equals(before.Inf, after.Inf, StringComparison.OrdinalIgnoreCase))
        {
            yield return new DriverChange(now, ChangeKind.DriverChanged, id, name,
                $"Driver changed from {Describe(before)} to {Describe(after)}", IsLive: true);
        }

        if (after.IsPresent && before.Problem != after.Problem)
        {
            yield return after.Problem == 0
                ? new DriverChange(now, ChangeKind.ProblemCleared, id, name, "Working again", IsLive: true)
                : new DriverChange(now, ChangeKind.Problem, id, name, DriverText.Problem(after.Problem), IsLive: true);
        }
    }

    private static string After(DeviceInfo device) => device.HasDriver ? Describe(device) : "no driver installed";

    private static string Describe(DeviceInfo device) =>
        device.Version is null ? "none" : device.Provider is null ? device.Version : $"{device.Provider} {device.Version}";
}
