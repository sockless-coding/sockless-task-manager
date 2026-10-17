using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// The Processes page's state: every running process as a flat list or as a tree of who started
/// whom, narrowed by a search and filters, sorted by a column, plus the processes the user follows.
/// The table only updates while the page is on screen; followed processes record all the time.
/// </summary>
public sealed class ProcessList : ObservableObject
{
    /// <summary>
    /// Rows keep their order between re-sorts, so the list doesn't reshuffle under the pointer
    /// every tick. Changing the sort, search or filters re-sorts at once.
    /// </summary>
    private const long ResortMilliseconds = 3000;

    // Sampling thread (and the occasional off-tick sample when the page opens); guarded by _sampleLock.
    private readonly Lock _sampleLock = new();
    private readonly ProcessSampler _sampler = new();

    // Shared between threads.
    private volatile bool _isShown;
    private volatile bool _hasFollowed;
    private ProcessSnapshot? _pending;

    // UI thread.
    private readonly int _sessionId = CurrentSessionId();
    private readonly Dictionary<ProcessKey, FollowedProcess> _followed = [];
    private readonly HashSet<ProcessKey> _collapsed = [];
    private readonly Dictionary<ProcessKey, double> _sortValues = [];
    private readonly SortColumn[] _columns;
    private ProcessSnapshot? _latest;
    private long _sortedAt;
    private SortColumn _sortColumn;
    private bool _descending;
    private string _searchText = string.Empty;
    private bool _isTree;
    private bool _followedOnly;
    private bool _thisSessionOnly;
    private string _summary = string.Empty;
    private bool _isEmpty;

