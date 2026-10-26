using System.Collections.ObjectModel;
using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>
/// A .NET process's memory as its runtime sees it: the GC counters, streamed over its diagnostics
/// port once a second (what dotnet-counters shows), and on request a snapshot of the heap by type
/// (what dotnet-gcdump shows). The details pane shows a line of it; the .NET runtime page, the rest.
/// Only a .NET (Core) process with diagnostics on has the port; for anything else neither shows.
/// </summary>
public sealed class ManagedMemory : ObservableObject
{
    private const double BytesPerMegabyte = 1024 * 1024;
    private const double RuntimeMegabyte = 1_000_000; // the runtime's "MB" counters divide by this
    private const uint VerboseLevel = 5;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly int _processId;
    private readonly List<(HeapTypePair Pair, HeapType Row)> _heapRows = [];
    private readonly List<HeapTypePair> _shownRows = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<string, double> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _totals = new(StringComparer.Ordinal);
    private readonly Fact _gen0 = new("Gen 0", "—");
    private readonly Fact _gen1 = new("Gen 1", "—");
    private readonly Fact _gen2 = new("Gen 2", "—");
    private readonly Fact _large = new("Large objects", "—");
    private readonly Fact _pinned = new("Pinned objects", "—");
    private readonly Fact _fragmentation = new("Fragmented", "—");
    private readonly Fact _committed = new("Committed", "—");
    private readonly Fact _allocations = new("Allocating", "—");
    private readonly Fact _gen0Collections = new("Gen 0 GCs", "—");
    private readonly Fact _gen1Collections = new("Gen 1 GCs", "—");
    private readonly Fact _gen2Collections = new("Gen 2 GCs", "—");
    private readonly Fact _pauses = new("Paused", "—");
    private readonly Fact _threads = new("Thread pool", "—");
    private readonly Fact _queued = new("Queued work", "—");
    private readonly Fact _completed = new("Work done", "—");
    private readonly Fact _contention = new("Lock waits", "—");
    private readonly Fact _timers = new("Timers", "—");
    private readonly Fact _exceptionRate = new("Exceptions", "—");
    private readonly Fact _assemblies = new("Assemblies", "—");
    private readonly Fact _jitted = new("Methods jitted", "—");
    private readonly Fact _jitTime = new("Time in JIT", "—");
    private readonly Fact _cpu = new("CPU", "—");
    private readonly Fact _workingSet = new("Working set", "—");
    private readonly Fact _requestRate = new("Requests", "—");
    private readonly Fact _currentRequests = new("In progress", "—");
    private readonly Fact _totalRequests = new("Total", "—");
    private readonly Fact _failedRequests = new("Failed", "—");
    private readonly Action<string> _copy;
    private CancellationTokenSource? _run;
    private bool _isPresent;
    private bool _hasCounters;
    private string? _status;
    private bool _statusIsError;
    private bool _offerElevation;
    private string _heapText = "—";
    private double _heapMaximum = 1;
    private SnapshotJob? _snapshot;
    private bool _isSnapshotting;
    private string? _snapshotStatus;
    private bool _snapshotIsError;
    private string? _snapshotSummary;
    private string? _snapshotWarning;
    private ManagedTab _tab;
    private bool _hasWebCounters;
    private bool _isPageOpen;
    private long _heapBytes;
    private HeapSnapshot? _heap;
    private HeapSnapshot? _previousHeap;
    private bool _isComparing;
    private string? _compareSummary;
    private HeapSort _sort = HeapSort.Bytes;
    private string _heapFilter = string.Empty;
    private string? _heapCountText;
    private string? _runtimeText;

    // Written by the reading task, read by Apply.
    private volatile bool _pendingPresent;
    private volatile string? _pendingStatus;
    private volatile bool _pendingIsError;
    private volatile bool _pendingDenied;
    private volatile string? _pendingVersion;
    private bool _received;

