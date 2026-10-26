using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Features.Processes.Dotnet;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// The Processes page's state: every running process as a flat list or as a tree of who started
/// whom, narrowed by a search and filters, sorted by a column, plus the processes the user follows.
/// Selecting a process opens its details, where it can be ended, suspended or reprioritised.
/// The Ended view lists processes that have exited, the short-lived ones included.
/// The table only updates while the page is on screen; followed processes record all the time.
/// </summary>
public sealed class ProcessList : ObservableObject, IDisposable
{
    /// <summary>
    /// Rows keep their order between re-sorts, so the list doesn't reshuffle under the pointer
    /// every tick. Changing the sort, search or filters re-sorts at once.
    /// </summary>
    private const long ResortMilliseconds = 3000;

    /// <summary>
    /// Without the trace, exits are found by comparing consecutive samples. Across a longer gap
    /// (the page was hidden) the exit time is unknown, so that comparison is skipped.
    /// </summary>
    private const long MaxSampleGapMilliseconds = 2500;

    private const int MaxEnded = 1000;

    // Sampling thread (and the occasional off-tick sample when the page opens); guarded by _sampleLock.
    private readonly Lock _sampleLock = new();
    private readonly ProcessSampler _sampler = new();

    // Shared between threads.
    private volatile bool _isShown;
    private volatile bool _hasFollowed;
    private ProcessSnapshot? _pending;

    // UI thread.
    private readonly ProcessTrace? _trace;
    private readonly int _sessionId = CurrentSessionId();
    private readonly Dictionary<ProcessKey, FollowedProcess> _followed = [];
    private readonly HashSet<ProcessKey> _collapsed = [];
    private readonly Dictionary<ProcessKey, double> _sortValues = [];
    private readonly TableColumn[] _columns;
    private readonly ProcessSettings _settings = ProcessSettings.Load();
    private TableColumn[] _filtered = [];
    private readonly List<EndedProcess> _ended = [];
    private readonly Dictionary<ProcessKey, EndedProcess> _endedByKey = [];
    private readonly DateTime _recordingSince = DateTime.Now;
    private ProcessSnapshot? _latest;
    private long _sortedAt;
    private TableColumn _sortColumn;
    private bool _descending;
    private string _searchText = string.Empty;
    private bool _isTree;
    private bool _followedOnly;
    private bool _thisSessionOnly;
    private bool _dotnetOnly;
    private bool _showEnded;
    private bool _shortLivedOnly;
    private bool _isTracing;
    private bool _showColumnFilters;
    private string _summary = string.Empty;
    private bool _isEmpty;
    private ProcessDetails? _details;
    private ManagedMemory? _managedPage;
    private Confirmation? _confirmation;

    internal ProcessList()
    {
        // Every column the table can show, in order. Name and PID are always shown.
        // Saved choices only cover the columns that can be hidden.
        TableColumn Column(ProcessColumn column, string title, double width, ColumnKind kind, bool shown, string? hint = null) =>
            new(column, title, width, kind,
                column is ProcessColumn.Name or ProcessColumn.Id || (_settings.Columns?.Contains(column) ?? shown), hint, this);

        NameColumn = Column(ProcessColumn.Name, "Name", 0, ColumnKind.Text, true);
        var cpu = Column(ProcessColumn.Cpu, "CPU", 64, ColumnKind.Percent, true, "e.g. > 1");
        _columns =
        [
            NameColumn,
            Column(ProcessColumn.Id, "PID", 64, ColumnKind.Id, true, "e.g. 1234"),
            Column(ProcessColumn.User, "User", 120, ColumnKind.Text, true),
            cpu,
            Column(ProcessColumn.Memory, "Memory", 88, ColumnKind.Bytes, true, "e.g. > 100 MB"),
            Column(ProcessColumn.Io, "I/O", 88, ColumnKind.BytesPerSecond, true, "e.g. > 1 MB/s"),
            Column(ProcessColumn.Threads, "Threads", 64, ColumnKind.Count, false),
            Column(ProcessColumn.Handles, "Handles", 72, ColumnKind.Count, false),
            Column(ProcessColumn.WorkingSet, "Working set", 96, ColumnKind.Bytes, false, "e.g. > 100 MB"),
            Column(ProcessColumn.Commit, "Commit", 88, ColumnKind.Bytes, false, "e.g. > 100 MB"),
            Column(ProcessColumn.Priority, "Priority", 96, ColumnKind.Label, false),
            Column(ProcessColumn.Session, "Session", 64, ColumnKind.Id, false, "e.g. 1"),
            Column(ProcessColumn.Started, "Started", 128, ColumnKind.Label, false),
            Column(ProcessColumn.Description, "Description", 200, ColumnKind.Text, false),
            Column(ProcessColumn.Path, "Path", 260, ColumnKind.Text, false),
            Column(ProcessColumn.CommandLine, "Command line", 320, ColumnKind.Text, false),
        ];
        HideableColumns = [.. _columns.Where(c => c.CanHide)];
        ShowVisibleColumns();

        _sortColumn = _columns.FirstOrDefault(c => c.Column == _settings.SortColumn && (c.IsVisible || c == NameColumn)) ?? cpu;
        _descending = _settings.SortColumn is null ? true : _settings.SortDescending;
        ShowSort();

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        ClearEndedCommand = new RelayCommand(ClearEnded);

        _trace = ProcessTrace.TryStart();
        _isTracing = _trace is not null;
    }

