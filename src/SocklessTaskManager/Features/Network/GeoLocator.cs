using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;

namespace Sockless.TaskManager.Features.Network;

/// <summary>The two kinds of database: where an address is, and which network (ASN) owns it.</summary>
internal enum GeoKind
{
    Location,
    Network,
}

/// <summary>A free database that can be downloaded without an account.</summary>
/// <param name="FilePrefix">DB-IP's file name before the month: <c>dbip-city-lite</c>.</param>
internal sealed record GeoSource(string Title, string Description, GeoKind Kind, string FilePrefix);

/// <summary>
/// Location and network-owner lookups from .mmdb databases the user chose to download or add. None
/// ships with the app. Files live under %LOCALAPPDATA%\Sockless Task Manager\GeoIP as
/// <c>location.mmdb</c> and <c>network.mmdb</c>. UI thread only, so a database is never swapped under
/// a lookup.
/// </summary>
internal sealed class GeoLocator : IDisposable
{
    public const string Attribution = "IP geolocation by DB-IP (db-ip.com), licensed CC BY 4.0.";

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", "GeoIP");

    public static readonly GeoSource[] Sources =
    [
        new("Countries", "DB-IP Lite, about 4 MB to download", GeoKind.Location, "dbip-country-lite"),
        new("Cities", "DB-IP Lite, about 60 MB to download, 130 MB on disk", GeoKind.Location, "dbip-city-lite"),
        new("Network owners", "DB-IP Lite ASN, about 5 MB to download", GeoKind.Network, "dbip-asn-lite"),
    ];

    private const int MaxCached = 50_000;

    private static readonly HttpClient Http = CreateClient();

    private readonly Dictionary<NetAddress, GeoInfo?> _cache = [];
    private GeoDatabase? _location;
    private GeoDatabase? _network;
    private bool _loaded;

    /// <summary>Raised on the UI thread when a database is added, replaced or removed.</summary>
    public event Action? Changed;

    public GeoDatabase? Location
    {
        get
        {
            EnsureLoaded();
            return _location;
        }
    }

    public GeoDatabase? Network
    {
        get
        {
            EnsureLoaded();
            return _network;
        }
    }

    public bool HasAny => Location is not null || Network is not null;

    /// <summary>What the databases know about a public address; null for private ones or without a database.</summary>
    public GeoInfo? Lookup(NetAddress address)
    {
        if (!address.IsRoutable || !HasAny)
            return null;
        if (_cache.TryGetValue(address, out var cached))
            return cached;

        GeoInfo? info = null;
        try
        {
            info = _location?.Lookup(address);
            if (_network?.Lookup(address) is { } network)
                info = info?.Merge(network) ?? network;
        }
        catch (InvalidDataException ex)
        {
            Debug.WriteLine($"Geo lookup of {address} failed: {ex.Message}");
        }

        if (_cache.Count >= MaxCached)
            _cache.Clear();
        _cache[address] = info;
        return info;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;
        _location = TryOpen(FilePath(GeoKind.Location));
        _network = TryOpen(FilePath(GeoKind.Network));
    }

    private static GeoDatabase? TryOpen(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return GeoDatabase.Open(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Couldn't open {path}: {ex.Message}");
            return null;
        }
    }

    public static string FilePath(GeoKind kind) =>
        Path.Combine(Folder, kind == GeoKind.Location ? "location.mmdb" : "network.mmdb");

    private static string StagingPath(GeoKind kind) => FilePath(kind) + ".download";

    /// <summary>Puts a checked database file in place of the current one of its kind.</summary>
    private void Install(GeoKind kind, string staged)
    {
        EnsureLoaded();
        ref var current = ref kind == GeoKind.Location ? ref _location : ref _network;
        current?.Dispose();
        current = null;

        var path = FilePath(kind);
        File.Move(staged, path, overwrite: true);
        current = TryOpen(path);
        _cache.Clear();
        Changed?.Invoke();
    }

    public void Remove(GeoKind kind)
    {
        EnsureLoaded();
        ref var current = ref kind == GeoKind.Location ? ref _location : ref _network;
        current?.Dispose();
        current = null;

        try
        {
            File.Delete(FilePath(kind));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Couldn't delete the {kind} database: {ex.Message}");
        }

        _cache.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// Downloads this month's (or, early in the month, last month's) file, unpacks and checks it, then
    /// installs it. Downloading runs off the UI thread; installing on it (call from the UI thread).
    /// </summary>
    public async Task DownloadAsync(GeoSource source, IProgress<double> progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        var staged = StagingPath(source.Kind);
        await Task.Run(() => FetchAsync(source, staged, progress, cancel), cancel);
        Install(source.Kind, staged);
    }

    private static async Task FetchAsync(GeoSource source, string staged, IProgress<double> progress, CancellationToken cancel)
    {
        var month = DateTime.UtcNow;
        for (var attempt = 0; attempt < 2; attempt++, month = month.AddMonths(-1))
        {
            var url = $"https://download.db-ip.com/free/{source.FilePrefix}-{month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.mmdb.gz";
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound && attempt == 0)
                continue;
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            try
            {
                await using (var download = await response.Content.ReadAsStreamAsync(cancel))
                await using (var counting = new ProgressStream(download, total, progress))
                await using (var unzip = new GZipStream(counting, CompressionMode.Decompress))
                await using (var file = File.Create(staged))
                {
                    await unzip.CopyToAsync(file, cancel);
                }

                Check(staged, source.Kind);
                return;
            }
            catch
            {
                TryDelete(staged);
                throw;
            }
        }

        throw new HttpRequestException("DB-IP has no file for this month or the last.");
    }

    /// <summary>
    /// Adds a database the user already has (GeoLite2, IPinfo, a DB-IP file…). Which kind it is
    /// comes from its metadata. Returns the kind installed. Call from the UI thread.
    /// </summary>
    public async Task<GeoKind> ImportAsync(string path, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        var kind = await Task.Run(() =>
        {
            GeoKind detected;
            using (var db = GeoDatabase.Open(path))
                detected = db.IsNetworkDatabase ? GeoKind.Network : GeoKind.Location;

            var staged = StagingPath(detected);
            File.Copy(path, staged, overwrite: true);
            return detected;
        }, cancel);

        Install(kind, StagingPath(kind));
        return kind;
    }

    /// <summary>Opens the file to prove it's a database of the expected kind.</summary>
    private static void Check(string path, GeoKind kind)
    {
        using var db = GeoDatabase.Open(path);
        if (db.IsNetworkDatabase != (kind == GeoKind.Network))
            throw new InvalidDataException($"Expected a {kind.ToString().ToLowerInvariant()} database, got {db.DatabaseType}.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"DBIP-City-Lite, built 1 Nov 2026, 128 MB".</summary>
    public static string Describe(GeoDatabase db) =>
        db.Built is { } built
            ? $"{db.DatabaseType}, built {built.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}, {Core.Format.Bytes(db.Size)}"
            : $"{db.DatabaseType}, {Core.Format.Bytes(db.Size)}";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocklessTaskManager");
        return client;
    }

    public void Dispose()
    {
        _location?.Dispose();
        _network?.Dispose();
        _location = _network = null;
    }

    /// <summary>Reports how far through the compressed download the reader is.</summary>
    private sealed class ProgressStream(Stream inner, long total, IProgress<double> progress) : Stream
    {
        private long _read;
        private int _lastPercent = -1;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            _read += read;
            if (total > 0)
            {
                var percent = (int)(_read * 100 / total);
                if (percent != _lastPercent)
                {
                    _lastPercent = percent;
                    progress.Report(Math.Min(1, (double)_read / total));
                }
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
