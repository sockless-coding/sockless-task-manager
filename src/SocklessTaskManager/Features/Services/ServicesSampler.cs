using System.Collections.Concurrent;

namespace Sockless.TaskManager.Features.Services;

/// <summary>A service as the page shows it: its state now, and its configuration as last read.</summary>
/// <param name="Config">Null where the service won't say (its DACL denies reading it).</param>
internal sealed record ServiceEntry(ServiceStatus Status, ServiceConfig? Config, Publisher Publisher);

internal sealed record ServicesSnapshot(IReadOnlyList<ServiceEntry> Services, bool Drivers);

/// <summary>
/// Reads the service list off the UI thread. The states are one <c>EnumServicesStatusExW</c> call per
/// sample; each service's configuration and publisher are read the first time it's seen, then again
/// a few per sample (so a change made elsewhere shows within half a minute or so), and at once for
/// a service the page asks about.
/// </summary>
internal sealed class ServicesSampler
{
    /// <summary>Configurations re-read per sample once every service has one.</summary>
    private const int RefreshPerSample = 12;

    private readonly ServicePublisher _publishers = new();
    private readonly Dictionary<string, Known> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _requested = new();
    private long _generation;

    /// <summary>Re-read this service's configuration on the next sample. Any thread.</summary>
    public void Refresh(string name) => _requested.Enqueue(name);

    public ServicesSnapshot Sample(bool drivers)
    {
        var statuses = ServiceManager.List(drivers);
        var generation = ++_generation;

        while (_requested.TryDequeue(out var name))
            _known.Remove(name);

        // Oldest reads first, a few per sample.
        var stale = _known.Count >= RefreshPerSample
            ? _known.OrderBy(k => k.Value.ReadAt).Take(RefreshPerSample).Select(k => k.Key).ToList()
            : [];
        foreach (var name in stale)
            _known.Remove(name);

        var entries = new List<ServiceEntry>(statuses.Count);
        foreach (var status in statuses)
        {
            if (!_known.TryGetValue(status.Name, out var known))
            {
                var config = ServiceManager.ReadConfig(status.Name);
                var publisher = config is null
                    ? new Publisher(Publisher.UnknownName, string.Empty, null, false)
                    : _publishers.Of(status.Name, status.Type, config.ImagePath);
                _known[status.Name] = known = new Known(config, publisher, generation);
            }

            known.SeenAt = generation;
            entries.Add(new ServiceEntry(status, known.Config, known.Publisher));
        }

        // Forget services that are gone, so a re-created one is read afresh.
        foreach (var name in _known.Where(k => k.Value.SeenAt != generation).Select(k => k.Key).ToList())
            _known.Remove(name);

        return new ServicesSnapshot(entries, drivers);
    }

    private sealed class Known(ServiceConfig? config, Publisher publisher, long readAt)
    {
        public ServiceConfig? Config { get; } = config;

        public Publisher Publisher { get; } = publisher;

        public long ReadAt { get; } = readAt;

        public long SeenAt { get; set; }
    }
}
