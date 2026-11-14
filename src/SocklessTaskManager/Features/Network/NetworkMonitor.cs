using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// The record of connections, kept on the sampling thread. Each tick it reads the TCP and UDP
/// tables and, elevated, drains the trace; connections it hasn't seen are added, ones that are gone
/// are closed but kept, and only what changed goes into the snapshot for the UI thread. The record
/// holds up to <see cref="MaxRecorded"/> connections; past that the oldest closed ones go first.
/// </summary>
internal sealed partial class NetworkMonitor : IDisposable
{
    public const int MaxRecorded = 50_000;

    /// <summary>A UDP conversation with no traffic for this long has ended.</summary>
    private const double UdpIdleSeconds = 30;

    /// <summary>Closed connections stay matchable this long, for late trace events and connections that reappear.</summary>
    private static readonly TimeSpan Reopenable = TimeSpan.FromSeconds(10);

    /// <summary>Services, the DNS cache and local addresses change rarely; read them this often.</summary>
    private const long SlowRefreshMilliseconds = 15_000;

    /// <summary>Retry a trace that stopped (another tool took the session) no more often than this.</summary>
    private const long TraceRetryMilliseconds = 30_000;

    private const uint QueryLimitedInformation = 0x1000;

    private readonly HostNames _hosts;
    private readonly SocketTable _table = new();
    private byte[] _processBuffer = NtSystem.Buffer(1024 * 1024);
    private NetworkTrace? _trace;
    private long _traceRetryAt;

    private readonly Dictionary<FlowKey, Flow> _open = [];
    private readonly Dictionary<FlowKey, Flow> _closed = [];
    private readonly Queue<Flow> _log = new();
    private readonly HashSet<Flow> _dirty = [];
    private readonly List<long> _removed = [];

    private Dictionary<int, (ProcessRef Key, string Name)> _running = [];
    private readonly Dictionary<int, (ProcessRef Key, string Name)> _lastKnown = [];
    private readonly Dictionary<ProcessRef, ProcessEntry> _processes = [];
    private readonly List<ProcessEntry> _processDirty = [];
    private Dictionary<int, List<string>> _services = [];
    private HashSet<NetAddress> _localAddresses = [];
    private long _slowRefreshAt;
    private long _lastTimestamp;
    private long _nextId;

    public NetworkMonitor(HostNames hosts)
    {
        _hosts = hosts;
    }

    /// <summary>Whether the trace is running: traffic, short connections and DNS lookups are seen.</summary>
    public bool IsTracing => _trace is { IsRunning: true };

    /// <summary>Starts or stops the trace. Elevated only; a no-op otherwise. Sampling thread.</summary>
    public void SetTracing(bool on)
    {
        if (!on)
        {
            _trace?.Dispose();
            _trace = null;
            return;
        }

        if (_trace is { IsRunning: false })
        {
            _trace.Dispose();
            _trace = null;
            _traceRetryAt = Environment.TickCount64 + TraceRetryMilliseconds;
        }

        if (_trace is null && Environment.IsPrivilegedProcess && Environment.TickCount64 >= _traceRetryAt)
        {
            _trace = NetworkTrace.TryStart();
            if (_trace is null)
                _traceRetryAt = Environment.TickCount64 + TraceRetryMilliseconds;
        }
    }

    /// <summary>
    /// One tick. <paramref name="record"/>, if set, says which processes (by image name) new
    /// connections are recorded for; connections already in the record are always kept up to date.
    /// </summary>
    public NetworkSnapshot Sample(Func<string, bool>? record)
    {
        var now = DateTime.UtcNow;
        var timestamp = Stopwatch.GetTimestamp();
        var seconds = _lastTimestamp == 0 ? 1 : Math.Max(0.1, Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds);
        _lastTimestamp = timestamp;

        ReadProcesses();
        if (Environment.TickCount64 >= _slowRefreshAt)
        {
            _slowRefreshAt = Environment.TickCount64 + SlowRefreshMilliseconds;
            RefreshSlow();
        }

        var tick = new HashSet<Flow>();
        var listeners = ReadTable(now, record, tick);
        if (_trace is { } trace)
            ReadTrace(trace, now, record, tick);

        CloseMissing(now, tick, seconds);
        Evict();
        MarkEnded();

        var rates = new Dictionary<ProcessRef, (double Send, double Receive)>();
        double sendRate = 0, receiveRate = 0;
        foreach (var flow in _open.Values)
        {
            if (flow.SendRate == 0 && flow.ReceiveRate == 0)
                continue;
            var r = rates.GetValueOrDefault(flow.Process);
            rates[flow.Process] = (r.Send + flow.SendRate, r.Receive + flow.ReceiveRate);
            sendRate += flow.SendRate;
            receiveRate += flow.ReceiveRate;
        }

        var changed = new List<ConnectionInfo>(_dirty.Count);
        foreach (var flow in _dirty)
            changed.Add(flow.ToInfo());
        _dirty.Clear();

        var processes = _processDirty.Select(p => p.ToInfo()).ToList();
        _processDirty.Clear();
        var removed = _removed.ToList();
        _removed.Clear();

        return new NetworkSnapshot(changed, removed, listeners, processes, rates, sendRate, receiveRate, IsTracing);
    }

