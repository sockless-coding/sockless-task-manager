using System.Buffers.Binary;
using System.Collections.Concurrent;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// A flow as the kernel reported it, before it's known which side is local: Kernel-Network's
/// receive events don't say consistently, so <see cref="NetworkMonitor"/> orients them against the
/// socket table.
/// </summary>
internal readonly record struct RawFlow(Protocol Protocol, NetAddress Source, ushort SourcePort, NetAddress Destination, ushort DestinationPort, int ProcessId);

/// <summary>What happened on a flow since the last drain.</summary>
internal struct FlowActivity
{
    public long Sent;
    public long Received;
    public int Retransmits;
    public bool Connected;
    public bool Accepted;
    public bool Disconnected;
}

/// <summary>A DNS answer: the name an app asked for, and what it resolved to.</summary>
internal readonly record struct DnsAnswer(string Name, NetAddress[] Addresses);

/// <summary>A process that started while tracing, so even one that exits within a second has a name.</summary>
internal readonly record struct TracedStart(ProcessRef Key, string? Name);

/// <summary>
/// Everything the socket table can't show, from one real-time ETW session (administrator only):
/// bytes sent and received per connection and UDP conversation, connections too short to be seen
/// by sampling, the names apps looked up (DNS client), and process starts. Microsoft-Windows-Kernel-Network
/// writes one event per send and receive, so the callback only adds to a dictionary under a lock;
/// <see cref="Drain"/> swaps it out once a tick.
/// </summary>
internal sealed class NetworkTrace : EtwSession
{
    private const string SessionName = "Sockless Task Manager network trace";

    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly Guid DnsClient = new("1c95126e-7eea-49a9-a3fe-a378b03ddb4d");
    private const ulong KeywordIpv4 = 0x10;
    private const ulong KeywordIpv6 = 0x20;
    private const ulong DnsKeywordOperational = 0x8000_0000_0000_0000;
    private const ushort DnsQueryCompleted = 3008;

    /// <summary>A flood of traffic to new endpoints shouldn't grow the dictionary without bound between drains.</summary>
    private const int MaxFlowsPerTick = 50_000;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<DnsAnswer> _answers = new();
    private readonly ConcurrentQueue<TracedStart> _starts = new();
    private Dictionary<RawFlow, FlowActivity> _flows = [];
    private Dictionary<RawFlow, FlowActivity> _spare = [];

    private NetworkTrace()
        : base(SessionName, bufferKb: 256)
    {
    }

    /// <summary>Starts the session, or returns null without administrator rights or if ETW refuses.</summary>
    public static NetworkTrace? TryStart()
    {
        if (!Environment.IsPrivilegedProcess)
            return null;

        var trace = new NetworkTrace();
        if (trace.Start(
            [
                new EtwProvider(KernelNetwork, LevelInformation, KeywordIpv4 | KeywordIpv6),
                new EtwProvider(DnsClient, LevelInformation, DnsKeywordOperational),
                new EtwProvider(KernelProcessEvents.Provider, LevelInformation, KernelProcessEvents.KeywordProcess),
            ]))
            return trace;

        trace.Dispose();
        return null;
    }

    /// <summary>Takes what has been traced since the last call. Sampling thread.</summary>
    public Dictionary<RawFlow, FlowActivity> Drain()
    {
        lock (_lock)
        {
            var taken = _flows;
            _spare.Clear();
            _flows = _spare;
            _spare = taken;
            return taken;
        }
    }

    public bool TryDequeue(out DnsAnswer answer) => _answers.TryDequeue(out answer);

    public bool TryDequeue(out TracedStart start) => _starts.TryDequeue(out start);

    protected override void OnEvent(in Guid provider, ushort id, byte version, ReadOnlySpan<byte> data)
    {
        if (provider == KernelNetwork)
            OnNetwork(id, data);
        else if (provider == DnsClient)
            OnDns(id, data);
        else if (provider == KernelProcessEvents.Provider && id == KernelProcessEvents.ProcessStart
                 && KernelProcessEvents.TryReadStart(data, version, out var pid, out var created, out _, out _, out var image))
            _starts.Enqueue(new TracedStart(new ProcessRef(pid, created), image is null ? null : Path.GetFileName(image)));
    }

    /// <summary>
    /// Every event starts PID, size, daddr, saddr, dport, sport; addresses and ports in network byte order.
    /// TCP v4: 10 send, 11 receive, 12 connect, 13 disconnect, 14 retransmit, 15 accept, 16 reconnect;
    /// v6 is 16 more. UDP v4: 42 send, 43 receive; v6: 58, 59.
    /// </summary>
    private void OnNetwork(ushort id, ReadOnlySpan<byte> data)
    {
        var (protocol, v6, kind) = id switch
        {
            >= 10 and <= 16 => (Protocol.Tcp, false, id - 10),
            >= 26 and <= 32 => (Protocol.Tcp, true, id - 26),
            42 or 43 => (Protocol.Udp, false, id - 42),
            58 or 59 => (Protocol.Udp, true, id - 58),
            _ => (Protocol.Tcp, false, -1),
        };
        if (kind < 0)
            return;

        NetAddress destination, source;
        int ports;
        if (v6)
        {
            if (data.Length < 44)
                return;
            destination = NetAddress.FromV6(data[8..]);
            source = NetAddress.FromV6(data[24..]);
            ports = 40;
        }
        else
        {
            if (data.Length < 20)
                return;
            destination = NetAddress.FromV4(data[8..]);
            source = NetAddress.FromV4(data[12..]);
            ports = 16;
        }

        var flow = new RawFlow(
            protocol,
            source, BinaryPrimitives.ReadUInt16BigEndian(data[(ports + 2)..]),
            destination, BinaryPrimitives.ReadUInt16BigEndian(data[ports..]),
            BinaryPrimitives.ReadInt32LittleEndian(data));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

        lock (_lock)
        {
            ref var activity = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_flows, flow);
            if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref activity))
            {
                if (_flows.Count >= MaxFlowsPerTick)
                    return;
                _flows[flow] = default;
                activity = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_flows, flow);
            }

            switch (kind)
            {
                case 0:
                    activity.Sent += size;
                    break;
                case 1:
                    activity.Received += size;
                    break;
                case 2 or 6:
                    activity.Connected = true;
                    break;
                case 3:
                    activity.Disconnected = true;
                    break;
                case 4:
                    activity.Retransmits++;
                    break;
                case 5:
                    activity.Accepted = true;
                    break;
            }
        }
    }

    /// <summary>
    /// 3008, a query completed: QueryName, QueryType (u32), QueryOptions (u64), QueryStatus (u32),
    /// QueryResults ("type:  5 cdn.example.net;93.184.216.34;::ffff:93.184.216.34;").
    /// </summary>
    private void OnDns(ushort id, ReadOnlySpan<byte> data)
    {
        if (id != DnsQueryCompleted)
            return;

        var name = KernelProcessEvents.Utf16(data, out var at);
        if (name is null || data.Length < at + 16)
            return;

        var status = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]);
        if (status != 0)
            return;

        var results = KernelProcessEvents.Utf16(data[(at + 16)..]);
        if (results is null)
            return;

        var addresses = new List<NetAddress>();
        foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (NetAddress.TryParse(part, out var address))
                addresses.Add(address);
        }

        if (addresses.Count > 0)
            _answers.Enqueue(new DnsAnswer(name.TrimEnd('.'), [.. addresses]));
    }
}