    internal ProcessList()
    {
        NameColumn = new SortColumn(ProcessColumn.Name, "Name", SortBy);
        IdColumn = new SortColumn(ProcessColumn.Id, "PID", SortBy);
        CpuColumn = new SortColumn(ProcessColumn.Cpu, "CPU", SortBy);
        MemoryColumn = new SortColumn(ProcessColumn.Memory, "Memory", SortBy);
        IoColumn = new SortColumn(ProcessColumn.Io, "I/O", SortBy);
        _columns = [NameColumn, IdColumn, CpuColumn, MemoryColumn, IoColumn];

        _sortColumn = CpuColumn;
        _descending = true;
        ShowSort();

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    public ObservableCollection<ProcessRow> Rows { get; } = [];

    public ObservableCollection<FollowedProcess> Followed { get; } = [];

    public bool HasFollowed => Followed.Count > 0;

    public SortColumn NameColumn { get; }

    public SortColumn IdColumn { get; }

    public SortColumn CpuColumn { get; }

    public SortColumn MemoryColumn { get; }

    public SortColumn IoColumn { get; }

    public ICommand ClearSearchCommand { get; }

    /// <summary>Matches names and paths (any part, any case) and process ids (from the start).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearch));
                Changed();
            }
        }
    }

    public bool HasSearch => _searchText.Length > 0;

    public bool IsTree
    {
        get => _isTree;
        set
        {
            if (Set(ref _isTree, value))
            {
                OnPropertyChanged(nameof(IsFlat));
                Changed();
            }
        }
    }

    public bool IsFlat
    {
        get => !_isTree;
        set => IsTree = !value;
    }

    public bool FollowedOnly
    {
        get => _followedOnly;
        set
        {
            if (Set(ref _followedOnly, value))
                Changed();
        }
    }

    /// <summary>Only processes in the user's own Windows session: hides services and system processes.</summary>
    public bool ThisSessionOnly
    {
        get => _thisSessionOnly;
        set
        {
            if (Set(ref _thisSessionOnly, value))
                Changed();
        }
    }

    /// <summary>Process and thread counts, or how many of them match.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (Set(ref _isEmpty, value))
                OnPropertyChanged(nameof(EmptyTitle));
        }
    }

    public string EmptyTitle => _followedOnly && _followed.Count == 0
        ? "You aren't following any processes yet"
        : "No processes match";

    public string EmptyHint => _followedOnly && _followed.Count == 0
        ? "Select the star on a row to follow that process."
        : "Try a different search, or turn off a filter.";

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

    private bool IsFiltering => _searchText.Length > 0 || _followedOnly || _thisSessionOnly;

    /// <summary>Sampling thread.</summary>
    internal void Sample()
    {
        if (!_isShown && !_hasFollowed)
            return;

        ProcessSnapshot snapshot;
        lock (_sampleLock)
            snapshot = _sampler.Read();

        Volatile.Write(ref _pending, snapshot);
    }

    /// <summary>UI thread.</summary>
    internal void Apply()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot)
            return;

        _latest = snapshot;
        UpdateFollowed(snapshot);
        if (_isShown)
            Render();
    }

    /// <summary>Samples off the clock so the page fills in at once rather than on the next tick.</summary>
    private void SampleNow() => Task.Run(() =>
    {
        Sample();
        Dispatcher.UIThread.Post(Apply);
    });

    private void SortBy(SortColumn column)
    {
        _descending = ReferenceEquals(column, _sortColumn) ? !_descending : column.DescendingFirst;
        _sortColumn = column;
        ShowSort();
        Changed();
    }

    private void ShowSort()
    {
        foreach (var column in _columns)
            column.Show(ReferenceEquals(column, _sortColumn), _descending);
    }

    private void Follow(ProcessKey key)
    {
        if (_followed.Remove(key, out var existing))
        {
            Followed.Remove(existing);
        }
        else if (_latest?.ByKey.GetValueOrDefault(key) is { } reading)
        {
            var followed = new FollowedProcess(reading, Unfollow);
            _followed[key] = followed;
            Followed.Add(followed);
        }

        FollowedChanged();
    }

    private void Unfollow(ProcessKey key)
    {
        if (_followed.Remove(key, out var existing))
            Followed.Remove(existing);

        FollowedChanged();
    }

    private void FollowedChanged()
    {
        _hasFollowed = _followed.Count > 0;
        OnPropertyChanged(nameof(HasFollowed));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        Render();
    }

    private void Toggle(ProcessKey key)
    {
        if (!_collapsed.Remove(key))
            _collapsed.Add(key);

        Render();
    }

    private void UpdateFollowed(ProcessSnapshot snapshot)
    {
        foreach (var followed in Followed)
        {
            if (!followed.IsRunning)
                continue;

            if (snapshot.ByKey.TryGetValue(followed.Key, out var reading))
                followed.Update(reading);
            else
                followed.MarkExited();
        }
    }

    /// <summary>The search, filters or sort changed: show the result now, freshly sorted.</summary>
    private void Changed()
    {
        _sortValues.Clear();
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        Render();
    }

    private void Render()
    {
        if (_latest is not { } snapshot)
            return;

        var search = _searchText.Trim();
        var matches = new HashSet<ProcessKey>();
        foreach (var process in snapshot.Processes)
        {
            if (Matches(process, search))
                matches.Add(process.Key);
        }

        RefreshSortValues(snapshot);

        var rows = new List<(ProcessReading Reading, RowPlacement Placement)>(matches.Count);
        if (_isTree)
        {
            BuildTree(snapshot, matches, rows);
        }
        else
        {
            var flat = snapshot.Processes.Where(p => matches.Contains(p.Key)).ToList();
            flat.Sort(Compare);
            foreach (var process in flat)
                rows.Add((process, new RowPlacement(0, false, false, false)));
        }

        Reconcile(rows);

        var total = snapshot.Processes.Count;
        Summary = IsFiltering
            ? $"{Format.Number(matches.Count)} of {Format.Number(total)} processes match"
            : $"{Format.Number(total)} processes, {Format.Number(snapshot.Threads)} threads";
        IsEmpty = rows.Count == 0;
    }

    private bool Matches(ProcessReading process, string search)
    {
        if (_thisSessionOnly && process.SessionId != _sessionId)
            return false;
        if (_followedOnly && !_followed.ContainsKey(process.Key))
            return false;
        if (search.Length == 0)
            return true;

        return process.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
               || (process.Path?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
               || process.Key.Id.ToString(CultureInfo.InvariantCulture).StartsWith(search, StringComparison.Ordinal);
    }

    /// <summary>
    /// Depth-first from the processes whose parent is gone. While searching or filtering, the
    /// tree keeps each match's ancestors as context, so it still shows what started it.
    /// </summary>
    private void BuildTree(ProcessSnapshot snapshot, HashSet<ProcessKey> matches, List<(ProcessReading, RowPlacement)> rows)
    {
        var filtering = IsFiltering;
        var included = matches;
        if (filtering)
        {
            included = [.. matches];
            foreach (var key in matches)
            {
                var parent = snapshot.ByKey[key].Parent;
                while (parent is { } p && snapshot.ByKey.TryGetValue(p, out var up) && included.Add(p))
                    parent = up.Parent;
            }
        }

        var children = new Dictionary<ProcessKey, List<ProcessReading>>();
        var roots = new List<ProcessReading>();
        foreach (var process in snapshot.Processes)
        {
            if (!included.Contains(process.Key))
                continue;

            if (process.Parent is { } parent && included.Contains(parent))
            {
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = [];
                siblings.Add(process);
            }
            else
            {
                roots.Add(process);
            }
        }

        var visited = new HashSet<ProcessKey>();

        void Walk(ProcessReading process, int depth)
        {
            if (!visited.Add(process.Key))
                return;

            var kids = children.GetValueOrDefault(process.Key);
            var expanded = !_collapsed.Contains(process.Key);
            var isContext = filtering && !matches.Contains(process.Key);
            rows.Add((process, new RowPlacement(depth, kids is { Count: > 0 }, expanded, isContext)));

            if (kids is null || !expanded)
                return;

            kids.Sort(Compare);
            foreach (var kid in kids)
                Walk(kid, depth + 1);
        }

        roots.Sort(Compare);
        foreach (var root in roots)
            Walk(root, 0);

        // Parent links only point back in time, so there are no cycles, unless two processes
        // claim the same start time. Anything a cycle hid is listed at the top level.
        foreach (var process in snapshot.Processes)
        {
            if (included.Contains(process.Key) && !visited.Contains(process.Key) && !IsInsideCollapsed(process, snapshot))
                Walk(process, 0);
        }
    }

    private static int CurrentSessionId()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.SessionId;
    }

    private bool IsInsideCollapsed(ProcessReading process, ProcessSnapshot snapshot)
    {
        var seen = new HashSet<ProcessKey>();
        var parent = process.Parent;
        while (parent is { } p && seen.Add(p) && snapshot.ByKey.TryGetValue(p, out var up))
        {
            if (_collapsed.Contains(p))
                return true;
            parent = up.Parent;
        }

        return false;
    }

    private int Compare(ProcessReading a, ProcessReading b)
    {
        var order = _sortColumn.Column switch
        {
            ProcessColumn.Name => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            ProcessColumn.Id => a.Key.Id.CompareTo(b.Key.Id),
            _ => SortValue(a).CompareTo(SortValue(b)),
        };

        if (_descending)
            order = -order;
        if (order == 0)
            order = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : a.Key.Id.CompareTo(b.Key.Id);
    }

    private double Value(ProcessReading process) => _sortColumn.Column switch
    {
        ProcessColumn.Cpu => process.Cpu,
        ProcessColumn.Memory => process.Memory,
        ProcessColumn.Io => process.IoRate,
        _ => 0,
    };

    /// <summary>The value a row is sorted by: as of the last re-sort, or live for processes new since then.</summary>
    private double SortValue(ProcessReading process) =>
        _sortValues.TryGetValue(process.Key, out var value) ? value : Value(process);

    private void RefreshSortValues(ProcessSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        if (_sortValues.Count > 0 && now - _sortedAt < ResortMilliseconds)
            return;

        // Don't hold an order taken before there were any rates: everything would read 0%.
        if (!snapshot.HasRates)
        {
            _sortValues.Clear();
            return;
        }

        _sortValues.Clear();
        foreach (var process in snapshot.Processes)
            _sortValues[process.Key] = Value(process);
        _sortedAt = now;
    }

    /// <summary>
    /// Update rows in place by key so the list doesn't rebuild every tick. Between re-sorts the
    /// order is unchanged, so this is one pass of property updates.
    /// </summary>
    private void Reconcile(List<(ProcessReading Reading, RowPlacement Placement)> entries)
    {
        var existing = new Dictionary<ProcessKey, ProcessRow>(Rows.Count);
        foreach (var row in Rows)
            existing.TryAdd(row.Key, row);

        var keep = new HashSet<ProcessKey>(entries.Count);
        foreach (var entry in entries)
            keep.Add(entry.Reading.Key);

        // Drop rows that are gone first, from the end, so the moves below are short.
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i].Key))
                Rows.RemoveAt(i);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var (reading, placement) = entries[i];
            if (!existing.TryGetValue(reading.Key, out var row))
                row = new ProcessRow(reading.Key, Follow, Toggle);

            row.Update(reading, placement, _isTree, _followed.ContainsKey(reading.Key));

            if (i < Rows.Count && ReferenceEquals(Rows[i], row))
                continue;

            var current = existing.ContainsKey(reading.Key) ? IndexFrom(row, i) : -1;
            if (current >= 0)
                Rows.Move(current, i);
            else
                Rows.Insert(i, row);
        }
    }

    /// <summary>Position of <paramref name="row"/>, which is known to be at or after <paramref name="start"/>.</summary>
    private int IndexFrom(ProcessRow row, int start)
    {
        for (var i = start; i < Rows.Count; i++)
        {
            if (ReferenceEquals(Rows[i], row))
                return i;
        }

        return -1;
    }
}
