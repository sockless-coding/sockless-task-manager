using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace Sockless.TaskManager.Features.Network;

/// <summary>Where an address is, in the terms someone debugging traffic cares about.</summary>
internal enum AddressScope : byte
{
    /// <summary>0.0.0.0 or ::, a socket bound to every address.</summary>
    Any,
    Loopback,
    LinkLocal,

    /// <summary>RFC 1918, carrier-grade NAT, unique local IPv6.</summary>
    Private,
    Multicast,
    Broadcast,
    Public,
}

/// <summary>
/// An IPv4 or IPv6 address as 16 bytes, IPv4 kept as IPv4-mapped IPv6 (<c>::ffff:a.b.c.d</c>), so
/// either fits in one key without allocating. <see cref="High"/> and <see cref="Low"/> are the
/// address's first and last 8 bytes read big-endian.
/// </summary>
internal readonly record struct NetAddress(ulong High, ulong Low) : IComparable<NetAddress>
{
    private const ulong MappedPrefix = 0x0000_FFFF_0000_0000;

    public static readonly NetAddress None = default;

    public bool IsV4 => High == 0 && (Low & 0xFFFF_FFFF_0000_0000) == MappedPrefix;

    /// <summary>The IPv4 address, most significant byte first (192.168.0.1 is 0xC0A80001).</summary>
    public uint V4 => (uint)Low;

    /// <param name="networkOrder">Four bytes as they travel: 192, 168, 0, 1.</param>
    public static NetAddress FromV4(ReadOnlySpan<byte> networkOrder) =>
        new(0, MappedPrefix | BinaryPrimitives.ReadUInt32BigEndian(networkOrder));

    public static NetAddress FromV4(uint value) => new(0, MappedPrefix | value);

    /// <summary>Sixteen bytes as they travel. An IPv4-mapped address comes back as IPv4.</summary>
    public static NetAddress FromV6(ReadOnlySpan<byte> bytes) =>
        new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));

    public static NetAddress From(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            address.TryWriteBytes(bytes, out _);
            return FromV4(bytes);
        }

        address.TryWriteBytes(bytes, out _);
        return FromV6(bytes);
    }

    public static bool TryParse(ReadOnlySpan<char> text, out NetAddress address)
    {
        if (IPAddress.TryParse(text, out var parsed))
        {
            address = From(parsed);
            return true;
        }

        address = None;
        return false;
    }

    public IPAddress ToIPAddress()
    {
        if (IsV4)
            return new IPAddress([(byte)(V4 >> 24), (byte)(V4 >> 16), (byte)(V4 >> 8), (byte)V4]);

        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
        return new IPAddress(bytes);
    }

    /// <summary>Writes the 16 bytes, IPv4 as IPv4-mapped.</summary>
    public void WriteBytes(Span<byte> bytes)
    {
        BinaryPrimitives.WriteUInt64BigEndian(bytes, High);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
    }

    public AddressScope Scope
    {
        get
        {
            if (IsV4)
            {
                var v = V4;
                return (v >> 24) switch
                {
                    0 => AddressScope.Any,
                    127 => AddressScope.Loopback,
                    10 => AddressScope.Private,
                    _ when v == 0xFFFF_FFFF => AddressScope.Broadcast,
                    >= 224 and <= 239 => AddressScope.Multicast,
                    _ when (v & 0xFFFF_0000) == 0xA9FE_0000 => AddressScope.LinkLocal,  // 169.254/16
                    _ when (v & 0xFFF0_0000) == 0xAC10_0000 => AddressScope.Private,    // 172.16/12
                    _ when (v & 0xFFFF_0000) == 0xC0A8_0000 => AddressScope.Private,    // 192.168/16
                    _ when (v & 0xFFC0_0000) == 0x6440_0000 => AddressScope.Private,    // 100.64/10
                    _ => AddressScope.Public,
                };
            }

            if (High == 0 && Low == 0)
                return AddressScope.Any;
            if (High == 0 && Low == 1)
                return AddressScope.Loopback;

            var top = (ushort)(High >> 48);
            if ((top & 0xFF00) == 0xFF00)
                return AddressScope.Multicast;
            if ((top & 0xFFC0) == 0xFE80)
                return AddressScope.LinkLocal;
            if ((top & 0xFE00) == 0xFC00)
                return AddressScope.Private;
            return AddressScope.Public;
        }
    }

    /// <summary>Somewhere a location or owner can be looked up for.</summary>
    public bool IsRoutable => Scope == AddressScope.Public;

    public override string ToString()
    {
        if (IsV4)
        {
            var v = V4;
            return string.Create(CultureInfo.InvariantCulture, $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}");
        }

        return ToIPAddress().ToString();
    }

    /// <summary>"1.2.3.4:443", "[2001:db8::1]:443", or "1.2.3.4:*" without a port.</summary>
    public string WithPort(ushort port)
    {
        var p = port == 0 ? "*" : port.ToString(CultureInfo.InvariantCulture);
        return IsV4 ? $"{this}:{p}" : $"[{this}]:{p}";
    }

    public int CompareTo(NetAddress other)
    {
        var high = High.CompareTo(other.High);
        return high != 0 ? high : Low.CompareTo(other.Low);
    }
}

internal static class AddressScopes
{
    /// <summary>A word for the scope; empty for the public internet, where the location says more.</summary>
    public static string Describe(AddressScope scope) => scope switch
    {
        AddressScope.Any => "Any address",
        AddressScope.Loopback => "This computer",
        AddressScope.LinkLocal => "Link-local",
        AddressScope.Private => "Local network",
        AddressScope.Multicast => "Multicast",
        AddressScope.Broadcast => "Broadcast",
        _ => "Internet",
    };
}