    internal ManagedMemory(int processId, string processName, Action open, Action close, Action<string> copy)
    {
        _processId = processId;
        _copy = copy;
        ProcessName = processName;
        Generations = [_gen0, _gen1, _gen2, _large, _pinned];
        Collector = [_committed, _fragmentation, _allocations, _gen0Collections, _gen1Collections, _gen2Collections, _pauses];
        SnapshotCommand = new RelayCommand(TakeSnapshot);
        OpenCommand = new RelayCommand(open);
        CloseCommand = new RelayCommand(close);
        SortByCountCommand = new RelayCommand(() => Sort(HeapSort.Count));
        SortByBytesCommand = new RelayCommand(() => Sort(HeapSort.Bytes));
        SortByRetainedCommand = new RelayCommand(() => Sort(HeapSort.Retained));
        ClearHeapFilterCommand = new RelayCommand(() => HeapFilter = string.Empty);
        Exceptions = new ExceptionFeed(processId);
        Allocations = new AllocationFeed(processId);
        Work = [_threads, _queued, _completed, _contention, _timers, _cpu];
        Code = [_assemblies, _jitted, _jitTime, _exceptionRate, _workingSet];
        Requests = [_requestRate, _currentRequests, _totalRequests, _failedRequests];
        CopyHeapCommand = new RelayCommand(() => _copy(HeapAsText()));
        CopyAllocationsCommand = new RelayCommand(() => _copy(Allocations.AsText()));
        CopyExceptionsCommand = new RelayCommand(() => _copy(Exceptions.AsText()));
    }

    private enum HeapSort
    {
        Count,
        Bytes,
        Retained,
    }

    /// <summary>What the page's lower card shows.</summary>
    private enum ManagedTab
    {
        Heap,
        Allocations,
        Exceptions,
        Runtime,
    }

    public string ProcessName { get; }

    /// <summary>Exceptions as they're thrown, while the page is open.</summary>
    public ExceptionFeed Exceptions { get; }

    /// <summary>What's allocated, by type, while the page is open.</summary>
    public AllocationFeed Allocations { get; }

    // The lower card's switch: one radio button each.
    public bool IsShowingHeap
    {
        get => _tab == ManagedTab.Heap;
        set => ShowTab(ManagedTab.Heap, value);
    }

    public bool IsShowingAllocations
    {
        get => _tab == ManagedTab.Allocations;
        set => ShowTab(ManagedTab.Allocations, value);
    }

    public bool IsShowingExceptions
    {
        get => _tab == ManagedTab.Exceptions;
        set => ShowTab(ManagedTab.Exceptions, value);
    }

    public bool IsShowingRuntime
    {
        get => _tab == ManagedTab.Runtime;
        set => ShowTab(ManagedTab.Runtime, value);
    }

    /// <summary>Threads, the thread pool, locks and timers.</summary>
    public IReadOnlyList<Fact> Work { get; }

    /// <summary>Assemblies, the JIT, exceptions and the working set.</summary>
    public IReadOnlyList<Fact> Code { get; }

    /// <summary>ASP.NET Core's request counters, for a web app.</summary>
    public IReadOnlyList<Fact> Requests { get; }

    /// <summary>The process is an ASP.NET Core app: its request counters arrive.</summary>
    public bool HasWebCounters
    {
        get => _hasWebCounters;
        private set => Set(ref _hasWebCounters, value);
    }

    public ICommand CopyHeapCommand { get; }

    public ICommand CopyAllocationsCommand { get; }

    public ICommand CopyExceptionsCommand { get; }

    /// <summary>
    /// The .NET page is on screen. Exceptions and allocations are followed only then: each costs
    /// the process an event. UI thread.
    /// </summary>
    internal bool IsPageOpen
    {
        get => _isPageOpen;
        set
        {
            _isPageOpen = value;
            if (value && _run is not null)
                StartFeeds();
            else if (!value)
                StopFeeds();
        }
    }

    private void ShowTab(ManagedTab tab, bool show)
    {
        if (!show || _tab == tab)
            return;

        _tab = tab;
        OnPropertyChanged(nameof(IsShowingHeap));
        OnPropertyChanged(nameof(IsShowingAllocations));
        OnPropertyChanged(nameof(IsShowingExceptions));
        OnPropertyChanged(nameof(IsShowingRuntime));
    }

    private void StartFeeds()
    {
        Exceptions.Start();
        Allocations.Start();
    }

    private void StopFeeds()
    {
        Exceptions.Stop();
        Allocations.Stop();
    }

    /// <summary>"PID 4120 · .NET 9.0.3".</summary>
    public string Subtitle => _runtimeText is null
        ? $"PID {_processId.ToString(System.Globalization.CultureInfo.CurrentCulture)}"
        : $"PID {_processId.ToString(System.Globalization.CultureInfo.CurrentCulture)} · {_runtimeText}";

