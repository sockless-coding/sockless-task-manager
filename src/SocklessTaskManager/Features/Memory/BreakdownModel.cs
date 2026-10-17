using Avalonia.Threading;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>One sampling pass. Built on the sampling thread, then only read.</summary>
internal sealed class BreakdownSample
{
    public required ulong TotalBytes { get; init; }

    public required PageLists Lists { get; init; }

    /// <summary>Only read while the path is inside "In use".</summary>
    public KernelReading? Kernel { get; init; }

    public List<ProcessUsage>? Processes { get; init; }

    /// <summary>Only read while a pool is shown.</summary>
    public List<PoolTag>? Tags { get; init; }

    /// <summary>Only read while the driver list is shown.</summary>
    public List<KernelModule>? Modules { get; init; }

    /// <summary>Active memory: neither free, nor cache, nor waiting to be written.</summary>
    public ulong InUseBytes
    {
        get
        {
            var other = Lists.UnusedBytes + Lists.BadBytes + Lists.StandbyBytes + Lists.AllModifiedBytes;
            return TotalBytes > other ? TotalBytes - other : 0;
        }
    }
}

/// <summary>What a sample has to read for the current path.</summary>
[Flags]
internal enum BreakdownNeeds
{
    Lists = 0,
    InUse = 1,
    Tags = 2,
    Modules = 4,
}

/// <summary>A row: something that holds memory, and whether it can be opened.</summary>
internal sealed record Entry(string Key, string Name, double Bytes)
{
    public string Note { get; init; } = string.Empty;

    /// <summary>Series token for the legend swatch and bar, where the level has a composition bar.</summary>
    public string? SeriesKey { get; init; }

    public bool CanOpen { get; init; }

    /// <summary>Left out while it holds nothing (bad pages, usually).</summary>
    public bool Optional { get; init; }

    /// <summary>Identifiers (pool tags) are set in mono.</summary>
    public bool Mono { get; init; }

    public string? ToolTip { get; init; }
}

/// <summary>What one breadcrumb path shows: a title, an explanation, a total and its parts.</summary>
internal sealed record Level(string Title, string Description, double Bytes, IReadOnlyList<Entry> Entries)
{
    /// <summary>Largest first. False keeps the order given (page lists, standby priorities).</summary>
    public bool Sorted { get; init; } = true;

    /// <summary>Show the stacked bar of the entries above the table.</summary>
    public bool ShowComposition { get; init; }

    /// <summary>False where entry sizes aren't parts of the total (driver image sizes).</summary>
    public bool ShowShare { get; init; } = true;

    public string SizeHeader { get; init; } = "In RAM";

    /// <summary>A line about the data itself: scanning, a permission problem, nothing found.</summary>
    public string Status { get; init; } = string.Empty;

    public bool IsBusy { get; init; }

    /// <summary>Key of the on-demand scan behind this level, so it can be re-run.</summary>
    public string? ScanKey { get; init; }
}

/// <summary>
/// Slow readings (address spaces, driver files) run once when their level is opened, on the
/// thread pool, and are kept while the path still uses them. UI thread only.
/// </summary>
internal sealed class Scans
{
    private readonly Dictionary<string, Scan> _scans = [];
    private readonly HashSet<string> _used = [];

    /// <summary>A scan finished; the level showing it should render again.</summary>
    public event Action? Completed;

    public Scan<T> Get<T>(string key, Func<T> run) where T : class
    {
        _used.Add(key);
        if (_scans.TryGetValue(key, out var existing))
            return (Scan<T>)existing;

        var scan = new Scan<T>();
        _scans[key] = scan;
        _ = Task.Run(() =>
        {
            T? result = null;
            string? error = null;
            try
            {
                result = run();
            }
            catch (Exception ex)
            {
                error = ex is UnauthorizedAccessException ? ex.Message : $"Reading failed: {ex.Message}";
            }

            Dispatcher.UIThread.Post(() =>
            {
                scan.Complete(result, error);
                if (_scans.TryGetValue(key, out var current) && ReferenceEquals(current, scan))
                    Completed?.Invoke();
            });
        });
        return scan;
    }

    /// <summary>Starts a render pass; scans not asked for by the end of it are dropped.</summary>
    public void BeginPass() => _used.Clear();

    public void EndPass()
    {
        foreach (var key in _scans.Keys.Where(k => !_used.Contains(k)).ToList())
            _scans.Remove(key);
    }

    public void Forget(string key) => _scans.Remove(key);
}

internal abstract class Scan
{
    public bool IsDone { get; private protected set; }

    public string? Error { get; private protected set; }

    public DateTime CompletedAt { get; private protected set; }
}

internal sealed class Scan<T> : Scan where T : class
{
    public T? Result { get; private set; }

    public void Complete(T? result, string? error)
    {
        Result = result;
        Error = error;
        IsDone = true;
        CompletedAt = DateTime.Now;
    }
}
