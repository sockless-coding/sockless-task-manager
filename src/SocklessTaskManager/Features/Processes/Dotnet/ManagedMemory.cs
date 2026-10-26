using System.Collections.ObjectModel;
using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>
/// The .NET section of the details pane: the runtime's own GC counters, streamed over its
/// diagnostics port once a second (what dotnet-counters shows), and on request a snapshot of the
/// heap by type (what dotnet-gcdump shows). Only a .NET (Core) process with diagnostics on has
/// the port; for anything else the section stays hidden.
/// </summary>
public sealed class ManagedMemory : ObservableObject
{
    private const double BytesPerMegabyte = 1024 * 1024;
    private const double RuntimeMegabyte = 1_000_000; // the runtime's "MB" counters divide by this
    private const uint VerboseLevel = 5;
    private const int ShownTypes = 30;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly int _processId;
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
    private readonly Fact _collections = new("Collections", "—");
    private readonly Fact _pauses = new("Paused", "—");
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

    // Written by the reading task, read by Apply.
    private volatile bool _pendingPresent;
    private volatile string? _pendingStatus;
    private volatile bool _pendingIsError;
    private volatile bool _pendingDenied;
    private volatile string? _pendingVersion;
    private bool _received;

    internal ManagedMemory(int processId)
    {
        _processId = processId;
        Facts = [_gen0, _gen1, _gen2, _large, _pinned, _fragmentation, _committed, _allocations, _collections, _pauses];
        SnapshotCommand = new RelayCommand(TakeSnapshot);
    }

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

    /// <summary>"Runtime .NET 9.0.3" first, once it's known, then the counters.</summary>
    public ObservableCollection<Fact> Facts { get; }

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
        private set => Set(ref _heapMaximum, value);
    }

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

    /// <summary>The last snapshot's largest types.</summary>
    public ObservableCollection<HeapType> HeapTypes { get; } = [];

    /// <summary>Starts streaming counters, if the process has a diagnostics port. UI thread.</summary>
    internal void Start()
    {
        if (_run is not null)
            return;

        _run = new CancellationTokenSource();
        var cancel = _run.Token;
        Task.Run(() => RunAsync(cancel), cancel);
    }

    /// <summary>Stops the session in the process. UI thread.</summary>
    internal void Stop()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;

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
        IsSnapshotting = true;
        SnapshotIsError = false;
        SnapshotStatus = "Starting a full collection…";
        Task.Run(async () =>
        {
            try
            {
                snapshot.Result = await snapshot.Walk.RunAsync(_processId, snapshot.Cancel.Token);
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
            HeapTypes.Clear();
            foreach (var type in result.Types.Take(ShownTypes))
                HeapTypes.Add(type);

            var shown = result.Types.Count > ShownTypes
                ? $" · largest {Format.Number(ShownTypes)} of {Format.Number(result.Types.Count)} types"
                : string.Empty;
            SnapshotSummary = $"{Format.Number(result.Objects)} objects · {Format.Bytes(result.Bytes)} · taken {result.TakenAt.ToString("T", System.Globalization.CultureInfo.CurrentCulture)}{shown}";
            SnapshotStatus = null;
        }
        else if (snapshot.Error is { } error)
        {
            SnapshotStatus = error;
            SnapshotIsError = true;
        }
        else
        {
            SnapshotStatus = snapshot.Walk.HasStarted
                ? $"Walking the heap… {Format.Number(snapshot.Walk.Objects)} objects so far"
                : "Starting a full collection…";
            return;
        }

        _snapshot = null;
        IsSnapshotting = false;
    }

    /// <summary>The counters that arrived since the last tick, into properties. UI thread.</summary>
    internal void Apply()
    {
        IsPresent = _pendingPresent;
        Status = _pendingStatus;
        StatusIsError = _pendingIsError;
        OfferElevation = _pendingDenied && !Elevation.IsElevated;
        if (_pendingVersion is { } version && Facts[0].Label != "Runtime")
            Facts.Insert(0, new Fact("Runtime", $".NET {version}"));
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
        _collections.Value = totals.TryGetValue("gen-0-gc-count", out var gen0)
            ? $"{Format.Number((long)gen0)} gen 0 · {Format.Number((long)totals.GetValueOrDefault("gen-1-gc-count"))} gen 1 · {Format.Number((long)totals.GetValueOrDefault("gen-2-gc-count"))} gen 2"
            : "—";
        _pauses.Value = totals.TryGetValue("total-pause-time-by-gc", out var paused)
            ? $"{Format.Milliseconds(paused)} in total"
            : latest.TryGetValue("time-in-gc", out var share) ? $"{Format.Percent(share)} of the time" : "—";
    }

    private static string BytesOf(Dictionary<string, double> values, string name) =>
        values.TryGetValue(name, out var bytes) ? Format.Bytes(bytes) : "—";

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
                session = await DiagnosticsPort.StartAsync(_processId, "System.Runtime", 0xFFFFFFFF, VerboseLevel,
                    "EventCounterIntervalSec=1", 4, handshake.Token);
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