    /// <summary>Opens the .NET runtime page.</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Back to the process list.</summary>
    public ICommand CloseCommand { get; }

    /// <summary>The process has a .NET diagnostics port: show the section.</summary>
    public bool IsPresent
    {
        get => _isPresent;
        private set => Set(ref _isPresent, value);
    }

    /// <summary>Counters have arrived.</summary>
    public bool HasCounters
    {
        get => _hasCounters;
        private set => Set(ref _hasCounters, value);
    }

    /// <summary>Connecting, or why there's nothing to show.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status is not null;

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    /// <summary>Access was refused, and restarting elevated would help.</summary>
    public bool OfferElevation
    {
        get => _offerElevation;
        private set => Set(ref _offerElevation, value);
    }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    /// <summary>Where the heap is, by generation, as of the last collection.</summary>
    public IReadOnlyList<Fact> Generations { get; }

    /// <summary>What the collector is doing: commit, fragmentation, allocations, collections, pauses.</summary>
    public IReadOnlyList<Fact> Collector { get; }

    /// <summary>Bytes in the GC heap, in MB.</summary>
    public History Heap { get; } = new();

    public string HeapText
    {
        get => _heapText;
        private set => Set(ref _heapText, value);
    }

    public double HeapMaximum
    {
        get => _heapMaximum;
        private set
        {
            if (Set(ref _heapMaximum, value))
                OnPropertyChanged(nameof(HeapScale));
        }
    }

    /// <summary>The graph's top, for its frame.</summary>
    public string HeapScale => Format.Bytes(_heapMaximum * BytesPerMegabyte);

    public ICommand SnapshotCommand { get; }

    /// <summary>A heap walk is under way.</summary>
    public bool IsSnapshotting
    {
        get => _isSnapshotting;
        private set => Set(ref _isSnapshotting, value);
    }

    /// <summary>The walk's progress, or why it failed.</summary>
    public string? SnapshotStatus
    {
        get => _snapshotStatus;
        private set
        {
            if (Set(ref _snapshotStatus, value))
                OnPropertyChanged(nameof(HasSnapshotStatus));
        }
    }

    public bool HasSnapshotStatus => _snapshotStatus is not null;

    public bool SnapshotIsError
    {
        get => _snapshotIsError;
        private set => Set(ref _snapshotIsError, value);
    }

    /// <summary>"61,573 objects · 22.9 MB · taken 14:03:12".</summary>
    public string? SnapshotSummary
    {
        get => _snapshotSummary;
        private set
        {
            if (Set(ref _snapshotSummary, value))
                OnPropertyChanged(nameof(HasSnapshot));
        }
    }

    public bool HasSnapshot => _snapshotSummary is not null;

    /// <summary>The last snapshot is incomplete, and why.</summary>
    public string? SnapshotWarning
    {
        get => _snapshotWarning;
        private set
        {
            if (Set(ref _snapshotWarning, value))
                OnPropertyChanged(nameof(HasSnapshotWarning));
        }
    }

    public bool HasSnapshotWarning => _snapshotWarning is not null;

    /// <summary>The last snapshot's types, sorted, matching <see cref="HeapFilter"/>.</summary>
    public ObservableCollection<HeapType> HeapTypes { get; } = [];

    /// <summary>"199 types", or "12 of 199 types" while filtering.</summary>
    public string? HeapCountText
    {
        get => _heapCountText;
        private set => Set(ref _heapCountText, value);
    }

    /// <summary>Part of a type's name, any case.</summary>
    public string HeapFilter
    {
        get => _heapFilter;
        set
        {
            if (!Set(ref _heapFilter, value))
                return;
            OnPropertyChanged(nameof(HasHeapFilter));
            ShowHeap();
        }
    }

    public bool HasHeapFilter => _heapFilter.Length > 0;

    /// <summary>A snapshot, but no type matches the filter.</summary>
    public bool HasNoMatches => _heap is not null && HeapTypes.Count == 0;

    public ICommand ClearHeapFilterCommand { get; }

    public ICommand SortByCountCommand { get; }

    public ICommand SortByBytesCommand { get; }

    public ICommand SortByRetainedCommand { get; }