    public ObservableCollection<ProcessRow> Rows { get; } = [];

    public ObservableCollection<EndedProcess> EndedRows { get; } = [];

    public ObservableCollection<FollowedProcess> Followed { get; } = [];

    public bool HasFollowed => Followed.Count > 0;

    /// <summary>The first column, which takes the width the others leave.</summary>
    public TableColumn NameColumn { get; }

    /// <summary>The shown columns after the name, in order.</summary>
    public ObservableCollection<TableColumn> VisibleColumns { get; } = [];

    /// <summary>The column chooser's list: every column but Name.</summary>
    public IReadOnlyList<TableColumn> HideableColumns { get; }

    /// <summary>
    /// Narrower than this and the Name column would be squeezed out, so the table scrolls
    /// sideways instead: a minimum name width, the shown columns, the star, spacing and padding.
    /// </summary>
    public double TableMinWidth => 200 + VisibleColumns.Sum(c => c.Width + 16) + 16 + 24 + 48;

    /// <summary>The row of filter boxes under the column titles. Hiding it clears the filters.</summary>
    public bool ShowColumnFilters
    {
        get => _showColumnFilters;
        set
        {
            if (!Set(ref _showColumnFilters, value) || value)
                return;

            foreach (var column in _columns)
                column.FilterText = string.Empty;
        }
    }

    public int ColumnFilterCount => _filtered.Length;

    public bool HasColumnFilters => _filtered.Length > 0;

    public string ColumnFiltersToolTip => _filtered.Length == 0
        ? "Filter by column"
        : $"Filtering by {string.Join(", ", _filtered.Select(c => c.Title))}. Turn off to clear.";

    public ICommand ClearSearchCommand { get; }

    public ICommand ClearEndedCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

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

    /// <summary>Show the processes that have ended, rather than the running ones.</summary>
    public bool IsEndedView
    {
        get => _showEnded;
        set
        {
            if (Set(ref _showEnded, value))
            {
                OnPropertyChanged(nameof(IsRunningView));
                Changed();
            }
        }
    }

