using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// The names in Windows' DNS client cache and the addresses they resolved to: what
/// <c>ipconfig /displaydns</c> shows. That's the name an app asked for (<c>www.example.com</c>),
/// which says far more than a reverse lookup of a CDN address does, and reading it sends nothing
/// over the network. <c>DnsGetCacheDataTable</c> is undocumented but has been stable since XP; each
/// name is then asked of the cache only (<c>DNS_QUERY_NO_WIRE_QUERY</c>). Needs no elevation.
/// </summary>
internal static unsafe partial class DnsCache
{
    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private const uint QueryNoWireQuery = 0x10;
    private const int FreeFlat = 0;
    private const int FreeRecordList = 1;

    // DNS_CACHE_ENTRY (x64): next, name, type, data length, flags.
    private const int EntryNext = 0;
    private const int EntryName = 8;
    private const int EntryType = 16;

    // DNS_RECORDW (x64): next, name, type, data length, flags, ttl, reserved, then the data.
    private const int RecordNext = 0;
    private const int RecordType = 16;
    private const int RecordData = 32;

    /// <summary>Every cached A and AAAA answer, as (name asked for, address). Empty if the cache can't be read.</summary>
    public static List<(string Name, NetAddress Address)> Read()
    {
        var results = new List<(string, NetAddress)>();
        nint first;
        try
        {
            if (!DnsGetCacheDataTable(out first) || first == 0)
                return results;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return results;
        }

        var seen = new HashSet<(string, ushort)>();
        var entry = (byte*)first;
        while (entry is not null)
        {
            var next = *(byte**)(entry + EntryNext);
            var namePointer = *(char**)(entry + EntryName);
            var type = *(ushort*)(entry + EntryType);
            if (namePointer is not null && type is TypeA or TypeAaaa)
            {
                var name = new string(namePointer);
                if (seen.Add((name, type)))
                    Resolve(name, type, results);
            }

            if (namePointer is not null)
                DnsFree((nint)namePointer, FreeFlat);
            DnsFree((nint)entry, FreeFlat);
            entry = next;
        }

        return results;
    }

    private static void Resolve(string name, ushort type, List<(string, NetAddress)> results)
    {
        nint records;
        fixed (char* n = name)
        {
            if (DnsQuery(n, type, QueryNoWireQuery, 0, out records, 0) != 0 || records == 0)
                return;
        }

        try
        {
            for (var record = (byte*)records; record is not null; record = *(byte**)(record + RecordNext))
            {
                switch (*(ushort*)(record + RecordType))
                {
                    case TypeA:
                        results.Add((name, NetAddress.FromV4(new ReadOnlySpan<byte>(record + RecordData, 4))));
                        break;
                    case TypeAaaa:
                        results.Add((name, NetAddress.FromV6(new ReadOnlySpan<byte>(record + RecordData, 16))));
                        break;
                }
            }
        }
        finally
        {
            DnsFree(records, FreeRecordList);
        }
    }

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsGetCacheDataTable(out nint entries);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W")]
    private static partial int DnsQuery(char* name, ushort type, uint options, nint extra, out nint results, nint reserved);

    [LibraryImport("dnsapi.dll")]
    private static partial void DnsFree(nint data, int freeType);
}