    public bool IsSortedByCount => _sort == HeapSort.Count;

    public bool IsSortedByBytes => _sort == HeapSort.Bytes;

    public bool IsSortedByRetained => _sort == HeapSort.Retained;

    /// <summary>
    /// The table can show or sort by what types keep alive: the last snapshot worked it out, and
    /// when comparing, the one before it did too.
    /// </summary>
    public bool HasRetained => _heap?.HasRetained == true && (!_isComparing || _previousHeap?.HasRetained == true);

    /// <summary>There's a snapshot before the last to compare it with.</summary>
    public bool CanCompare => _previousHeap is not null;

    /// <summary>
    /// Show how each type changed since the snapshot before, and sort by the change: what grows
    /// between two snapshots is what leaks.
    /// </summary>
    public bool IsComparing
    {
        get => _isComparing;
        set
        {
            if (!Set(ref _isComparing, value && CanCompare))
                return;
            OnPropertyChanged(nameof(HasRetained));
            if (!HasRetained && _sort == HeapSort.Retained)
                Sort(HeapSort.Bytes);
            UpdateCompareSummary();
            ShowHeap();
        }
    }

    /// <summary>"Since 14:03:12: +12,345 objects, +3.4 MB".</summary>
    public string? CompareSummary
    {
        get => _compareSummary;
        private set => Set(ref _compareSummary, value);
    }

    /// <summary>Starts streaming counters, if the process has a diagnostics port. UI thread.</summary>
    internal void Start()
    {
        if (_run is not null)
            return;

        _run = new CancellationTokenSource();
        var cancel = _run.Token;
        Task.Run(() => RunAsync(cancel), cancel);
        if (_isPageOpen)
            StartFeeds();
    }

    /// <summary>Stops the sessions in the process. UI thread.</summary>
    internal void Stop()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        StopFeeds();

