using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace Sockless.TaskManager.Features.Network;

/// <summary>What a database knows about one address. Any part may be missing.</summary>
internal sealed record GeoInfo(
    string? CountryCode,
    string? Country,
    string? Region,
    string? City,
    long? Asn,
    string? Organization,
    double? Latitude = null,
    double? Longitude = null)
{
    public static readonly GeoInfo Empty = new(null, null, null, null, null, null);

    /// <summary>City databases place an address (roughly); country ones don't.</summary>
    public bool HasCoordinates => Latitude is not null && Longitude is not null;

    /// <summary>"Stockholm, Sweden", "Sweden", or "SE".</summary>
    public string? Place => (City, Country ?? CountryCode) switch
    {
        ({ } city, { } country) => $"{city}, {country}",
        (null, { } country) => country,
        ({ } city, null) => city,
        _ => null,
    };

    /// <summary>"Google LLC (AS15169)".</summary>
    public string? Network => (Organization, Asn) switch
    {
        ({ } org, { } asn) => $"{org} (AS{asn})",
        ({ } org, null) => org,
        (null, { } asn) => $"AS{asn}",
        _ => null,
    };

    /// <summary>Fills what this one lacks from <paramref name="other"/>.</summary>
    public GeoInfo Merge(GeoInfo? other) => other is null ? this : new(
        CountryCode ?? other.CountryCode,
        Country ?? other.Country,
        Region ?? other.Region,
        City ?? other.City,
        Asn ?? other.Asn,
        Organization ?? other.Organization,
        Latitude ?? other.Latitude,
        Longitude ?? other.Longitude);
}

/// <summary>
/// A reader for MaxMind DB (.mmdb) files, the format DB-IP, MaxMind GeoLite2/GeoIP2 and IPinfo
/// publish their databases in. Hand-written so it stays reflection-free; the file is memory-mapped,
/// so a lookup only pages in the few hundred bytes it walks. Understands the GeoIP2 City/Country
/// and ASN layouts, and IPinfo's flat one. Spec: https://maxmind.github.io/MaxMind-DB/
/// Lookups are thread-safe; dispose only when none is running.
/// </summary>
internal sealed unsafe class GeoDatabase : IDisposable
{
    private static readonly byte[] MetadataMarker = [0xAB, 0xCD, 0xEF, .. "MaxMind.com"u8];

    // Data section types.
    private const int TypeExtended = 0;
    private const int TypePointer = 1;
    private const int TypeString = 2;
    private const int TypeDouble = 3;
    private const int TypeUInt16 = 5;
    private const int TypeUInt32 = 6;
    private const int TypeMap = 7;
    private const int TypeInt32 = 8;
    private const int TypeUInt64 = 9;
    private const int TypeUInt128 = 10;
    private const int TypeArray = 11;
    private const int TypeBoolean = 14;
    private const int TypeFloat = 15;

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly long _length;
    private readonly uint _nodeCount;
    private readonly int _recordSize;
    private readonly int _nodeBytes;
    private readonly long _dataStart;
    private readonly int _ipVersion;
    private readonly uint _ipv4Start;

    private GeoDatabase(string path, MemoryMappedFile file, MemoryMappedViewAccessor view, byte* pointer, long length)
    {
        Path = path;
        _file = file;
        _view = view;
        _base = pointer;
        _length = length;

        var metadata = FindMetadata() ?? throw new InvalidDataException("Not a MaxMind DB file: no metadata.");
        _nodeCount = (uint)(MetadataNumber(metadata, "node_count") ?? throw new InvalidDataException("No node count."));
        _recordSize = (int)(MetadataNumber(metadata, "record_size") ?? 0);
        _ipVersion = (int)(MetadataNumber(metadata, "ip_version") ?? 6);
        if (_recordSize is not (24 or 28 or 32))
            throw new InvalidDataException($"Unsupported record size {_recordSize}.");

        _nodeBytes = _recordSize / 4;
        _dataStart = (long)_nodeCount * _nodeBytes + 16;
        if (_dataStart >= _length)
            throw new InvalidDataException("Truncated file.");

        DatabaseType = MetadataString(metadata, "database_type") ?? "Unknown";
        Description = MetadataDescription(metadata);
        Built = MetadataNumber(metadata, "build_epoch") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime : null;

        // IPv4 addresses live under ::/96 in an IPv6 tree; find that node once.
        if (_ipVersion == 6)
        {
            var node = 0u;
            for (var i = 0; i < 96 && node < _nodeCount; i++)
                node = Record(node, 0);
            _ipv4Start = node;
        }
    }

