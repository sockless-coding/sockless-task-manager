namespace Sockless.TaskManager.Features.Network;

/// <summary>One process for its whole life: an id can be reused once it exits.</summary>
/// <param name="CreateTime">FILETIME (UTC); 0 when the process was gone before it could be read.</param>
internal readonly record struct ProcessRef(int Id, long CreateTime);

/// <summary>What the page knows about a process that has touched the network.</summary>
/// <param name="Path">Full executable path, or null where Windows won't say (protected and other users' processes, without elevation).</param>
/// <param name="Services">Display names of the services it hosts (svchost.exe), sorted.</param>
internal sealed record ProcessInfo(ProcessRef Key, string Name, string? Path, IReadOnlyList<string> Services, bool HasEnded)
{
    /// <summary>"svchost.exe" with its services: "DNS Client", "DHCP Client +2".</summary>
    public string? ServiceText => Services.Count switch
    {
        0 => null,
        1 => Services[0],
        _ => $"{Services[0]} +{Services.Count - 1}",
    };
}

/// <summary>A TCP connection's or a UDP conversation's endpoints.</summary>
internal readonly record struct FlowKey(Protocol Protocol, NetAddress Local, ushort LocalPort, NetAddress Remote, ushort RemotePort)
{
    public FlowKey Reversed => new(Protocol, Remote, RemotePort, Local, LocalPort);
}

/// <summary>
/// One connection as recorded, at one moment. Immutable: the monitor publishes a new one when
/// something about it changes.
/// </summary>
/// <param name="Id">Unique for the session; the same connection keeps it.</param>
/// <param name="State">TCP state as last seen; <see cref="TcpState.None"/> for UDP, or where Windows didn't say.</param>
/// <param name="IsOpen">Still in the TCP table, or a UDP conversation with traffic in the last 30 seconds.</param>
/// <param name="IsInbound">Someone connected to a port this process listens on.</param>
/// <param name="Opened">When Windows created the socket, if it said.</param>
/// <param name="FirstSeen">When this app first saw it.</param>
/// <param name="LastSeen">When this app last saw it open or carrying data.</param>
/// <param name="Sent">Bytes sent since first seen; only counted while tracing (administrator).</param>
/// <param name="SendRate">Bytes per second over the last sample.</param>
internal sealed record ConnectionInfo(
    long Id,
    FlowKey Key,
    ProcessRef Process,
    TcpState State,
    bool IsOpen,
    bool IsInbound,
    DateTime? Opened,
    DateTime FirstSeen,
    DateTime LastSeen,
    long Sent,
    long Received,
    double SendRate,
    double ReceiveRate,
    int Retransmits,
    bool ShortLived)
{
    public Protocol Protocol => Key.Protocol;
}

/// <summary>A TCP socket waiting for connections, or a bound UDP socket.</summary>
internal sealed record ListenerInfo(Protocol Protocol, NetAddress Address, ushort Port, ProcessRef Process, DateTime? Since);

/// <summary>What changed since the last sample, for the UI thread to merge.</summary>
/// <param name="Changed">Connections that are new or changed since the last snapshot.</param>
/// <param name="Removed">Connections dropped from the record to keep it within its limit.</param>
/// <param name="Listeners">Every listening and bound socket, now.</param>
/// <param name="Processes">Processes that are new or changed (ended, services) since the last snapshot.</param>
/// <param name="Rates">Bytes per second (send, receive) by process over the last sample; empty without tracing.</param>
/// <param name="IsTracing">Traffic, short connections and DNS lookups are being traced (administrator).</param>
internal sealed record NetworkSnapshot(
    IReadOnlyList<ConnectionInfo> Changed,
    IReadOnlyList<long> Removed,
    IReadOnlyList<ListenerInfo> Listeners,
    IReadOnlyList<ProcessInfo> Processes,
    IReadOnlyDictionary<ProcessRef, (double Send, double Receive)> Rates,
    double SendRate,
    double ReceiveRate,
    bool IsTracing);

internal static class TcpStates
{
    /// <summary>The state as a word a person would use.</summary>
    public static string Describe(ConnectionInfo c)
    {
        if (c.Protocol == Protocol.Udp)
            return c.IsOpen ? "Active" : "Ended";
        if (!c.IsOpen)
            return "Closed";

        return c.State switch
        {
            TcpState.SynSent => "Connecting",
            TcpState.SynReceived => "Accepting",
            TcpState.Established => "Connected",
            TcpState.FinWait1 or TcpState.FinWait2 or TcpState.Closing or TcpState.LastAck or TcpState.CloseWait => "Closing",
            TcpState.TimeWait => "Time wait",
            TcpState.Closed => "Closed",
            _ => "Open",
        };
    }

    /// <summary>The state as Windows names it, for the details pane.</summary>
    public static string Technical(TcpState state) => state switch
    {
        TcpState.Closed => "CLOSED",
        TcpState.Listen => "LISTEN",
        TcpState.SynSent => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECEIVED",
        TcpState.Established => "ESTABLISHED",
        TcpState.FinWait1 => "FIN_WAIT_1",
        TcpState.FinWait2 => "FIN_WAIT_2",
        TcpState.CloseWait => "CLOSE_WAIT",
        TcpState.Closing => "CLOSING",
        TcpState.LastAck => "LAST_ACK",
        TcpState.TimeWait => "TIME_WAIT",
        TcpState.DeleteTcb => "DELETE_TCB",
        _ => "Not reported",
    };
}
