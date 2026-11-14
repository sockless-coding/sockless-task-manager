using System.Globalization;
using System.Text;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

/// <summary>Wording shared by the Network page's tables, details and export.</summary>
internal static class NetworkText
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>The time for today, the date and time before that.</summary>
    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.ToString(local.Date == DateTime.Today ? "T" : "g", Culture);
    }

    /// <summary>"14 Nov 2026 10:42:07 (3 min ago)".</summary>
    public static string WhenLong(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return $"{local.ToString("G", Culture)} ({Ago(DateTime.UtcNow - utc)})";
    }

    public static string Ago(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
            return "just now";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours} h {span.Minutes} min ago";
        return $"{(int)span.TotalDays} d {span.Hours} h ago";
    }

    public static string Protocol(FlowKey key) => key.Protocol == Network.Protocol.Tcp ? "TCP" : "UDP";

    public static string ProtocolLong(FlowKey key) =>
        $"{Protocol(key)} over {(key.Local.IsV4 ? "IPv4" : "IPv6")}";

    /// <summary>Bits per second, as the Performance page shows network speed; empty when idle.</summary>
    public static string Rate(double bytesPerSecond) =>
        bytesPerSecond > 0 ? Format.BitsPerSecond(bytesPerSecond * 8) : string.Empty;

    /// <summary>What a well-known port is usually for, or null.</summary>
    public static string? PortName(ushort port, Protocol protocol) => (port, protocol) switch
    {
        (20 or 21, Network.Protocol.Tcp) => "FTP",
        (22, Network.Protocol.Tcp) => "SSH",
        (23, Network.Protocol.Tcp) => "Telnet",
        (25 or 465 or 587, Network.Protocol.Tcp) => "SMTP (mail)",
        (53, _) => "DNS",
        (67 or 68, Network.Protocol.Udp) => "DHCP",
        (80, Network.Protocol.Tcp) => "HTTP",
        (110 or 995, Network.Protocol.Tcp) => "POP3 (mail)",
        (123, Network.Protocol.Udp) => "NTP (time)",
        (135, Network.Protocol.Tcp) => "RPC endpoint mapper",
        (137 or 138, Network.Protocol.Udp) => "NetBIOS",
        (139, Network.Protocol.Tcp) => "NetBIOS session",
        (143 or 993, Network.Protocol.Tcp) => "IMAP (mail)",
        (161 or 162, Network.Protocol.Udp) => "SNMP",
        (389 or 636, _) => "LDAP",
        (443, Network.Protocol.Tcp) => "HTTPS",
        (443, Network.Protocol.Udp) => "HTTPS (QUIC)",
        (445, Network.Protocol.Tcp) => "SMB (file sharing)",
        (500 or 4500, Network.Protocol.Udp) => "IPsec",
        (853, _) => "DNS over TLS",
        (1194, _) => "OpenVPN",
        (1433, Network.Protocol.Tcp) => "SQL Server",
        (1900, Network.Protocol.Udp) => "SSDP (device discovery)",
        (3306, Network.Protocol.Tcp) => "MySQL",
        (3389, _) => "Remote Desktop",
        (3702, Network.Protocol.Udp) => "WS-Discovery",
        (5040, Network.Protocol.Tcp) => "Connected Devices Platform",
        (5353, Network.Protocol.Udp) => "mDNS",
        (5355, _) => "LLMNR",
        (5432, Network.Protocol.Tcp) => "PostgreSQL",
        (5985 or 5986, Network.Protocol.Tcp) => "WinRM (remote PowerShell)",
        (6379, Network.Protocol.Tcp) => "Redis",
        (7680, Network.Protocol.Tcp) => "Delivery Optimization",
        (8080 or 8000 or 8888, Network.Protocol.Tcp) => "HTTP (alternative)",
        (27017, Network.Protocol.Tcp) => "MongoDB",
        (51820, Network.Protocol.Udp) => "WireGuard",
        _ => null,
    };

    /// <summary>A CSV field, quoted when it needs to be; text a spreadsheet would run as a formula is neutralised.</summary>
    public static void Csv(StringBuilder line, string? value, bool last = false)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        if (value.AsSpan().IndexOfAny(",\"\r\n") >= 0)
            line.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        else
            line.Append(value);

        line.Append(last ? "\r\n" : ",");
    }

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Iso(DateTime? utc) => utc?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;
}
