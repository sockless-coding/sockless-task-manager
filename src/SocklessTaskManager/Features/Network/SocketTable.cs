using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Network;

internal enum Protocol : byte
{
    Tcp,
    Udp,
}

/// <summary>MIB_TCP_STATE, plus <see cref="None"/> for UDP.</summary>
internal enum TcpState : byte
{
    None = 0,
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
}

/// <summary>One row of the TCP or UDP table.</summary>
/// <param name="RemotePort">0 for UDP and for listening sockets.</param>
/// <param name="ProcessId">0 for connections in TIME_WAIT: the process has let go of them.</param>
/// <param name="CreateTime">FILETIME (UTC) the socket was created, or 0 if Windows doesn't say.</param>
internal readonly record struct SocketEntry(
    Protocol Protocol,
    NetAddress LocalAddress,
    ushort LocalPort,
    NetAddress RemoteAddress,
    ushort RemotePort,
    TcpState State,
    int ProcessId,
    long CreateTime);

/// <summary>
/// The system's TCP and UDP tables, IPv4 and IPv6, with the owning process of each socket
/// (<c>GetExtendedTcpTable</c>/<c>GetExtendedUdpTable</c>, the "owner module" classes, which also
/// say when each socket was created). Needs no elevation. Four calls, well under a millisecond each.
/// </summary>
internal sealed unsafe partial class SocketTable
{
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const int TcpTableOwnerModuleAll = 8;
    private const int UdpTableOwnerModule = 2;
    private const uint ErrorInsufficientBuffer = 122;

    // MIB_TCPTABLE_OWNER_MODULE and friends: a count, then rows aligned to 8.
    private const int RowsAt = 8;
    private const int Tcp4Row = 160;
    private const int Tcp6Row = 192;
    private const int Udp4Row = 160;
    private const int Udp6Row = 176;

    private byte[] _buffer = new byte[64 * 1024];

    public List<SocketEntry> Read()
    {
        var entries = new List<SocketEntry>(512);
        ReadTable(Protocol.Tcp, AfInet, entries);
        ReadTable(Protocol.Tcp, AfInet6, entries);
        ReadTable(Protocol.Udp, AfInet, entries);
        ReadTable(Protocol.Udp, AfInet6, entries);
        return entries;
    }

    private void ReadTable(Protocol protocol, uint family, List<SocketEntry> entries)
    {
        uint status;
        var size = (uint)_buffer.Length;
        for (var attempt = 0; ; attempt++)
        {
            fixed (byte* buffer = _buffer)
            {
                status = protocol == Protocol.Tcp
                    ? GetExtendedTcpTable(buffer, ref size, 0, family, TcpTableOwnerModuleAll, 0)
                    : GetExtendedUdpTable(buffer, ref size, 0, family, UdpTableOwnerModule, 0);
            }

            if (status != ErrorInsufficientBuffer || attempt == 3)
                break;
            _buffer = new byte[Math.Max(size + 16 * 1024, (uint)_buffer.Length * 2)];
            size = (uint)_buffer.Length;
        }

        if (status != 0)
            return;

        var data = _buffer.AsSpan(0, (int)Math.Min(size, (uint)_buffer.Length));
        if (data.Length < RowsAt)
            return;

        var count = BinaryPrimitives.ReadInt32LittleEndian(data);
        var rowSize = (protocol, family) switch
        {
            (Protocol.Tcp, AfInet) => Tcp4Row,
            (Protocol.Tcp, _) => Tcp6Row,
            (Protocol.Udp, AfInet) => Udp4Row,
            _ => Udp6Row,
        };

        for (var i = 0; i < count; i++)
        {
            var at = RowsAt + i * rowSize;
            if (at + rowSize > data.Length)
                break;

            var row = data.Slice(at, rowSize);
            entries.Add((protocol, family) switch
            {
                // MIB_TCPROW_OWNER_MODULE: state, local addr, local port, remote addr, remote port, pid, created.
                (Protocol.Tcp, AfInet) => new SocketEntry(
                    Protocol.Tcp,
                    NetAddress.FromV4(row[4..]), Port(row[8..]),
                    NetAddress.FromV4(row[12..]), Port(row[16..]),
                    (TcpState)BinaryPrimitives.ReadUInt32LittleEndian(row),
                    BinaryPrimitives.ReadInt32LittleEndian(row[20..]),
                    BinaryPrimitives.ReadInt64LittleEndian(row[24..])),

                // MIB_TCP6ROW_OWNER_MODULE: local addr, scope, port, remote addr, scope, port, state, pid, created.
                (Protocol.Tcp, _) => new SocketEntry(
                    Protocol.Tcp,
                    NetAddress.FromV6(row), Port(row[20..]),
                    NetAddress.FromV6(row[24..]), Port(row[44..]),
                    (TcpState)BinaryPrimitives.ReadUInt32LittleEndian(row[48..]),
                    BinaryPrimitives.ReadInt32LittleEndian(row[52..]),
                    BinaryPrimitives.ReadInt64LittleEndian(row[56..])),

                // MIB_UDPROW_OWNER_MODULE: local addr, port, pid, created.
                (Protocol.Udp, AfInet) => new SocketEntry(
                    Protocol.Udp,
                    NetAddress.FromV4(row), Port(row[4..]),
                    NetAddress.None, 0, TcpState.None,
                    BinaryPrimitives.ReadInt32LittleEndian(row[8..]),
                    BinaryPrimitives.ReadInt64LittleEndian(row[16..])),

                // MIB_UDP6ROW_OWNER_MODULE: local addr, scope, port, pid, created.
                _ => new SocketEntry(
                    Protocol.Udp,
                    NetAddress.FromV6(row), Port(row[20..]),
                    NetAddress.None, 0, TcpState.None,
                    BinaryPrimitives.ReadInt32LittleEndian(row[24..]),
                    BinaryPrimitives.ReadInt64LittleEndian(row[32..])),
            });
        }
    }

    /// <summary>Ports sit in the low 16 bits of a DWORD, in network byte order.</summary>
    private static ushort Port(ReadOnlySpan<byte> dword) => BinaryPrimitives.ReadUInt16BigEndian(dword);

    /// <summary>
    /// Resets an IPv4 TCP connection (<c>SetTcpEntry</c> with DELETE_TCB). Needs administrator rights;
    /// Windows has no equivalent for IPv6. Returns 0 or a Win32 error.
    /// </summary>
    public static uint Close(NetAddress local, ushort localPort, NetAddress remote, ushort remotePort)
    {
        if (!local.IsV4 || !remote.IsV4)
            return 50; // ERROR_NOT_SUPPORTED

        var row = stackalloc uint[5];
        row[0] = (uint)TcpState.DeleteTcb;
        row[1] = NetworkOrder(local.V4);
        row[2] = NetworkOrderPort(localPort);
        row[3] = NetworkOrder(remote.V4);
        row[4] = NetworkOrderPort(remotePort);
        return SetTcpEntry(row);
    }

    /// <summary>The DWORD whose bytes in memory are the address in network order.</summary>
    private static uint NetworkOrder(uint value) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;

    private static uint NetworkOrderPort(ushort port) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(port) : port;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(void* table, ref uint size, int order, uint family, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedUdpTable(void* table, ref uint size, int order, uint family, int tableClass, uint reserved);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint SetTcpEntry(uint* row);
}