        if (_snapshot is { } snapshot)
        {
            snapshot.Cancel.Cancel();
            _snapshot = null;
            IsSnapshotting = false;
            SnapshotStatus = null;
        }
    }

    /// <summary>
    /// Walks the heap. It runs a full collection in the process and pauses it until the walk is
    /// done, so only when asked. UI thread.
    /// </summary>
    private void TakeSnapshot()
    {
        if (_snapshot is not null || _run is null)
            return;

        var snapshot = new SnapshotJob();
        _snapshot = snapshot;
        var heapBytes = _heapBytes;
        IsSnapshotting = true;
        SnapshotIsError = false;
        SnapshotStatus = "Starting a full collection…";
        Task.Run(async () =>
        {
            try
            {
                snapshot.Result = await snapshot.Walk.RunAsync(_processId, heapBytes, snapshot.Cancel.Token);

                // Working out the dominators of a big heap takes hundreds of MB, briefly. Hand
                // it back to Windows now rather than whenever the GC next gets round to it.
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            catch (OperationCanceledException) when (snapshot.Cancel.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                snapshot.Error = "The .NET runtime didn't answer. A suspended or busy process can't.";
            }
            catch (UnauthorizedAccessException)
            {
                snapshot.Error = "Windows refused access to its .NET runtime.";
            }
            catch (Exception e) when (e is IOException or InvalidDataException or DiagnosticsException or TimeoutException)
            {
                snapshot.Error = $"Couldn't walk its heap. {e.Message}";
            }
        });
    }

    /// <summary>The walk's progress, then its result. UI thread, each tick.</summary>
    private void ApplySnapshot()
    {
        if (_snapshot is not { } snapshot)
            return;

        if (snapshot.Result is { } result)
        {
            _previousHeap = _heap;
            _heap = result;
            BuildHeapRows();
            OnPropertyChanged(nameof(CanCompare));
            OnPropertyChanged(nameof(HasRetained));
            if (!HasRetained)
                Sort(HeapSort.Bytes);
            UpdateCompareSummary();
            ShowHeap();

            var retained = result.HasRetained
                ? string.Empty
                : $" · over {Format.Number(HeapGraph.MaxObjects)} objects, too many to work out what each type keeps alive";
            SnapshotSummary = $"{Format.Number(result.Objects)} objects · {Format.Bytes(result.Bytes)} · taken {result.TakenAt.ToString("T", System.Globalization.CultureInfo.CurrentCulture)}{retained}";
            SnapshotWarning = result.IsIncomplete
                ? "Incomplete: the runtime's buffer in the process filled up and part of the walk was dropped. The totals are low, and what types keep alive wasn't worked out."
                : null;
            SnapshotStatus = null;
        }
        else if (snapshot.Error is { } error)
        {
            SnapshotStatus = error;
            SnapshotIsError = true;
        }
        else
        {
            SnapshotStatus = snapshot.Walk.IsAnalyzing
                ? $"Working out what each type keeps alive, across {Format.Number(snapshot.Walk.Objects)} objects…"
                : snapshot.Walk.HasStarted
                    ? $"Walking the heap… {Format.Number(snapshot.Walk.Objects)} objects so far"
                    : "Starting a full collection…";
            return;
        }

        _snapshot = null;
        IsSnapshotting = false;
    }

    /// <summary>
    /// The last snapshot's types, each matched by name with the snapshot before, plus the types
    /// that were there before and are gone now (shown only when comparing).
    /// </summary>
    private void BuildHeapRows()
    {
        _heapRows.Clear();
        if (_heap is not { } heap)
            return;

        var before = new Dictionary<string, HeapTypeTotals>(StringComparer.Ordinal);
        foreach (var type in _previousHeap?.Types ?? [])
            before[type.Name] = type;
        var bothRetained = heap.HasRetained && _previousHeap?.HasRetained == true;

        foreach (var now in heap.Types)
        {
            before.Remove(now.Name, out var then);
            var retainedChange = bothRetained ? (now.Retained ?? 0) - (then?.Retained ?? 0) : (long?)null;
            var pair = new HeapTypePair(now, then, retainedChange);
            _heapRows.Add((pair, new HeapType(pair, heap.Bytes)));
        }

        foreach (var gone in before.Values)
        {
            var pair = new HeapTypePair(null, gone, bothRetained ? -(gone.Retained ?? 0) : null);
            _heapRows.Add((pair, new HeapType(pair, heap.Bytes)));
        }
    }

    /// <summary>The heap table as shown (filtered, sorted, comparing or not), as tab-separated text.</summary>
    private string HeapAsText()
    {
        var text = new System.Text.StringBuilder(_isComparing
            ? "Type\tObjects\tObjects change\tOwn bytes\tOwn bytes change\tRetained bytes change"
            : "Type\tObjects\tOwn bytes\tRetained bytes");
        text.AppendLine();
        foreach (var pair in _shownRows)
        {
            text.Append(TypeName.Full(pair.Name)).Append('\t').Append(pair.Count).Append('\t');
            if (_isComparing)
            {
                text.Append(pair.CountChange).Append('\t').Append(pair.Bytes).Append('\t')
                    .Append(pair.BytesChange).Append('\t').Append(pair.RetainedChange).AppendLine();
            }
            else
            {
                text.Append(pair.Bytes).Append('\t').Append(pair.Retained).AppendLine();
            }
        }

        return text.ToString();
    }

    private void UpdateCompareSummary()
    {
        if (!_isComparing || _heap is not { } now || _previousHeap is not { } then)
        {
            CompareSummary = null;
            return;
        }

        CompareSummary = $"Since the snapshot at {then.TakenAt.ToString("T", System.Globalization.CultureInfo.CurrentCulture)}: "
            + $"{HeapType.Signed(now.Objects - then.Objects, c => Format.Number(c))} objects, "
            + $"{HeapType.Signed(now.Bytes - then.Bytes, b => Format.Bytes(b))}";
    }

    /// <summary>
    /// Largest first by the column chosen: descending only, as "what's biggest" is the question.
    /// While comparing, the columns are the changes, so the fastest-growing types come first.
    /// </summary>
    private void Sort(HeapSort sort)
    {
        if (_sort == sort || (sort == HeapSort.Retained && !HasRetained))
            return;

        _sort = sort;
        OnPropertyChanged(nameof(IsSortedByCount));
        OnPropertyChanged(nameof(IsSortedByBytes));
        OnPropertyChanged(nameof(IsSortedByRetained));
        ShowHeap();
    }

    private void ShowHeap()
    {
        HeapTypes.Clear();
        if (_heap is null)
        {
            HeapCountText = null;
            return;
        }

        var filter = _heapFilter.Trim();
        var shown = _isComparing ? _heapRows : _heapRows.Where(r => r.Pair.Now is not null);
        var total = shown.Count();
        var matching = filter.Length == 0
            ? shown
            : shown.Where(r => r.Row.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        var sorted = (_sort, _isComparing) switch
        {
            (HeapSort.Count, false) => matching.OrderByDescending(r => r.Pair.Count),
            (HeapSort.Retained, false) => matching.OrderByDescending(r => r.Pair.Retained).ThenByDescending(r => r.Pair.Bytes),
            (_, false) => matching.OrderByDescending(r => r.Pair.Bytes),
            (HeapSort.Count, true) => matching.OrderByDescending(r => r.Pair.CountChange),
            (HeapSort.Retained, true) => matching.OrderByDescending(r => r.Pair.RetainedChange).ThenByDescending(r => r.Pair.BytesChange),
            (_, true) => matching.OrderByDescending(r => r.Pair.BytesChange),
        };
        _shownRows.Clear();
        foreach (var (pair, row) in sorted)
        {
            _shownRows.Add(pair);
            HeapTypes.Add(row);
        }

        OnPropertyChanged(nameof(HasNoMatches));
        HeapCountText = filter.Length == 0
            ? $"{Format.Number(total)} types"
            : $"{Format.Number(HeapTypes.Count)} of {Format.Number(total)} types";
    }

    /// <summary>The counters that arrived since the last tick, into properties. UI thread.</summary>
    internal void Apply()
    {
        Exceptions.Apply();
        Allocations.Apply();
        IsPresent = _pendingPresent;
        Status = _pendingStatus;
        StatusIsError = _pendingIsError;
        OfferElevation = _pendingDenied && !Elevation.IsElevated;
        if (_pendingVersion is { } version && _runtimeText is null)
        {
            _runtimeText = $".NET {version}";
            OnPropertyChanged(nameof(Subtitle));
        }

        ApplySnapshot();

        Dictionary<string, double> latest;
        Dictionary<string, double> totals;
        lock (_lock)
        {
            if (!_received)
                return;
            _received = false;
            latest = new Dictionary<string, double>(_latest);
            totals = new Dictionary<string, double>(_totals);
        }

        HasCounters = true;
        if (latest.TryGetValue("gc-heap-size", out var heap))
        {
            Heap.Push(heap * RuntimeMegabyte / BytesPerMegabyte);
            HeapText = Format.Bytes(heap * RuntimeMegabyte);
            _heapBytes = (long)(heap * RuntimeMegabyte);
            HeapMaximum = Format.NiceCeiling(Heap.Max(), 1);
        }

        _gen0.Value = BytesOf(latest, "gen-0-size");
        _gen1.Value = BytesOf(latest, "gen-1-size");
        _gen2.Value = BytesOf(latest, "gen-2-size");
        _large.Value = BytesOf(latest, "loh-size");
        _pinned.Value = BytesOf(latest, "poh-size");
        _fragmentation.Value = latest.TryGetValue("gc-fragmentation", out var fragmentation) ? Format.Percent(fragmentation) : "—";
        _committed.Value = latest.TryGetValue("gc-committed", out var committed) ? Format.Bytes(committed * RuntimeMegabyte) : "—";
        _allocations.Value = latest.TryGetValue("alloc-rate", out var rate) ? Format.BytesPerSecond(rate) : "—";

        _threads.Value = CountOf(latest, "threadpool-thread-count", " threads");
        _queued.Value = CountOf(latest, "threadpool-queue-length", " items");
        _completed.Value = CountOf(latest, "threadpool-completed-items-count", " items/s");
        _contention.Value = CountOf(latest, "monitor-lock-contention-count", "/s");
        _timers.Value = CountOf(latest, "active-timer-count", " active");
        _cpu.Value = latest.TryGetValue("cpu-usage", out var cpu) ? Format.Percent(cpu) : "—";
        _assemblies.Value = CountOf(latest, "assembly-count", " loaded");
        _jitted.Value = CountOf(latest, "methods-jitted-count", string.Empty);
        _jitTime.Value = totals.TryGetValue("time-in-jit", out var jit) ? Format.Milliseconds(jit) : "—";
        _exceptionRate.Value = CountOf(latest, "exception-count", "/s");
        _workingSet.Value = latest.TryGetValue("working-set", out var workingSet) ? Format.Bytes(workingSet * RuntimeMegabyte) : "—";

        if (latest.ContainsKey("total-requests"))
        {
            HasWebCounters = true;
            _requestRate.Value = CountOf(latest, "requests-per-second", "/s");
            _currentRequests.Value = CountOf(latest, "current-requests", string.Empty);
            _totalRequests.Value = CountOf(latest, "total-requests", string.Empty);
            _failedRequests.Value = CountOf(latest, "failed-requests", string.Empty);
        }
        _gen0Collections.Value = CountOf(totals, "gen-0-gc-count");
        _gen1Collections.Value = CountOf(totals, "gen-1-gc-count");
        _gen2Collections.Value = CountOf(totals, "gen-2-gc-count");
        _pauses.Value = totals.TryGetValue("total-pause-time-by-gc", out var paused)
            ? Format.Milliseconds(paused)
            : latest.TryGetValue("time-in-gc", out var share) ? $"{Format.Percent(share)} of the time" : "—";
    }

    private static string BytesOf(Dictionary<string, double> values, string name) =>
        values.TryGetValue(name, out var bytes) ? Format.Bytes(bytes) : "—";

    private static string CountOf(Dictionary<string, double> values, string name, string unit = "") =>
        values.TryGetValue(name, out var count) ? Format.Number((long)Math.Round(count)) + unit : "—";

    private async Task RunAsync(CancellationToken cancel)
    {
        if (!DiagnosticsPort.Exists(_processId))
            return;

        _pendingPresent = true;
        _pendingIsError = false;
        _pendingDenied = false;
        _pendingStatus = "Connecting to the .NET runtime…";
        EventPipeSession? session = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                handshake.CancelAfter(HandshakeTimeout);
                _pendingVersion = await DiagnosticsPort.RuntimeVersionAsync(_processId, handshake.Token);
                session = await DiagnosticsPort.StartAsync(_processId,
                [
                    new EventPipeProvider("System.Runtime", 0xFFFFFFFF, VerboseLevel, "EventCounterIntervalSec=1"),
                    new EventPipeProvider("Microsoft.AspNetCore.Hosting", 0xFFFFFFFF, VerboseLevel, "EventCounterIntervalSec=1"),
                ], 4, handshake.Token);
            }

            _pendingStatus = "Waiting for the first counters…";
            await new NettraceReader(session.Stream).ReadAsync(OnEvent, cancel);
            Fail("The .NET runtime stopped sending counters.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            Fail("The .NET runtime didn't answer. A suspended or busy process can't.");
        }
        catch (UnauthorizedAccessException)
        {
            _pendingDenied = true;
            Fail("Windows refused access to its .NET runtime. It runs as another user or as administrator.");
        }
        catch (DiagnosticsException e) when (e.Code == DiagnosticsPort.UnknownCommand)
        {
            Fail("Its .NET runtime is too old to stream counters. They need .NET 5 or later.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DiagnosticsException or TimeoutException)
        {
            Fail($"Couldn't read its .NET counters. {e.Message}");
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private void Fail(string message)
    {
        _pendingIsError = true;
        _pendingStatus = message;
    }

    /// <summary>One counter's value for the last interval. Reading task.</summary>
    private void OnEvent(EventMetadata metadata, ReadOnlySpan<byte> payload)
    {
        if (metadata.Name != "EventCounters")
            return;

        var fields = metadata.Decode(payload);
        if (fields.GetValueOrDefault("Name") is not string name)
            return;

        lock (_lock)
        {
            if (fields.GetValueOrDefault("Increment") is double increment)
            {
                // A sum over the interval: kept as a total and as a rate.
                var seconds = fields.GetValueOrDefault("IntervalSec") is double interval && interval > 0 ? interval : 1;
                _totals[name] = _totals.GetValueOrDefault(name) + increment;
                _latest[name] = increment / seconds;
            }
            else if (fields.GetValueOrDefault("Mean") is double mean)
            {
                _latest[name] = mean;
            }

            _received = true;
        }

        _pendingStatus = null;
    }

    /// <summary>One heap walk: written by its task, read on the UI thread.</summary>
    private sealed class SnapshotJob
    {
        public HeapWalk Walk { get; } = new();

        public CancellationTokenSource Cancel { get; } = new();

        public volatile HeapSnapshot? Result;

        public volatile string? Error;
    }
}