    public bool IsRunningView
    {
        get => !_showEnded;
        set => IsEndedView = !value;
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

    /// <summary>Only .NET processes with a diagnostics port: the ones the details can say more about.</summary>
    public bool DotnetOnly
    {
        get => _dotnetOnly;
        set
        {
            if (Set(ref _dotnetOnly, value))
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

    /// <summary>In the Ended view, only processes that ran for less than <see cref="EndedProcess.ShortLived"/>.</summary>
    public bool ShortLivedOnly
    {
        get => _shortLivedOnly;
        set
        {
            if (Set(ref _shortLivedOnly, value))
                Changed();
        }
    }

    public string ShortLivedToolTip =>
        $"Only processes that ran for less than {EndedProcess.Duration(EndedProcess.ShortLived)}";

    /// <summary>Every start and exit is traced (elevated), rather than inferred from samples.</summary>
    public bool IsTracing
    {
        get => _isTracing;
        private set
        {
            if (Set(ref _isTracing, value))
            {
                OnPropertyChanged(nameof(ShowTraceHint));
                OnPropertyChanged(nameof(EndedEmptyHint));
            }
        }
    }

    public bool ShowTraceHint => !_isTracing;

    /// <summary>Why the Ended list may be missing processes, and what fixes that.</summary>
    public string TraceHint => Elevation.IsElevated
        ? "Windows refused to start a process trace, so this list only has processes that ran for at least a second while this page was open or a process was followed."
        : "Only processes that ran for at least a second, while this page was open or a process was followed, are listed. Run as administrator to catch every process, even ones that last a few milliseconds, all the time.";

    public bool CanRestartElevated => !Elevation.IsElevated;

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
        : _filtered.Length > 0
            ? "Try a different search, or loosen a column filter."
            : "Try a different search, or turn off a filter.";

    public bool IsEndedEmpty => EndedRows.Count == 0;

    public bool HasEnded => _ended.Count > 0;

    public string EndedEmptyTitle => _ended.Count == 0 ? "No processes have ended yet" : "No ended processes match";

    public string EndedEmptyHint => _ended.Count > 0
        ? "Try a different search, or turn off a filter."
        : _isTracing
            ? "Processes that end are listed here, newest first."
            : "Processes that end while this page is open are listed here, newest first.";

    /// <summary>The selected process, shown in the details pane.</summary>
    public ProcessDetails? Details
    {
        get => _details;
        private set
        {
            var previous = _details;
            if (Set(ref _details, value))
            {
                previous?.Watch(false);
                value?.Watch(_isShown);
                ManagedPage = null;
                OnPropertyChanged(nameof(HasDetails));
                MarkSelection();
            }
        }
    }

    public bool HasDetails => _details is not null;

    /// <summary>The .NET memory page, open over the table for the selected process.</summary>
    public ManagedMemory? ManagedPage
    {
        get => _managedPage;
        private set
        {
            var previous = _managedPage;
            if (!Set(ref _managedPage, value))
                return;

            if (previous is not null)
                previous.IsPageOpen = false;
            if (value is not null)
                value.IsPageOpen = true;
            OnPropertyChanged(nameof(HasManagedPage));
        }
    }

    public bool HasManagedPage => _managedPage is not null;

    /// <summary>A destructive action waiting for the user to confirm it.</summary>
    public Confirmation? Confirmation
    {
        get => _confirmation;
        private set
        {
            if (Set(ref _confirmation, value))
                OnPropertyChanged(nameof(HasConfirmation));
        }
    }

    public bool HasConfirmation => _confirmation is not null;

    /// <summary>Set by the view as it enters and leaves the screen.</summary>
    internal bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value)
                return;

            _isShown = value;
            _details?.Watch(value);
            if (value)
                SampleNow();
        }
    }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    internal Action<string>? CopyText { get; set; }

    /// <summary>Set by the view: asks where to save a dump, suggesting a file name. Null if cancelled.</summary>
    internal Func<string, Task<string?>>? PickDumpPath { get; set; }

    private bool IsFiltering => _searchText.Length > 0 || _followedOnly || _dotnetOnly || _thisSessionOnly || _filtered.Length > 0;

    private bool IsFilteringEnded => _searchText.Length > 0 || _shortLivedOnly || _thisSessionOnly;

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
        // The trace records whether or not anything is sampled.
        var endedChanged = DrainTrace();

        if (Interlocked.Exchange(ref _pending, null) is { } snapshot)
        {
            if (!_isTracing && _latest is { } previous && snapshot.TakenAt - previous.TakenAt <= MaxSampleGapMilliseconds)
                endedChanged |= RecordExits(previous, snapshot);

            _latest = snapshot;
            UpdateFollowed(snapshot);
            UpdateDetails(snapshot);
            if (_isShown && !_showEnded)
                Render();
        }

        if (endedChanged)
            EndedChanged();

