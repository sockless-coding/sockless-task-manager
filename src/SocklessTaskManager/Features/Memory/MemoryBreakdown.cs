using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>
/// The Memory page's state: a breadcrumb path into <see cref="BreakdownTree"/> and the rows of
/// the level it points at. Samples only while the page is on screen, and only what the current
/// level needs (see <see cref="BreakdownNeeds"/>). Every row is listed; the view virtualizes.
/// </summary>
public sealed class MemoryBreakdown : ObservableObject, IDisposable
{
    /// <summary>
    /// Rows keep their order between re-sorts, so a list doesn't reshuffle under the pointer
    /// every tick, and moved rows (which re-render) stay rare.
    /// </summary>
    private const long ResortMilliseconds = 5000;

    // Sampling thread (and the occasional off-tick sample after navigating); guarded by _sampleLock.
    private readonly Lock _sampleLock = new();
    private readonly KernelMemory _kernel = new();
    private readonly PoolTags _tags = new();
    private byte[] _listBuffer = NtSystem.Buffer(256);
    private byte[] _processBuffer = NtSystem.Buffer(1024 * 1024);
    private byte[] _moduleBuffer = NtSystem.Buffer(128 * 1024);

    // Shared between threads.
    private volatile bool _isShown;
    private volatile int _needs;
    private BreakdownSample? _pending;

    // UI thread.
    private readonly Scans _scans = new();
    private readonly List<(string Key, string Name)> _path = [];
    private BreakdownSample? _latest;
    private Level? _level;
    private readonly Dictionary<string, int> _rank = [];
    private long _rankedAt;
    private string _title = "Physical memory";
    private string _description = string.Empty;
    private string _totalText = "—";
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _canRefresh;
    private bool _showComposition = true;
    private bool _showShare = true;
    private string _sizeHeader = "In RAM";
    private IReadOnlyList<CompositionSegment> _composition = [];
    private bool _isEmpty;

    internal MemoryBreakdown()
    {
        _scans.Completed += Render;
        RefreshCommand = new RelayCommand(Refresh);
        RebuildCrumbs();
    }

    public ObservableCollection<BreakdownCrumb> Crumbs { get; } = [];

    /// <summary>At the top level the only crumb would repeat the title.</summary>
    public bool HasCrumbs => _path.Count > 0;

    public ObservableCollection<BreakdownRow> Rows { get; } = [];

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Description
    {
        get => _description;
        private set => Set(ref _description, value);
    }

    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0 || _canRefresh;