    /// <summary>Drops every closed connection from the record; open ones stay. Sampling thread.</summary>
    public void ClearClosed()
    {
        var open = _log.Where(f => f.IsOpen).ToList();
        foreach (var flow in _log)
        {
            if (flow.IsOpen)
                continue;
            _removed.Add(flow.Id);
            _dirty.Remove(flow);
        }

        _log.Clear();
        foreach (var flow in open)
            _log.Enqueue(flow);
        _closed.Clear();
    }

    // ---------- Socket table ----------

    private List<ListenerInfo> ReadTable(DateTime now, Func<string, bool>? record, HashSet<Flow> tick)
    {
        var rows = _table.Read();
        var listeners = new List<ListenerInfo>();
        var listening = new HashSet<(int, ushort)>();
        foreach (var row in rows)
        {
            if (row.Protocol == Protocol.Tcp && !IsListening(row))
                continue;

            var process = Resolve(row.ProcessId);
            Entry(process);
            listeners.Add(new ListenerInfo(row.Protocol, row.LocalAddress, row.LocalPort, process,
                row.CreateTime > 0 ? DateTime.FromFileTimeUtc(row.CreateTime) : null));
            if (row.Protocol == Protocol.Tcp)
                listening.Add((row.ProcessId, row.LocalPort));
        }

        _listening = listening;
        foreach (var row in rows)
        {
            if (row.Protocol != Protocol.Tcp || IsListening(row))
                continue;
            if (row.LocalAddress.Scope != AddressScope.Any)
                _localAddresses.Add(row.LocalAddress);

            var key = new FlowKey(Protocol.Tcp, row.LocalAddress, row.LocalPort, row.RemoteAddress, row.RemotePort);
            if (_open.TryGetValue(key, out var flow) && row.ProcessId != 0 && flow.Process.Id != 0 && flow.Process.Id != row.ProcessId)
            {
                // The same endpoints, another process: the port was reused.
                Close(flow);
                flow = null;
            }

            if (flow is null && _closed.Remove(key, out var recent) && (row.ProcessId == 0 || recent.Process.Id == row.ProcessId))
            {
                // Closed a moment ago (seen by the trace before the table had it): it's still the same one.
                flow = recent;
                flow.IsOpen = true;
                flow.ShortLived = false;
                _open[key] = flow;
                Dirty(flow);
            }

            if (flow is null)
            {
                // A connection in TIME_WAIT that was never seen open belongs to nobody any more: nothing to learn from it.
                if (row.ProcessId == 0)
                    continue;

                var process = Resolve(row.ProcessId);
                if (record is not null && !record(Entry(process).Name))
                    continue;

                flow = Add(key, process, now);
                flow.IsInbound = listening.Contains((row.ProcessId, row.LocalPort));
                if (row.CreateTime > 0)
                    flow.Opened = DateTime.FromFileTimeUtc(row.CreateTime);
            }

            if (flow.State != row.State)
            {
                flow.State = row.State;
                Dirty(flow);
            }

            flow.LastSeen = now;
            flow.TraceOnly = false;
            tick.Add(flow);
        }

        return listeners;
    }

    private HashSet<(int, ushort)> _listening = [];

    /// <summary>LISTEN, or (where Windows reports no state) a socket with no remote end.</summary>
    private static bool IsListening(SocketEntry row) =>
        row.State == TcpState.Listen
        || row.State == TcpState.None && row.RemotePort == 0 && row.RemoteAddress.Scope == AddressScope.Any;

    // ---------- Trace ----------