        // The process ended: there's no runtime left to show.
        if (_managedPage is not null && !ReferenceEquals(_details?.Managed, _managedPage))
            ManagedPage = null;
    }

    public void Dispose()
    {
        _trace?.Dispose();
        _details?.Watch(false);
    }

    /// <summary>Samples off the clock so the page fills in at once rather than on the next tick.</summary>
    private void SampleNow() => Task.Run(() =>
    {
        Sample();
        Dispatcher.UIThread.Post(Apply);
    });

    // ========== Selection and details ==========

    /// <summary>Opens a running process in the details pane.</summary>
    internal void Select(ProcessKey key)
    {
        if (_latest?.ByKey.GetValueOrDefault(key) is not { } reading)
            return;

        IsEndedView = false;
        if (_details is { IsAlive: true } current && current.Key == key)
            return;

        ShowDetails(new ProcessDetails(reading, ParentText(reading.Parent), this));
    }

    private void SelectEnded(EndedProcess ended)
    {
        if (_details?.Key == ended.Key)
            return;

        ShowDetails(new ProcessDetails(ended, this));
    }

    internal void CloseDetails() => Details = null;

    internal void OpenManaged(ProcessDetails details)
    {
        if (ReferenceEquals(_details, details) && details.Managed is { } managed)
            ManagedPage = managed;
    }

    internal void CloseManaged() => ManagedPage = null;

    internal bool IsRunning(ProcessKey key) => _latest?.ByKey.ContainsKey(key) ?? false;

    /// <summary>
    /// Selects the row <paramref name="delta"/> rows from the selected one in the current table
    /// (arrow keys). Returns its index, or -1 if there are no rows.
    /// </summary>
    internal int MoveSelection(int delta)
    {
        var count = _showEnded ? EndedRows.Count : Rows.Count;
        if (count == 0)
            return -1;

        var current = -1;
        for (var i = 0; i < count && current < 0; i++)
        {
            var key = _showEnded ? EndedRows[i].Key : Rows[i].Key;
            if (key == _details?.Key)
                current = i;
        }

        var index = current < 0 ? (delta > 0 ? 0 : count - 1) : Math.Clamp(current + delta, 0, count - 1);
        if (_showEnded)
            SelectEnded(EndedRows[index]);
        else
            Select(Rows[index].Key);
        return index;
    }

    /// <summary>The selected row in the current table, for the keyboard shortcuts.</summary>
    internal ProcessKey? SelectedKey => _details?.Key;

    private void ShowDetails(ProcessDetails details)
    {
        details.IsFollowed = _followed.ContainsKey(details.Key);
        Details = details;
        LoadFacts(details);
    }

    /// <summary>Command line, user and file description are read off the UI thread, once per selection.</summary>
    private void LoadFacts(ProcessDetails details)
    {
        var (key, name, path, isAlive) = (details.Key, details.Name, details.Path, details.IsAlive);
        Task.Run(() =>
        {
            var facts = ProcessFacts.Read(key, name, path);
            var priority = isAlive ? ProcessControl.Priority(key) : null;
            var affinity = isAlive ? ProcessControl.Affinity(key) : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_details, details))
                    details.ShowFacts(facts, priority, affinity);
            });
        });
    }

    private void UpdateDetails(ProcessSnapshot snapshot)
    {
        if (_details is not { IsAlive: true } details)
            return;

        if (snapshot.ByKey.TryGetValue(details.Key, out var reading))
        {
            details.Update(reading);
            details.SetParent(ParentText(reading.Parent), reading.Parent);
        }
        else
        {
            details.MarkExited(_endedByKey.GetValueOrDefault(details.Key));
        }
    }

    private void MarkSelection()
    {
        var key = _details?.Key;
        foreach (var row in Rows)
            row.IsSelected = row.Key == key;
        foreach (var row in EndedRows)
            row.IsSelected = row.Key == key;
    }

    /// <summary>"explorer.exe (PID 4120)", from the latest sample.</summary>
    private string? ParentText(ProcessKey? parent) =>
        parent is { } key && _latest?.ByKey.GetValueOrDefault(key) is { } reading
            ? $"{reading.Name} (PID {key.Id.ToString(CultureInfo.CurrentCulture)})"
            : null;

    /// <summary>
    /// A traced exit's parent, which may already have ended too (a script and the tools it ran):
    /// the running process with that id that started first, or else an ended one.
    /// </summary>
    private string? ParentText(int parentId, long childCreated)
    {
        if (parentId <= 0)
            return null;

        if (_latest is { } snapshot)
        {
            foreach (var process in snapshot.Processes)
            {
                if (process.Key.Id == parentId && process.Key.CreateTime <= childCreated)
                    return ParentText(process.Key);
            }
        }

        foreach (var ended in _ended)
        {
            if (ended.Key.Id == parentId && ended.Key.CreateTime <= childCreated)
                return $"{ended.Name} (PID {ended.IdText}, ended)";
        }

        return null;
    }

    // ========== Actions ==========

    /// <summary>Asks before ending a process, or a process and everything it started.</summary>
    internal void RequestEnd(ProcessKey key, bool tree)
    {
        if (_latest?.ByKey.GetValueOrDefault(key) is not { } reading)
            return;

        Select(key);
        var targets = tree ? Descendants(reading) : [];
        targets.Add(reading);

        var name = reading.Name;
        var id = key.Id.ToString(CultureInfo.CurrentCulture);
        var others = targets.Count - 1;
        var message = others > 0
            ? $"This ends {name} (PID {id}) and the {Format.Number(others)} {(others == 1 ? "process" : "processes")} it started, directly or through others. Unsaved work in them will be lost."
            : $"This ends {name} (PID {id}) at once. Unsaved work in it will be lost.";

        string? warning = null;
        if (targets.Any(t => ProcessFacts.IsCritical(t.Key, t.Name)))
            warning = "This includes a critical Windows process. Ending it stops Windows with a blue screen, and unsaved work everywhere is lost.";
        else if (targets.Any(t => t.Key.Id == Environment.ProcessId))
            warning = "This includes Sockless Task Manager itself, which will close.";

        Confirmation = new Confirmation(
            others > 0 ? $"End {name} and its processes?" : $"End {name}?",
            message,
            warning,
            tree ? "End process tree" : "End task",
            () =>
            {
                Confirmation = null;
                End(reading, targets);
            },
            () => Confirmation = null);
    }

    /// <summary>Everything below <paramref name="root"/> in the tree, deepest first, so children go before their parents.</summary>
    private List<ProcessReading> Descendants(ProcessReading root)
    {
        var children = new Dictionary<ProcessKey, List<ProcessReading>>();
        foreach (var process in _latest!.Processes)
        {
            if (process.Parent is { } parent)
            {
                if (!children.TryGetValue(parent, out var siblings))
                    children[parent] = siblings = [];
                siblings.Add(process);
            }
        }

        var result = new List<ProcessReading>();
        var seen = new HashSet<ProcessKey> { root.Key };

        void Walk(ProcessKey key)
        {
            foreach (var child in children.GetValueOrDefault(key) ?? [])
            {
                if (!seen.Add(child.Key))
                    continue;
                Walk(child.Key);
                result.Add(child);
            }
        }

        Walk(root.Key);
        return result;
    }

    private void End(ProcessReading root, List<ProcessReading> targets)
    {
        var ended = 0;
        var denied = 0;
        var failed = 0;
        foreach (var target in targets)
        {
            switch (ProcessControl.End(target.Key))
            {
                case Outcome.Done:
                case Outcome.Gone:
                    ended++;
                    break;
                case Outcome.Denied:
                    denied++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        if (_details is not { } details || details.Key != root.Key)
            return;

        if (targets.Count == 1)
        {
            var outcome = denied > 0 ? Outcome.Denied : failed > 0 ? Outcome.Failed : Outcome.Done;
            Report(details, outcome, "ending", root.Name, $"Ended {root.Name}.");
        }
        else if (ended == targets.Count)
        {
            details.ShowNotice($"Ended {root.Name} and {Format.Number(targets.Count - 1)} more.", isError: false);
        }
        else
        {
            var left = denied + failed;
            details.ShowNotice(
                $"Ended {Format.Number(ended)} of {Format.Number(targets.Count)} processes. Windows denied ending {Format.Number(left)} of them{(Elevation.IsElevated ? "." : "; some need administrator rights.")}",
                isError: true,
                offerElevation: denied > 0);
        }
    }

    internal void SuspendOrResume(ProcessKey key)
    {
        if (_latest?.ByKey.GetValueOrDefault(key) is not { } reading)
            return;

        Select(key);
        var suspend = !reading.IsSuspended;
        if (suspend && ProcessFacts.IsUnsafeToSuspend(key, reading.Name, ProcessFacts.IsCritical(key, reading.Name)))
        {
            _details?.ShowNotice($"Suspending {reading.Name} would freeze Windows or this app, so nothing could resume it.", isError: true);
            return;
        }

        var outcome = ProcessControl.Suspend(key, suspend);
        if (_details is { } details)
        {
            Report(details, outcome, suspend ? "suspending" : "resuming", reading.Name,
                suspend ? $"Suspended {reading.Name}. It won't run until you resume it." : $"Resumed {reading.Name}.");
        }

        // Show the new state now rather than on the next tick.
        if (outcome == Outcome.Done)
            SampleNow();
    }

    /// <summary>
    /// Asks where to save, then writes a dump off the UI thread: all memory, or with
    /// <paramref name="heapOnly"/> only the process's own. Windows pauses the process until its
    /// memory is copied, so the notice says how much there is.
    /// </summary>
    internal async void CreateDump(ProcessKey key, bool heapOnly)
    {
        if (_latest?.ByKey.GetValueOrDefault(key) is not { } reading || PickDumpPath is not { } pick)
            return;

        Select(key);
        if (_details is not { IsDumping: false } details || details.Key != key)
            return;
        if (key.Id == Environment.ProcessId)
        {
            details.ShowNotice("This app can't dump itself: it would be paused while it wrote the file.", isError: true);
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var kind = heapOnly ? "_heap" : string.Empty;
        var path = await pick($"{System.IO.Path.GetFileNameWithoutExtension(reading.Name)}_{key.Id}_{stamp}{kind}.dmp");
        if (path is null)
            return;

        details.IsDumping = true;
        details.ShowNotice($"Writing a {(heapOnly ? "heap" : "full")} dump of {reading.Name}. It's paused until its memory is copied, up to {Format.Bytes(reading.Commit)}.", isError: false);
        var outcome = await Task.Run(() => ProcessControl.WriteDump(key, path, heapOnly));
        details.IsDumping = false;

        if (outcome != Outcome.Done)
        {
            Report(details, outcome, "dumping", reading.Name, string.Empty);
            return;
        }

        var size = new FileInfo(path) is { Exists: true } file ? $" ({Format.Bytes(file.Length)})" : string.Empty;
        details.ShowNotice($"Wrote {System.IO.Path.GetFileName(path)}{size}. Open it in Visual Studio, WinDbg or dotnet-dump.",
            isError: false, action: ("Show in folder", new RelayCommand(() => OpenLocation(path))));
    }

    internal void SetPriority(ProcessDetails details, uint priorityClass)
    {
        var outcome = ProcessControl.SetPriority(details.Key, priorityClass);
        var actual = ProcessControl.Priority(details.Key);
        if (actual is { } value)
            details.ShowPriority(value);

        if (outcome != Outcome.Done)
            Report(details, outcome, "changing the priority of", details.Name, string.Empty);
        else if (actual != priorityClass)
            details.ShowNotice($"Windows set {details.Priority?.Title ?? "a different"} priority instead: real-time needs administrator rights.", isError: false, offerElevation: true);
        else
            details.ClearNotice();
    }

    internal void SetAffinity(ProcessDetails details, ulong mask)
    {
        var outcome = ProcessControl.SetAffinity(details.Key, mask);
        if (ProcessControl.Affinity(details.Key) is { } actual)
            details.ShowAffinity(actual.Process);

        if (outcome != Outcome.Done)
            Report(details, outcome, "changing the processors of", details.Name, string.Empty);
        else
            details.ClearNotice();
    }

    private static void Report(ProcessDetails details, Outcome outcome, string doing, string name, string done)
    {
        switch (outcome)
        {
            case Outcome.Done when done.Length > 0:
                details.ShowNotice(done, isError: false);
                break;
            case Outcome.Done:
                details.ClearNotice();
                break;
            case Outcome.Gone:
                details.ShowNotice($"{name} has already exited.", isError: false);
                break;
            case Outcome.Denied:
                details.ShowNotice(
                    Elevation.IsElevated
                        ? $"Windows denied {doing} {name}: it's a protected process."
                        : $"Windows denied {doing} {name}. Processes of other users and of Windows itself need administrator rights.",
                    isError: true,
                    offerElevation: true);
                break;
            default:
                details.ShowNotice($"Windows couldn't finish {doing} {name}.", isError: true);
                break;
        }
    }

    internal void OpenLocation(string? path)
    {
        if (path is null)
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _details?.ShowNotice("Couldn't open the file's folder.", isError: true);
        }
    }

    internal void Copy(string? text)
    {
        if (text is not null)
            CopyText?.Invoke(text);
    }

    // ========== Ended processes ==========

    private bool DrainTrace()
    {
        if (_trace is null)
            return false;

        var changed = false;
        while (_trace.TryDequeue(out var exit))
        {
            if (_endedByKey.ContainsKey(exit.Key))
                continue;

            // A sample names it in full; the stop event alone carries at most 15 characters.
            var reading = _latest?.ByKey.GetValueOrDefault(exit.Key);
            var traced = exit.SessionId >= 0;
            AddEnded(new EndedProcess(
                exit.Key,
                reading?.Name ?? exit.Name ?? "Unknown",
                exit.Path ?? reading?.Path,
                traced ? exit.SessionId : reading?.SessionId ?? -1,
                traced ? ParentText(exit.ParentId, exit.Key.CreateTime) : ParentText(reading?.Parent),
                DateTime.FromFileTimeUtc(exit.ExitTime).ToLocalTime(),
                isExact: true,
                exit.ExitCode,
                exit.PeakCommit,
                SelectEnded));
            changed = true;
        }

        if (!_trace.IsRunning)
            IsTracing = false;
        return changed;
    }

    /// <summary>Processes in <paramref name="previous"/> but not in <paramref name="current"/> exited in between.</summary>
    private bool RecordExits(ProcessSnapshot previous, ProcessSnapshot current)
    {
        var changed = false;
        var noticed = DateTime.Now;
        foreach (var process in previous.Processes)
        {
            if (current.ByKey.ContainsKey(process.Key) || _endedByKey.ContainsKey(process.Key))
                continue;

            AddEnded(new EndedProcess(
                process.Key,
                process.Name,
                process.Path,
                process.SessionId,
                ParentText(process.Parent),
                noticed,
                isExact: false,
                exitCode: null,
                peakCommit: null,
                SelectEnded));
            changed = true;
        }

        return changed;
    }

    private void AddEnded(EndedProcess ended)
    {
        _ended.Insert(0, ended);
        _endedByKey[ended.Key] = ended;
        if (MatchesEnded(ended, _searchText.Trim()))
            EndedRows.Insert(0, ended);

        if (_ended.Count > MaxEnded)
        {
            var oldest = _ended[^1];
            _ended.RemoveAt(_ended.Count - 1);
            _endedByKey.Remove(oldest.Key);
            EndedRows.Remove(oldest);
        }

        if (_details?.Key == ended.Key)
        {
            ended.IsSelected = true;
            _details.MarkExited(ended);
        }
    }

    private void ClearEnded()
    {
        _ended.Clear();
        _endedByKey.Clear();
        EndedRows.Clear();
        if (_details is { IsAlive: false })
            Details = null;
        EndedChanged();
    }

    private bool MatchesEnded(EndedProcess ended, string search)
    {
        if (_thisSessionOnly && ended.SessionId != _sessionId)
            return false;
        if (_shortLivedOnly && !ended.IsShortLived)
            return false;
        return search.Length == 0 || ended.Matches(search);
    }

    private void RenderEnded()
    {
        var search = _searchText.Trim();
        EndedRows.Clear();
        foreach (var ended in _ended)
        {
            if (MatchesEnded(ended, search))
                EndedRows.Add(ended);
        }

        MarkSelection();
        EndedChanged();
    }

    private void EndedChanged()
    {
        OnPropertyChanged(nameof(IsEndedEmpty));
        OnPropertyChanged(nameof(HasEnded));
        OnPropertyChanged(nameof(EndedEmptyTitle));
        OnPropertyChanged(nameof(EndedEmptyHint));
        if (!_showEnded)
            return;

        var total = _ended.Count;
        var noun = total == 1 ? "process" : "processes";
        Summary = IsFilteringEnded
            ? $"{Format.Number(EndedRows.Count)} of {Format.Number(total)} ended {noun} match"
            : _isTracing
                ? $"{Format.Number(total)} {noun} ended since {_recordingSince.ToString("t", CultureInfo.CurrentCulture)}"
                : $"{Format.Number(total)} ended {noun} seen";
    }

    // ========== Running processes ==========

    internal void SortBy(TableColumn column)
    {
        _descending = ReferenceEquals(column, _sortColumn) ? !_descending : column.DescendingFirst;
        _sortColumn = column;
        ShowSort();
        SaveSettings();
        Changed();
    }

    private void ShowSort()
    {
        foreach (var column in _columns)
            column.ShowSort(ReferenceEquals(column, _sortColumn), _descending);
    }

    /// <summary>A column was shown or hidden from the chooser.</summary>
    internal void ColumnsChanged()
    {
        ShowVisibleColumns();
        foreach (var row in Rows)
            row.SetColumns(VisibleColumns);

        // Sorting by a column that's no longer shown would order rows for no visible reason.
        if (!_sortColumn.IsVisible && !ReferenceEquals(_sortColumn, NameColumn))
        {
            _sortColumn = _columns.First(c => c.Column == ProcessColumn.Cpu) is { IsVisible: true } cpu ? cpu : NameColumn;
            _descending = _sortColumn.DescendingFirst;
            ShowSort();
        }

        SaveSettings();
        Changed();
    }

    /// <summary>A column filter was typed, changed or cleared.</summary>
    internal void FiltersChanged()
    {
        _filtered = [.. _columns.Where(c => c.HasFilter)];
        OnPropertyChanged(nameof(ColumnFilterCount));
        OnPropertyChanged(nameof(HasColumnFilters));
        OnPropertyChanged(nameof(ColumnFiltersToolTip));
        OnPropertyChanged(nameof(EmptyHint));
        Changed();
    }

    private void ShowVisibleColumns()
    {
        VisibleColumns.Clear();
        foreach (var column in _columns)
        {
            if (column.IsVisible && !ReferenceEquals(column, NameColumn))
                VisibleColumns.Add(column);
        }

        // Command lines and descriptions cost a lookup per process: only gather them while shown.
        _sampler.WantsCommandLine = VisibleColumns.Any(c => c.Column == ProcessColumn.CommandLine);
        _sampler.WantsDescription = VisibleColumns.Any(c => c.Column == ProcessColumn.Description);
        OnPropertyChanged(nameof(TableMinWidth));
    }

    private void SaveSettings()
    {
        _settings.Columns = [.. _columns.Where(c => c.IsVisible && c.CanHide).Select(c => c.Column)];
        _settings.SortColumn = _sortColumn.Column;
        _settings.SortDescending = _descending;
        _settings.Save();
    }

    internal void Follow(ProcessKey key)
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
        if (_details is { } details)
            details.IsFollowed = _followed.ContainsKey(details.Key);
        Render();
    }

    internal void Toggle(ProcessKey key)
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

    /// <summary>The view, search, filters or sort changed: show the result now, freshly sorted.</summary>
    private void Changed()
    {
        _sortValues.Clear();
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        if (_showEnded)
            RenderEnded();
        else
            Render();
    }

    private void Render()
    {
        if (_latest is not { } snapshot || _showEnded)
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
        if (_dotnetOnly && !process.IsDotnet)
            return false;
        foreach (var column in _filtered)
        {
            if (!column.Filter!.Matches(column, process))
                return false;
        }

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
        using var self = Process.GetCurrentProcess();
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
        var order = _sortColumn.SortsByNumber
            ? SortValue(a).CompareTo(SortValue(b))
            : string.Compare(_sortColumn.SortText(a), _sortColumn.SortText(b), StringComparison.CurrentCultureIgnoreCase);

        if (_descending)
            order = -order;
        if (order == 0)
            order = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : a.Key.Id.CompareTo(b.Key.Id);
    }

    /// <summary>The value a row is sorted by: as of the last re-sort, or live for processes new since then.</summary>
    private double SortValue(ProcessReading process) =>
        _sortValues.TryGetValue(process.Key, out var value) ? value : _sortColumn.Number(process);

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
        if (!_sortColumn.SortsByNumber)
            return;

        foreach (var process in snapshot.Processes)
            _sortValues[process.Key] = _sortColumn.Number(process);
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

        var selected = _details?.Key;
        for (var i = 0; i < entries.Count; i++)
        {
            var (reading, placement) = entries[i];
            if (!existing.TryGetValue(reading.Key, out var row))
                row = new ProcessRow(reading.Key, VisibleColumns, this);

            row.Update(reading, placement, _isTree, _followed.ContainsKey(reading.Key), reading.Key == selected);

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