    /// <summary>The status describes work in progress (it ends with an ellipsis).</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>The level shows a one-off scan that can be read again.</summary>
    public bool CanRefresh
    {
        get => _canRefresh;
        private set
        {
            if (Set(ref _canRefresh, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool ShowComposition
    {
        get => _showComposition;
        private set => Set(ref _showComposition, value);
    }

    public bool ShowShare
    {
        get => _showShare;
        private set => Set(ref _showShare, value);
    }

    public string SizeHeader
    {
        get => _sizeHeader;
        private set => Set(ref _sizeHeader, value);
    }

    public IReadOnlyList<CompositionSegment> Composition
    {
        get => _composition;
        private set => Set(ref _composition, value);
    }

    /// <summary>Nothing to list and nothing pending.</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public ICommand RefreshCommand { get; }

    /// <summary>Set by the view as it enters and leaves the screen.</summary>
    internal bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value)
                return;

            _isShown = value;
            if (value)
                SampleNow();
        }
    }

    /// <summary>Sampling thread.</summary>
    internal void Sample()
    {
        if (!_isShown)
            return;

        var needs = (BreakdownNeeds)_needs;
        BreakdownSample sample;
        lock (_sampleLock)
        {
            var inUse = needs.HasFlag(BreakdownNeeds.InUse);
            sample = new BreakdownSample
            {
                TotalBytes = SystemPerformance.Read().PhysicalTotalBytes,
                Lists = PageLists.Read(ref _listBuffer),
                Kernel = inUse ? _kernel.Read() : null,
                Processes = inUse ? ProcessMemory.Read(ref _processBuffer) : null,
                Tags = needs.HasFlag(BreakdownNeeds.Tags) ? _tags.Read() : null,
                Modules = needs.HasFlag(BreakdownNeeds.Modules) ? KernelModules.Read(ref _moduleBuffer) : null,
            };
        }

        Volatile.Write(ref _pending, sample);
    }

    /// <summary>UI thread.</summary>
    internal void Apply()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } sample)
            return;

        _latest = sample;
        Render();
    }

    /// <summary>Samples off the clock so a newly opened level fills in at once rather than on the next tick.</summary>
    private void SampleNow() => Task.Run(() =>
    {
        Sample();
        Dispatcher.UIThread.Post(Apply);
    });

    private void Open(BreakdownRow row)
    {
        if (!row.CanOpen)
            return;

        _path.Add((row.Key, row.Name));
        Navigated();
    }

    private void NavigateTo(int depth)
    {
        if (depth >= _path.Count)
            return;

        _path.RemoveRange(depth, _path.Count - depth);
        Navigated();
    }

    private void Navigated()
    {
        _rank.Clear();
        Rows.Clear();
        RebuildCrumbs();

        var needs = BreakdownTree.Needs(Keys());
        var had = (BreakdownNeeds)_needs;
        _needs = (int)needs;

        // The last sample may not have read what this level needs. A sample takes a few
        // milliseconds, so take it now rather than flash an empty level until the next tick.
        if ((needs & ~had) != 0)
        {
            Sample();
            Apply();
        }
        else
        {
            Render();
        }
    }

    private void Refresh()
    {
        if (_level?.ScanKey is { } key)
        {
            _scans.Forget(key);
            _rank.Clear();
            Render();
        }
    }

    private string[] Keys() => [.. _path.Select(p => p.Key)];

    private void Render()
    {
        if (_latest is not { } sample)
            return;

        _scans.BeginPass();
        Level? level;
        var popped = false;
        while ((level = BreakdownTree.Resolve(Keys(), sample, _scans)) is null && _path.Count > 0)
        {
            // What this path pointed at is gone (the process exited): go up until something is there.
            _path.RemoveAt(_path.Count - 1);
            popped = true;
        }

        _scans.EndPass();

        if (popped)
        {
                _rank.Clear();
            Rows.Clear();
            RebuildCrumbs();
            _needs = (int)BreakdownTree.Needs(Keys());
        }

        if (level is null)
            return;

        _level = level;
        Title = level.Title;
        Description = level.Description;
        TotalText = Format.Bytes(level.Bytes);
        Status = level.Status;
        IsBusy = level.IsBusy;
        CanRefresh = level.ScanKey is not null;
        ShowComposition = level.ShowComposition;
        ShowShare = level.ShowShare;
        SizeHeader = level.SizeHeader;

        var all = Order(level);
        IsEmpty = all.Count == 0 && !level.IsBusy;

        var basis = Math.Max(level.Bytes, all.Sum(e => e.Bytes));
        Reconcile(all, basis);

        if (level.ShowComposition && basis > 0)
            Composition = [.. all.Select(e => new CompositionSegment(e.Bytes / basis, ThemeResource.Brush(e.SeriesKey ?? "SlSeries1")))];
    }

    /// <summary>Largest first as of the last re-sort; anything new goes below, largest first.</summary>
    private List<Entry> Order(Level level)
    {
        var entries = level.Entries.Where(e => !e.Optional || e.Bytes > 0);
        if (!level.Sorted)
            return [.. entries];

        var now = Environment.TickCount64;
        if (_rank.Count > 0 && now - _rankedAt < ResortMilliseconds)
            return [.. entries.OrderBy(e => _rank.GetValueOrDefault(e.Key, int.MaxValue)).ThenByDescending(e => e.Bytes)];

        var sorted = entries.OrderByDescending(e => e.Bytes).ToList();
        _rank.Clear();
        for (var i = 0; i < sorted.Count; i++)
            _rank.TryAdd(sorted[i].Key, i);
        _rankedAt = now;
        return sorted;
    }

    /// <summary>
    /// Update rows in place by key so the list doesn't rebuild every tick. Between re-sorts the
    /// order is unchanged, so this is one pass of property updates even for thousands of rows.
    /// </summary>
    private void Reconcile(List<Entry> entries, double basis)
    {
        var existing = new Dictionary<string, BreakdownRow>(Rows.Count);
        foreach (var row in Rows)
            existing.TryAdd(row.Key, row);

        var keep = new HashSet<string>(entries.Count);
        foreach (var entry in entries)
            keep.Add(entry.Key);

        // Drop rows that are gone first, from the end, so the moves below are short.
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i].Key))
                Rows.RemoveAt(i);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!existing.TryGetValue(entry.Key, out var row))
                row = new BreakdownRow(entry.Key, Open);

            row.Update(entry, basis > 0 ? entry.Bytes / basis : 0);

            if (i < Rows.Count && ReferenceEquals(Rows[i], row))
                continue;

            var current = existing.ContainsKey(entry.Key) ? IndexFrom(row, i) : -1;
            if (current >= 0)
                Rows.Move(current, i);
            else
                Rows.Insert(i, row);
        }
    }

    /// <summary>Position of <paramref name="row"/>, which is known to be at or after <paramref name="start"/>.</summary>
    private int IndexFrom(BreakdownRow row, int start)
    {
        for (var i = start; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], row))
                return i;
        }

        return -1;
    }

    private void RebuildCrumbs()
    {
        Crumbs.Clear();
        Crumbs.Add(new BreakdownCrumb("Physical memory", 0, _path.Count == 0, NavigateTo));
        for (var i = 0; i < _path.Count; i++)
            Crumbs.Add(new BreakdownCrumb(_path[i].Name, i + 1, i == _path.Count - 1, NavigateTo));
        OnPropertyChanged(nameof(HasCrumbs));
    }

    public void Dispose()
    {
        lock (_sampleLock)
            _kernel.Dispose();
    }
}

