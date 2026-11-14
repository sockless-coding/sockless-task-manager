using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Sockless.TaskManager.Features.Network;

internal enum HostSource : byte
{
    /// <summary>A name an app looked up that resolved to the address (DNS cache or trace).</summary>
    Lookup,

    /// <summary>What the address's own PTR record (or the local network) calls it.</summary>
    Reverse,
}

internal sealed record HostName(string Name, HostSource Source);

/// <summary>
/// Names for addresses. A name an app looked up is the most telling (<c>api.example.com</c> rather
/// than a CDN's <c>server-1-2-3-4.fra.r.cloudfront.net</c>), so DNS answers, from the trace or the
/// Windows DNS cache, replace a reverse lookup's. Reverse lookups are optional (they go out on the
/// network), run a few at a time, and are asked once per address. Thread-safe.
/// </summary>
internal sealed class HostNames
{
    private const int MaxNames = 100_000;
    private const int MaxConcurrentLookups = 4;
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<NetAddress, HostName> _names = new();
    private readonly ConcurrentDictionary<NetAddress, byte> _asked = new();
    private readonly SemaphoreSlim _lookups = new(MaxConcurrentLookups);
    private int _version;

    /// <summary>Bumped whenever a name is added or replaced, so the page knows to refresh.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Ask the address's DNS server for names nothing looked up. Off sends nothing.</summary>
    public bool ReverseLookups { get; set; } = true;

    public HostName? Get(NetAddress address) => _names.GetValueOrDefault(address);

    /// <summary>An app's lookup resolved to these addresses.</summary>
    public void Add(string name, NetAddress address)
    {
        if (string.IsNullOrEmpty(name) || _names.Count >= MaxNames && !_names.ContainsKey(address))
            return;

        var entry = new HostName(name, HostSource.Lookup);
        var changed = false;
        _names.AddOrUpdate(address, _ => { changed = true; return entry; }, (_, old) =>
        {
            if (old.Source == HostSource.Lookup && old.Name == name)
                return old;
            changed = true;
            return entry;
        });
        if (changed)
            Interlocked.Increment(ref _version);
    }

    /// <summary>Starts a reverse lookup if one could help and none was tried. Returns at once.</summary>
    public void Request(NetAddress address)
    {
        if (!ReverseLookups || _names.ContainsKey(address))
            return;
        if (address.Scope is AddressScope.Any or AddressScope.Loopback or AddressScope.Multicast or AddressScope.Broadcast)
            return;
        if (!_asked.TryAdd(address, 0))
            return;

        _ = Task.Run(() => ReverseAsync(address));
    }

    private async Task ReverseAsync(NetAddress address)
    {
        await _lookups.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(LookupTimeout);
            var ip = address.ToIPAddress();
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), AddressFamily.Unspecified, timeout.Token).ConfigureAwait(false);
            var name = entry.HostName;
            if (string.IsNullOrEmpty(name) || IPAddress.TryParse(name, out _))
                return;

            if (_names.TryAdd(address, new HostName(name.TrimEnd('.'), HostSource.Reverse)))
                Interlocked.Increment(ref _version);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            // No PTR record, or it took too long: the address stays nameless.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reverse lookup of {address} failed: {ex}");
        }
        finally
        {
            _lookups.Release();
        }
    }
}