    private void ReadTrace(NetworkTrace trace, DateTime now, Func<string, bool>? record, HashSet<Flow> tick)
    {
        while (trace.TryDequeue(out TracedStart start))
        {
            if (start.Name is { } name)
                _lastKnown[start.Key.Id] = (start.Key, name);
        }

        while (trace.TryDequeue(out DnsAnswer answer))
        {
            foreach (var address in answer.Addresses)
                _hosts.Add(answer.Name, address);
        }

        foreach (var (raw, activity) in trace.Drain())
        {
            var key = Orient(raw);
            var flow = _open.GetValueOrDefault(key);
            if (flow is null && _closed.Remove(key, out var recent))
            {
                flow = recent;
                if (raw.Protocol == Protocol.Udp || !activity.Disconnected)
                {
                    flow.IsOpen = true;
                    _open[key] = flow;
                    Dirty(flow);
                }
                else
                {
                    _closed[key] = flow;
                }
            }

            if (flow is null)
            {
                // A disconnect or retransmit of a connection never seen tells nothing new.
                if (activity is { Sent: 0, Received: 0, Connected: false, Accepted: false })
                    continue;

                var process = Resolve(raw.ProcessId);
                if (record is not null && !record(Entry(process).Name))
                    continue;

                flow = Add(key, process, now);
                flow.IsInbound = activity.Accepted || _listening.Contains((raw.ProcessId, key.LocalPort))
                                 || raw.Protocol == Protocol.Udp && activity is { Sent: 0, Received: > 0 };
                flow.TraceOnly = true;
            }
            else if (flow.Process.Id == 0 && raw.ProcessId > 0)
            {
                flow.Process = Resolve(raw.ProcessId);
                Dirty(flow);
            }

            if (activity.Sent > 0 || activity.Received > 0 || activity.Retransmits > 0)
            {
                flow.Sent += activity.Sent;
                flow.Received += activity.Received;
                if (flow.IsOpen)
                {
                    // Late events for a closed connection count towards its total, not a rate.
                    flow.TickSent += activity.Sent;
                    flow.TickReceived += activity.Received;
                }

                flow.Retransmits += activity.Retransmits;
                Dirty(flow);
            }

            flow.LastSeen = now;
            if (activity.Disconnected)
                flow.Disconnected = true;
            if (raw.Protocol == Protocol.Udp)
                tick.Add(flow);
        }
    }

    /// <summary>
    /// Which end is ours. A connection already known decides it; otherwise the end with one of this
    /// computer's addresses is local, and the source if both are (loopback).
    /// </summary>
    private FlowKey Orient(RawFlow raw)
    {
        var forward = new FlowKey(raw.Protocol, raw.Source, raw.SourcePort, raw.Destination, raw.DestinationPort);
        if (_open.ContainsKey(forward) || _closed.ContainsKey(forward))
            return forward;

        var reverse = forward.Reversed;
        if (_open.ContainsKey(reverse) || _closed.ContainsKey(reverse))
            return reverse;

        return !IsLocal(raw.Source) && IsLocal(raw.Destination) ? reverse : forward;
    }

    private bool IsLocal(NetAddress address) => address.Scope == AddressScope.Loopback || _localAddresses.Contains(address);

    // ---------- Closing, rates and the record's limit ----------

    private void CloseMissing(DateTime now, HashSet<Flow> tick, double seconds)
    {
        List<Flow>? closing = null;
        foreach (var flow in _open.Values)
        {
            var open = flow.Protocol == Protocol.Tcp
                ? tick.Contains(flow)
                : (now - flow.LastSeen).TotalSeconds < UdpIdleSeconds;

            var sendRate = flow.TickSent / seconds;
            var receiveRate = flow.TickReceived / seconds;
            flow.TickSent = flow.TickReceived = 0;
            if (sendRate != flow.SendRate || receiveRate != flow.ReceiveRate)
            {
                flow.SendRate = sendRate;
                flow.ReceiveRate = receiveRate;
                Dirty(flow);
            }

            if (!open)
                (closing ??= []).Add(flow);
        }

        if (closing is not null)
        {
            foreach (var flow in closing)
            {
                flow.ShortLived = flow.TraceOnly && flow.Protocol == Protocol.Tcp;
                Close(flow);
            }
        }

        if (_closed.Count > 0)
        {
            foreach (var (key, flow) in _closed.ToList())
            {
                if (now - flow.LastSeen > Reopenable)
                    _closed.Remove(key);
            }
        }
    }

    private void Close(Flow flow)
    {
        _open.Remove(flow.Key);
        flow.IsOpen = false;
        flow.SendRate = flow.ReceiveRate = 0;
        _closed[flow.Key] = flow;
        Dirty(flow);
    }

    private void Evict()
    {
        var turns = _log.Count;
        while (_log.Count > MaxRecorded && turns-- > 0)
        {
            var oldest = _log.Dequeue();
            if (oldest.IsOpen)
            {
                _log.Enqueue(oldest);
                continue;
            }

            _closed.Remove(oldest.Key);
            _dirty.Remove(oldest);
            _removed.Add(oldest.Id);
        }
    }

    private Flow Add(FlowKey key, ProcessRef process, DateTime now)
    {
        var flow = new Flow(++_nextId, key, process, now);
        _open[key] = flow;
        _log.Enqueue(flow);
        Dirty(flow);
        Entry(process);
        return flow;
    }

    private void Dirty(Flow flow) => _dirty.Add(flow);

    // ---------- Processes ----------