/// <summary>One row of the breakdown table, updated in place every tick.</summary>
public sealed class BreakdownRow : ObservableObject
{
    private string _name = string.Empty;
    private string _note = string.Empty;
    private string _sizeText = string.Empty;
    private string _shareText = string.Empty;
    private double _share;
    private string? _seriesKey;
    private IBrush? _series;
    private bool _canOpen;
    private bool _mono;
    private string? _toolTip;
    private double _bytes = double.NaN;

    internal BreakdownRow(string key, Action<BreakdownRow> open)
    {
        Key = key;
        OpenCommand = new RelayCommand(() => open(this));
    }

    public string Key { get; }

    public ICommand OpenCommand { get; }

    public string Name
    {
        get => _name;
        private set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string Note
    {
        get => _note;
        private set => Set(ref _note, value);
    }

    public bool HasNote => _note.Length > 0;

    public string SizeText
    {
        get => _sizeText;
        private set
        {
            if (Set(ref _sizeText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>Fraction of the level's total, 0–1.</summary>
    public double Share
    {
        get => _share;
        private set => Set(ref _share, value);
    }

    public string ShareText
    {
        get => _shareText;
        private set => Set(ref _shareText, value);
    }

    /// <summary>Legend swatch on levels with a composition bar; also colours the share bar.</summary>
    public IBrush? Series
    {
        get => _series;
        private set
        {
            if (Set(ref _series, value))
                OnPropertyChanged(nameof(HasSwatch));
        }
    }

    public bool HasSwatch => _seriesKey is not null;

    public bool CanOpen
    {
        get => _canOpen;
        private set => Set(ref _canOpen, value);
    }

    public bool Mono
    {
        get => _mono;
        private set => Set(ref _mono, value);
    }

    public string? ToolTip
    {
        get => _toolTip;
        private set => Set(ref _toolTip, value);
    }

    public string AccessibleName => $"{_name}, {_sizeText}";

    internal void Update(Entry entry, double share)
    {
        Name = entry.Name;
        if (Set(ref _note, entry.Note, nameof(Note)))
            OnPropertyChanged(nameof(HasNote));

        // Most rows of a long list don't change from one tick to the next.
        if (entry.Bytes != _bytes || share != _share)
        {
            _bytes = entry.Bytes;
            SizeText = Format.Bytes(entry.Bytes);
            Share = share;
            ShareText = share is > 0 and < 0.005 ? "<1%" : Format.Percent(share * 100);
        }

        if (_seriesKey != entry.SeriesKey || _series is null)
        {
            _seriesKey = entry.SeriesKey;
            Series = ThemeResource.Brush(entry.SeriesKey ?? "SlSeries1");
        }

        CanOpen = entry.CanOpen;
        Mono = entry.Mono;
        ToolTip = entry.ToolTip;
    }
}

/// <summary>A breadcrumb: every level above the current one is a link back to it.</summary>
public sealed class BreakdownCrumb
{
    internal BreakdownCrumb(string name, int depth, bool isCurrent, Action<int> navigate)
    {
        Name = name;
        IsCurrent = isCurrent;
        ShowSeparator = depth > 0;
        NavigateCommand = new RelayCommand(() => navigate(depth));
    }

    public string Name { get; }

    public bool IsCurrent { get; }

    public bool IsLink => !IsCurrent;

    public bool ShowSeparator { get; }

    public ICommand NavigateCommand { get; }

    public override string ToString() => Name;
}