    public string Path { get; }

    /// <summary>"DBIP-City-Lite", "GeoLite2-ASN", "ipinfo lite.mmdb"…</summary>
    public string DatabaseType { get; }

    public string? Description { get; }

    public DateTime? Built { get; }

    public long Size => _length;

    /// <summary>An ASN database names networks, not places.</summary>
    public bool IsNetworkDatabase =>
        DatabaseType.Contains("ASN", StringComparison.OrdinalIgnoreCase)
        || DatabaseType.Contains("ISP", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens and checks a database; throws <see cref="InvalidDataException"/> or an IO exception if it isn't one.</summary>
    public static GeoDatabase Open(string path)
    {
        var length = new FileInfo(path).Length;
        if (length < 64)
            throw new InvalidDataException("Not a MaxMind DB file: too small.");

        var file = MemoryMappedFile.CreateFromFile(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
            null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        MemoryMappedViewAccessor? view = null;
        var acquired = false;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            acquired = true;
            return new GeoDatabase(path, file, view, pointer + view.PointerOffset, length);
        }
        catch
        {
            if (acquired)
                view!.SafeMemoryMappedViewHandle.ReleasePointer();
            view?.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>What the database has for the address; null if nothing. Throws <see cref="InvalidDataException"/> on a corrupt file.</summary>
    public GeoInfo? Lookup(NetAddress address)
    {
        if (_ipVersion == 4 && !address.IsV4)
            return null;

        Span<byte> bytes = stackalloc byte[16];
        address.WriteBytes(bytes);
        var (node, bits, first) = address.IsV4 ? (_ipVersion == 6 ? _ipv4Start : 0u, 32, 12) : (0u, 128, 0);

        for (var i = 0; i < bits && node < _nodeCount; i++)
            node = Record(node, (bytes[first + i / 8] >> (7 - i % 8)) & 1);

        if (node <= _nodeCount)
            return null;

        var offset = _dataStart + (node - _nodeCount) - 16;
        return offset >= _dataStart && offset < _length ? Decode(offset) : null;
    }

    private uint Record(uint node, int side)
    {
        var p = _base + (long)node * _nodeBytes;
        return _recordSize switch
        {
            24 => side == 0 ? (uint)(p[0] << 16 | p[1] << 8 | p[2]) : (uint)(p[3] << 16 | p[4] << 8 | p[5]),
            28 => side == 0
                ? (uint)((p[3] & 0xF0) << 20 | p[0] << 16 | p[1] << 8 | p[2])
                : (uint)((p[3] & 0x0F) << 24 | p[4] << 16 | p[5] << 8 | p[6]),
            _ => BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(p + side * 4, 4)),
        };
    }

    // ---------- A record as GeoInfo ----------

    private GeoInfo Decode(long offset)
    {
        string? countryCode = null, country = null, region = null, city = null, organization = null;
        long? asn = null;
        double? latitude = null, longitude = null;

        var cursor = offset;
        var (type, size, c) = Header(Locate(ref cursor));
        if (type != TypeMap)
            return GeoInfo.Empty;

        for (var i = 0; i < size; i++)
        {
            var key = StringAt(Locate(ref c));
            var value = Locate(ref c);
            switch (key)
            {
                // GeoIP2 and DB-IP.
                case "country":
                    (countryCode, country) = PlaceAt(value);
                    break;
                case "registered_country" when countryCode is null && country is null:
                    (countryCode, country) = PlaceAt(value);
                    break;
                case "city":
                    city = PlaceAt(value).Name;
                    break;
                case "subdivisions":
                    region = FirstPlaceName(value);
                    break;
                case "location":
                    (latitude, longitude) = CoordinatesAt(value);
                    break;
                case "latitude":
                    latitude = DoubleAt(value);
                    break;
                case "longitude":
                    longitude = DoubleAt(value);
                    break;
                case "autonomous_system_number":
                    asn = (long?)NumberAt(value);
                    break;
                case "autonomous_system_organization" or "isp" or "organization":
                    organization ??= StringAt(value);
                    break;

                // IPinfo's flat layout; its "asn" is a string, "AS15169".
                case "country_code":
                    countryCode ??= StringAt(value);
                    break;
                case "country_name":
                    country ??= StringAt(value);
                    break;
                case "as_name":
                    organization ??= StringAt(value);
                    break;
                case "asn":
                    if (StringAt(value) is { } text && text.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(text.AsSpan(2), out var parsed))
                        asn = parsed;
                    else if (NumberAt(value) is { } number)
                        asn = (long)number;
                    break;
            }
        }

        return new GeoInfo(countryCode, country, region, city, asn, organization, latitude, longitude);
    }

    /// <summary>GeoIP2's <c>{ latitude, longitude, accuracy_radius… }</c>.</summary>
    private (double?, double?) CoordinatesAt(long p)
    {
        var (type, size, c) = Header(p);
        if (type != TypeMap)
            return (null, null);

        double? latitude = null, longitude = null;
        for (var i = 0; i < size; i++)
        {
            var key = StringAt(Locate(ref c));
            var value = Locate(ref c);
            if (key == "latitude")
                latitude = DoubleAt(value);
            else if (key == "longitude")
                longitude = DoubleAt(value);
        }

        return (latitude, longitude);
    }

    /// <summary>A double (8 bytes, big-endian) or float; IPinfo stores coordinates as strings.</summary>
    private double? DoubleAt(long p)
    {
        var (type, size, payload) = Header(p);
        switch (type)
        {
            case TypeDouble when payload + 8 <= _length:
                return BinaryPrimitives.ReadDoubleBigEndian(new ReadOnlySpan<byte>(_base + payload, 8));
            case TypeFloat when payload + 4 <= _length:
                return BinaryPrimitives.ReadSingleBigEndian(new ReadOnlySpan<byte>(_base + payload, 4));
            case TypeString:
                return double.TryParse(Utf8(payload, size), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            default:
                return null;
        }
    }

    /// <summary>A GeoIP2 place: <c>{ iso_code, names: { en: … } }</c>.</summary>
    private (string? Code, string? Name) PlaceAt(long p)
    {
        var (type, size, c) = Header(p);
        if (type != TypeMap)
            return (null, null);

        string? code = null, name = null;
        for (var i = 0; i < size; i++)
        {
            var key = StringAt(Locate(ref c));
            var value = Locate(ref c);
            if (key == "iso_code")
                code = StringAt(value);
            else if (key == "names")
                name = NameAt(value);
        }

        return (code, name);
    }

    private string? FirstPlaceName(long p)
    {
        var (type, size, c) = Header(p);
        return type == TypeArray && size > 0 ? PlaceAt(Locate(ref c)).Name : null;
    }

    /// <summary>The name in the UI language if the database has it, else English.</summary>
    private string? NameAt(long p)
    {
        var (type, size, c) = Header(p);
        if (type != TypeMap)
            return null;

        var preferred = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        string? english = null, local = null, any = null;
        for (var i = 0; i < size; i++)
        {
            var language = StringAt(Locate(ref c));
            var value = StringAt(Locate(ref c));
            if (language == preferred && preferred != "en")
                local = value;
            else if (language == "en")
                english = value;
            any ??= value;
        }

        return local ?? english ?? any;
    }

    // ---------- Data section ----------

    /// <summary>
    /// Where the value at <paramref name="at"/> really is (through a pointer, if it's one), moving
    /// <paramref name="at"/> past the value, or past the pointer.
    /// </summary>
    private long Locate(ref long at)
    {
        var control = Byte(at);
        if (control >> 5 == TypePointer)
        {
            at++;
            return PointerTarget(control, ref at);
        }

        var start = at;
        at = End(at);
        return start;
    }

    /// <summary>A value's type and size, and where its payload starts. <paramref name="p"/> must not be a pointer.</summary>
    private (int Type, long Size, long Payload) Header(long p)
    {
        var control = Byte(p++);
        var type = control >> 5;
        if (type == TypeExtended)
            type = 7 + Byte(p++);

        long size = control & 0x1F;
        size = size switch
        {
            29 => 29 + Byte(p++),
            30 => 285 + (Byte(p++) << 8 | Byte(p++)),
            31 => 65821 + (Byte(p++) << 16 | Byte(p++) << 8 | Byte(p++)),
            _ => size,
        };
        return (type, size, p);
    }

    private long PointerTarget(int control, ref long at)
    {
        long vvv = control & 0x7;
        var pointer = ((control >> 3) & 0x3) switch
        {
            0 => vvv << 8 | Byte(at++),
            1 => (vvv << 16 | (long)Byte(at++) << 8 | Byte(at++)) + 2048,
            2 => (vvv << 24 | (long)Byte(at++) << 16 | (long)Byte(at++) << 8 | Byte(at++)) + 526336,
            _ => (long)Byte(at++) << 24 | (long)Byte(at++) << 16 | (long)Byte(at++) << 8 | Byte(at++),
        };
        return _dataStart + pointer;
    }

    /// <summary>Where the value (or pointer) at <paramref name="p"/> ends.</summary>
    private long End(long p)
    {
        var control = Byte(p);
        if (control >> 5 == TypePointer)
        {
            p++;
            PointerTarget(control, ref p);
            return p;
        }

        var (type, size, payload) = Header(p);
        switch (type)
        {
            case TypeMap:
                for (var i = 0; i < size * 2; i++)
                    payload = End(payload);
                return payload;
            case TypeArray:
                for (var i = 0; i < size; i++)
                    payload = End(payload);
                return payload;
            case TypeDouble:
                return payload + 8;
            case TypeFloat:
                return payload + 4;
            case TypeBoolean:
                return payload;
            default:
                return payload + size;
        }
    }

    private string? StringAt(long p)
    {
        var (type, size, payload) = Header(p);
        return type == TypeString ? Utf8(payload, size) : null;
    }

    private ulong? NumberAt(long p)
    {
        var (type, size, payload) = Header(p);
        if (type is not (TypeUInt16 or TypeUInt32 or TypeUInt64 or TypeInt32 or TypeUInt128))
            return null;

        ulong value = 0;
        var take = (int)Math.Min(size, 8);
        for (var i = 0; i < take; i++)
            value = value << 8 | Byte(payload + size - take + i);
        return value;
    }

    private string Utf8(long p, long size) =>
        p >= 0 && size >= 0 && p + size <= _length ? Encoding.UTF8.GetString(_base + p, (int)size) : string.Empty;

    private byte Byte(long at) =>
        at >= 0 && at < _length ? _base[at] : throw new InvalidDataException("Read past the end of the database.");

    // ---------- Metadata ----------

    private long? FindMetadata()
    {
        var searchFrom = Math.Max(0, _length - 128 * 1024);
        var tail = new ReadOnlySpan<byte>(_base + searchFrom, (int)(_length - searchFrom));
        var at = tail.LastIndexOf(MetadataMarker);
        return at < 0 ? null : searchFrom + at + MetadataMarker.Length;
    }

    /// <summary>Where the metadata map's value for <paramref name="key"/> is, or -1. Metadata has no pointers.</summary>
    private long MetadataValue(long metadata, string key)
    {
        var (type, size, c) = Header(metadata);
        if (type != TypeMap)
            return -1;

        for (var i = 0; i < size; i++)
        {
            var name = StringAt(c);
            c = End(c);
            if (name == key)
                return c;
            c = End(c);
        }

        return -1;
    }

    private ulong? MetadataNumber(long metadata, string key) =>
        MetadataValue(metadata, key) is var at and >= 0 ? NumberAt(at) : null;

    private string? MetadataString(long metadata, string key) =>
        MetadataValue(metadata, key) is var at and >= 0 ? StringAt(at) : null;

    private string? MetadataDescription(long metadata) =>
        MetadataValue(metadata, "description") is var at and >= 0 ? NameAt(at) : null;

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