    private void ReadProcesses()
    {
        var running = new Dictionary<int, (ProcessRef, string)>(_running.Count + 16);
        foreach (var p in SystemProcesses.Read(ref _processBuffer))
        {
            var entry = (new ProcessRef(p.Id, p.CreateTime), p.Name);
            running[p.Id] = entry;
            _lastKnown[p.Id] = entry;
        }

        _running = running;
        if (_lastKnown.Count > 4 * running.Count + 1000)
        {
            foreach (var id in _lastKnown.Keys.Where(id => !running.ContainsKey(id)).ToList())
                _lastKnown.Remove(id);
        }
    }

    /// <summary>The process with this id now, or the last one seen with it.</summary>
    private ProcessRef Resolve(int id)
    {
        if (_running.TryGetValue(id, out var running))
            return running.Key;
        if (_lastKnown.TryGetValue(id, out var known))
            return known.Key;
        return new ProcessRef(id, 0);
    }

    private ProcessEntry Entry(ProcessRef key)
    {
        if (_processes.TryGetValue(key, out var entry))
            return entry;

        var name = key.Id switch
        {
            0 => "System Idle Process",
            _ when _running.TryGetValue(key.Id, out var r) && r.Key == key => r.Name,
            _ when _lastKnown.TryGetValue(key.Id, out var k) && k.Key == key => k.Name,
            _ => $"Process {key.Id}",
        };

        var isRunning = _running.TryGetValue(key.Id, out var now) && now.Key == key;
        entry = new ProcessEntry(key, name, isRunning ? ImagePath(key.Id) : null)
        {
            Services = isRunning ? _services.GetValueOrDefault(key.Id) ?? [] : [],
            HasEnded = !isRunning,
        };
        _processes[key] = entry;
        _processDirty.Add(entry);
        return entry;
    }

    private void MarkEnded()
    {
        foreach (var entry in _processes.Values)
        {
            if (entry.HasEnded || _running.TryGetValue(entry.Key.Id, out var now) && now.Key == entry.Key)
                continue;
            entry.HasEnded = true;
            _processDirty.Add(entry);
        }
    }

    private void RefreshSlow()
    {
        _services = ServiceNames.Read();
        foreach (var entry in _processes.Values)
        {
            if (entry.HasEnded)
                continue;
            var services = _services.GetValueOrDefault(entry.Key.Id) ?? [];
            if (!services.SequenceEqual(entry.Services))
            {
                entry.Services = services;
                _processDirty.Add(entry);
            }
        }

        foreach (var (name, address) in DnsCache.Read())
            _hosts.Add(name, address);

        var local = new HashSet<NetAddress>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    local.Add(NetAddress.From(unicast.Address));
            }
        }
        catch (NetworkInformationException)
        {
        }

        _localAddresses = local;
    }

    private static unsafe string? ImagePath(int id)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)id);
        if (process == 0)
            return null;

        try
        {
            var buffer = stackalloc char[1024];
            var length = 1024u;
            return QueryFullProcessImageName(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public void Dispose()
    {
        _trace?.Dispose();
        _trace = null;
    }

    private sealed class ProcessEntry(ProcessRef key, string name, string? path)
    {
        public ProcessRef Key { get; } = key;

        public string Name { get; } = name;

        public string? Path { get; } = path;

        public IReadOnlyList<string> Services { get; set; } = [];

        public bool HasEnded { get; set; }

        public ProcessInfo ToInfo() => new(Key, Name, Path, Services, HasEnded);
    }

    private sealed class Flow(long id, FlowKey key, ProcessRef process, DateTime now)
    {
        public long Id { get; } = id;

        public FlowKey Key { get; } = key;

        public Protocol Protocol => Key.Protocol;

        public ProcessRef Process { get; set; } = process;

        public TcpState State { get; set; }

        public bool IsOpen { get; set; } = true;

        public bool IsInbound { get; set; }

        public DateTime? Opened { get; set; }

        public DateTime FirstSeen { get; } = now;

        public DateTime LastSeen { get; set; } = now;

        public long Sent { get; set; }

        public long Received { get; set; }

        public long TickSent { get; set; }

        public long TickReceived { get; set; }

        public double SendRate { get; set; }

        public double ReceiveRate { get; set; }

        public int Retransmits { get; set; }

        /// <summary>Seen only by the trace so far, never in the table.</summary>
        public bool TraceOnly { get; set; }

        public bool Disconnected { get; set; }

        /// <summary>Opened and closed between two samples: only the trace saw it.</summary>
        public bool ShortLived { get; set; }

        public ConnectionInfo ToInfo() => new(
            Id, Key, Process, State, IsOpen, IsInbound, Opened, FirstSeen, LastSeen,
            Sent, Received, SendRate, ReceiveRate, Retransmits, ShortLived);
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
