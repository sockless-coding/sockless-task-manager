using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

internal enum NetworkTab
{
    Connections,
    History,
    Listening,
    Processes,
    Map,
}

/// <summary>
/// The Network page's state, on the UI thread. It mirrors the monitor's record (merging each
/// snapshot's changes), and renders the view on screen: open connections, everything recorded,
/// listening ports, or processes with their traffic, narrowed by a search and sorted by a column.
/// Rows are kept and updated in place; the order is refreshed every few seconds, or at once when
/// the sort, search or view changes, so rows don't jump under the pointer.
/// </summary>
public sealed class NetworkPage : ObservableObject
{
    /// <summary>Tables show at most this many rows; a search narrows the rest.</summary>
    private const int MaxRows = 1000;

    private const long ResortMilliseconds = 5000;

    private const string PidPrefix = "pid:";

    private readonly Navigator _navigator;
    private readonly NetworkSettings _settings;
    private readonly HostNames _hosts;
    private readonly GeoLocator _geo;

    // Read by the sampling thread.
    private volatile bool _shouldMonitor;
    private volatile Func<string, bool>? _recordFilter;
    private volatile bool _clearRequested;

    // The record, as of the last snapshot.
    private readonly Dictionary<long, ConnectionInfo> _connections = [];
    private readonly Dictionary<ProcessRef, ProcessInfo> _processes = [];
    private IReadOnlyList<ListenerInfo> _listeners = [];
    private IReadOnlyDictionary<ProcessRef, (double Send, double Receive)> _rates = new Dictionary<ProcessRef, (double, double)>();
    private readonly Dictionary<ProcessRef, Traffic> _traffic = [];
    private bool _isTracing;
    private bool _hasSnapshot;
    private double _sendRate;
    private double _receiveRate;
    private DateTime? _recordingSince;

    // Rows.
    private readonly Dictionary<long, ConnectionRow> _connectionRows = [];
    private readonly Dictionary<string, ListenerRow> _listenerRows = [];
    private readonly Dictionary<ProcessRef, ProcessTrafficRow> _processRows = [];
    private readonly Dictionary<string, RelatedLine> _relatedLines = [];

    private bool _isShown;
    private NetworkTab _view = NetworkTab.Connections;
    private string _searchText = string.Empty;
    private bool _watchedOnly;
    private long _rankedAt;
    private string _summary = string.Empty;
    private string _rowsNote = string.Empty;
    private bool _isEmpty;
    private string _notice = string.Empty;
    private NetworkRow? _selected;
    private NetworkDetails? _details;
    private Confirmation? _confirmation;
    private GeoSetup? _geoSetup;
    private CancellationTokenSource? _download;

    internal NetworkPage(Navigator navigator, NetworkSettings settings, HostNames hosts, GeoLocator geo)
    {
        _navigator = navigator;
        _settings = settings;
        _hosts = hosts;
        _geo = geo;
        _hosts.ReverseLookups = settings.ReverseLookups;
        _geo.Changed += OnGeoChanged;

        LiveColumns = new ConnectionColumns(Sort, "Since");
        HistoryColumns = new ConnectionColumns(Sort, "Last seen");
        Listeners = new ListenerColumns(Sort);
        ProcessColumns = new ProcessColumns(Sort);
        _sorts[NetworkTab.Connections] = (LiveColumns.Seen, true);
        _sorts[NetworkTab.History] = (HistoryColumns.Seen, true);
        _sorts[NetworkTab.Listening] = (Listeners.Exposure, true);
        // Traffic is only known elevated (traced); otherwise open connections say most.
        _sorts[NetworkTab.Processes] = (Elevation.IsElevated ? ProcessColumns.ReceiveRate : ProcessColumns.Connections, true);
        MarkSortColumns();

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        DismissGeoHintCommand = new RelayCommand(() =>
        {
            _settings.GeoHintDismissed = true;
            _settings.Save();
            OnPropertyChanged(nameof(ShowGeoHint));
        });
        ExportCommand = new RelayCommand(() => ExportRequested?.Invoke(ExportName()));
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        CreateFilters();
        UpdateMonitoring();
    }

    private readonly Dictionary<NetworkTab, (NetworkColumn Column, bool Descending)> _sorts = [];

    /// <summary>The view asks for a file to save the CSV to; the argument is a suggested name.</summary>
    public event Action<string>? ExportRequested;

    /// <summary>The view puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The view asks for a .mmdb file and passes it to <see cref="ImportGeoAsync"/>.</summary>
    public event Action? ImportRequested;

    // ---------- Monitoring (read by the sampling thread) ----------

    /// <summary>Whether the feature should sample: the page is shown, or it records in the background.</summary>
    internal bool ShouldMonitor => _shouldMonitor;

    /// <summary>Which processes new connections are recorded for; null for all.</summary>
    internal Func<string, bool>? RecordFilter => _recordFilter;

    /// <summary>True once after the user cleared the history: the monitor drops its closed connections.</summary>
    internal bool TakeClearRequest()
    {
        if (!_clearRequested)
            return false;
        _clearRequested = false;
        return true;
    }

    /// <summary>Set by the view; the page renders only while it's shown.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value)
                return;
            _isShown = value;
            UpdateMonitoring();
            if (value)
                Render(resort: true);
        }
    }

    private void UpdateMonitoring()
    {
        var watched = _settings.Watched.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_isShown || _settings.Background == BackgroundRecording.All)
        {
            _recordFilter = null;
            _shouldMonitor = true;
        }
        else if (_settings.Background == BackgroundRecording.Watched && watched.Count > 0)
        {
            _recordFilter = watched.Contains;
            _shouldMonitor = true;
        }
        else
        {
            _shouldMonitor = false;
        }

        if (_shouldMonitor)
            _recordingSince ??= DateTime.UtcNow;
        UpdateSummary();
    }

    // ---------- Views, search and filters ----------

    public bool IsConnectionsView
    {
        get => _view == NetworkTab.Connections;
        set => SetView(value, NetworkTab.Connections);
    }

    public bool IsHistoryView
    {
        get => _view == NetworkTab.History;
        set => SetView(value, NetworkTab.History);
    }

    public bool IsListeningView
    {
        get => _view == NetworkTab.Listening;
        set => SetView(value, NetworkTab.Listening);
    }

    public bool IsProcessesView
    {
        get => _view == NetworkTab.Processes;
        set => SetView(value, NetworkTab.Processes);
    }

    public bool IsMapView
    {
        get => _view == NetworkTab.Map;
        set => SetView(value, NetworkTab.Map);
    }

    /// <summary>A location database is installed, so connections can be put on the map.</summary>
    public bool HasMap => _geo.Location is not null;

    /// <summary>Connections or History: the connection table is on screen.</summary>
    public bool IsConnectionTable => _view is NetworkTab.Connections or NetworkTab.History;

    private void SetView(bool on, NetworkTab view)
    {
        if (!on || _view == view)
            return;

        _view = view;
        OnPropertyChanged(nameof(IsConnectionsView));
        OnPropertyChanged(nameof(IsHistoryView));
        OnPropertyChanged(nameof(IsListeningView));
        OnPropertyChanged(nameof(IsProcessesView));
        OnPropertyChanged(nameof(IsMapView));
        OnPropertyChanged(nameof(IsConnectionTable));
        OnPropertyChanged(nameof(Columns));
        OnPropertyChanged(nameof(SearchHint));
        OnPropertyChanged(nameof(Filters));
        OnPropertyChanged(nameof(HasActiveFilters));
        Select(null);
        ConnectionRows.Clear();
        _connectionRows.Clear();
        Render(resort: true);
    }

    public ConnectionColumns LiveColumns { get; }

    public ConnectionColumns HistoryColumns { get; }

    /// <summary>The connection table's columns for the current view: Connections and History sort separately.</summary>
    public ConnectionColumns Columns => _view == NetworkTab.History ? HistoryColumns : LiveColumns;

    public ListenerColumns Listeners { get; }

    public ProcessColumns ProcessColumns { get; }

    public ObservableCollection<ConnectionRow> ConnectionRows { get; } = [];

    public ObservableCollection<ListenerRow> ListenerRows { get; } = [];

    public ObservableCollection<ProcessTrafficRow> ProcessRows { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearch));
                Render(resort: true);
            }
        }
    }

    public bool HasSearch => _searchText.Length > 0;

    public string SearchHint => _view switch
    {
        NetworkTab.Listening => "Search, or port:1-1024 -svchost",
        NetworkTab.Processes => "Search, or country:SE host:cloudfront",
        NetworkTab.Map => "Search, or process:chrome -org:Google",
        _ => "Search, or port:443 country:SE -svchost",
    };

    public ICommand ClearSearchCommand { get; }

    public string SearchHelp => NetworkSearch.Help;

    /// <summary>Only processes the user watches.</summary>
    public bool WatchedOnly
    {
        get => _watchedOnly;
        set
        {
            if (Set(ref _watchedOnly, value))
                Render(resort: true);
        }
    }

    public bool HasWatched => _settings.Watched.Count > 0;

    public string WatchedTip => _settings.Watched.Count == 0
        ? "Nothing is watched yet. Open a process or connection and choose Watch."
        : $"Only watched processes: {string.Join(", ", _settings.Watched.Order(StringComparer.OrdinalIgnoreCase))}";

    private void Sort(NetworkColumn column)
    {
        var (current, descending) = _sorts[_view];
        _sorts[_view] = ReferenceEquals(current, column) ? (column, !descending) : (column, column.DescendingFirst);
        MarkSortColumns();
        Render(resort: true);
    }

    private void MarkSortColumns()
    {
        Mark(LiveColumns.All, _sorts[NetworkTab.Connections]);
        Mark(HistoryColumns.All, _sorts[NetworkTab.History]);
        Mark(Listeners.All, _sorts[NetworkTab.Listening]);
        Mark(ProcessColumns.All, _sorts[NetworkTab.Processes]);

        static void Mark(NetworkColumn[] columns, (NetworkColumn Column, bool Descending) sort)
        {
            foreach (var c in columns)
            {
                c.IsActive = ReferenceEquals(c, sort.Column);
                c.IsDescending = c.IsActive && sort.Descending;
            }
        }
    }

    // ---------- Header, hints and notices ----------

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>"Showing the first 1,000 of 12,345. Search to narrow it down." Empty when everything fits.</summary>
    public string RowsNote
    {
        get => _rowsNote;
        private set
        {
            if (Set(ref _rowsNote, value))
                OnPropertyChanged(nameof(HasRowsNote));
        }
    }

    public bool HasRowsNote => _rowsNote.Length > 0;

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public string EmptyText => (_view, HasSearch || _watchedOnly || HasActiveFilters) switch
    {
        (_, true) => "Nothing matches. Try a different search, or loosen the filters.",
        (NetworkTab.Connections, _) => _hasSnapshot ? "No open connections." : "Reading the connection tables…",
        (NetworkTab.History, _) => "Nothing recorded yet.",
        (NetworkTab.Listening, _) => "No listening ports.",
        (NetworkTab.Map, _) => "Nothing to put on the map yet: only connections to internet addresses have a place.",
        _ => "No process has used the network yet.",
    };

    /// <summary>What's missing without administrator rights, or why tracing isn't running.</summary>
    public string Hint => (Elevation.IsElevated, _isTracing || !_hasSnapshot) switch
    {
        (false, _) => "Without administrator rights this page shows open connections and ports, but not how much each one sends, connections that last under a second, UDP conversations, or the names apps looked up.",
        (true, false) => "The network trace couldn't start (another tool may be using it), so traffic isn't counted. Open connections and ports are still listed.",
        _ => string.Empty,
    };

    public bool HasHint => Hint.Length > 0;

    public bool OfferElevation => !Elevation.IsElevated;

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public bool ShowGeoHint => !_settings.GeoHintDismissed && !_geo.HasAny;

    public ICommand DismissGeoHintCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand ExportCommand { get; }

    public ICommand ClearHistoryCommand { get; }

    public bool CanClearHistory => _connections.Values.Any(c => !c.IsOpen);

    /// <summary>The outcome of an export, for a few seconds.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    private void ShowNotice(string text)
    {
        Notice = text;
        DispatcherTimer.RunOnce(() =>
        {
            if (Notice == text)
                Notice = string.Empty;
        }, TimeSpan.FromSeconds(6));
    }

    private void UpdateSummary()
    {
        var open = _connections.Values.Count(c => c.IsOpen);
        var parts = new List<string>
        {
            open == 1 ? "1 open connection" : $"{Format.Number(open)} open connections",
            $"{Format.Number(_listeners.Count)} listening",
        };
        if (_isTracing)
            parts.Add($"receiving {Format.BitsPerSecond(_receiveRate * 8)}, sending {Format.BitsPerSecond(_sendRate * 8)}");

        var recording = _settings.Background switch
        {
            BackgroundRecording.All => "Recording everything, also while this page is closed",
            BackgroundRecording.Watched when _settings.Watched.Count > 0 => "Recording watched processes while this page is closed",
            _ => "Recording while this page is open",
        };
        if (_recordingSince is { } since)
            recording += $", since {NetworkText.When(since)}";

        Summary = $"{string.Join(" · ", parts)}. {recording}.";
    }

    // ---------- Snapshots ----------

    internal void Apply(NetworkSnapshot snapshot)
    {
        foreach (var id in snapshot.Removed)
            _connections.Remove(id);
        foreach (var process in snapshot.Processes)
            _processes[process.Key] = process;
        foreach (var connection in snapshot.Changed)
            _connections[connection.Id] = connection;

        _listeners = snapshot.Listeners;
        _rates = snapshot.Rates;
        _sendRate = snapshot.SendRate;
        _receiveRate = snapshot.ReceiveRate;
        var tracingChanged = _isTracing != snapshot.IsTracing || !_hasSnapshot;
        _isTracing = snapshot.IsTracing;
        _hasSnapshot = true;
        if (tracingChanged)
        {
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(HasHint));
        }

        if (_isTracing)
            PushTraffic();

        if (_isShown)
            Render(resort: false);
    }

    /// <summary>Per-process graphs: every process with traffic gets a point; ones idle for a minute and not shown are dropped.</summary>
    private void PushTraffic()
    {
        foreach (var key in _rates.Keys)
        {
            if (!_traffic.ContainsKey(key))
                _traffic[key] = new Traffic();
        }

        List<ProcessRef>? idle = null;
        foreach (var (key, traffic) in _traffic)
        {
            var (send, receive) = _rates.GetValueOrDefault(key);
            traffic.Receive.Push(receive * 8);
            traffic.Send.Push(send * 8);
            traffic.Idle = send + receive > 0 ? 0 : traffic.Idle + 1;
            if (traffic.Idle > History.DefaultLength && _details?.Process != key)
                (idle ??= []).Add(key);
        }

        if (idle is not null)
        {
            foreach (var key in idle)
                _traffic.Remove(key);
        }
    }

    private sealed class Traffic
    {
        public History Receive { get; } = new();

        public History Send { get; } = new();

        public int Idle { get; set; }
    }

    // ---------- Rendering ----------

    private void Render(bool resort)
    {
        var now = Environment.TickCount64;
        if (resort || now - _rankedAt >= ResortMilliseconds)
        {
            resort = true;
            _rankedAt = now;
        }

        var search = NetworkSearch.Parse(_searchText);
        switch (_view)
        {
            case NetworkTab.Connections or NetworkTab.History:
                RenderConnections(search, resort);
                break;
            case NetworkTab.Listening:
                RenderListeners(search, resort);
                break;
            case NetworkTab.Map:
                RenderMap(search, resort);
                break;
            default:
                RenderProcesses(search, resort);
                break;
        }

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CanClearHistory));
        OnPropertyChanged(nameof(ShowGeoHint));
        UpdateSummary();
        RefreshDetails();
    }

    private void RenderConnections(NetworkSearch search, bool resort)
    {
        var history = _view == NetworkTab.History;
        var matches = new List<ConnectionInfo>();
        foreach (var c in _connections.Values)
        {
            if ((history || c.IsOpen) && Matches(c, search))
                matches.Add(c);
        }

        var wanted = Rank(matches, c => c.Id, _connectionRows, ConnectionRows, resort, SortConnections);
        var rows = new List<ConnectionRow>(wanted.Count);
        foreach (var c in wanted)
        {
            if (!_connectionRows.TryGetValue(c.Id, out var row))
                _connectionRows[c.Id] = row = new ConnectionRow(c.Id, Select);
            Fill(row, c);
            rows.Add(row);
        }

        Keep(_connectionRows, rows, r => r.Id);
        Reconcile(ConnectionRows, rows);
        Note(matches.Count, rows.Count);
    }

    private void RenderListeners(NetworkSearch search, bool resort)
    {
        var inbound = new Dictionary<(ProcessRef, ushort), int>();
        foreach (var c in _connections.Values)
        {
            if (c.IsOpen && c.IsInbound && c.Protocol == Protocol.Tcp)
                inbound[(c.Process, c.Key.LocalPort)] = inbound.GetValueOrDefault((c.Process, c.Key.LocalPort)) + 1;
        }

        _inbound = inbound;

        // A process can bind the same address and port more than once (several UDP sockets on 5353): one row each.
        var matches = _listeners.Where(l => Matches(l, search)).DistinctBy(ListenerKey).ToList();
        var wanted = Rank(matches, ListenerKey, _listenerRows, ListenerRows, resort,
            list => SortListeners(list, inbound));
        var rows = new List<ListenerRow>(wanted.Count);
        foreach (var l in wanted)
        {
            var key = ListenerKey(l);
            if (!_listenerRows.TryGetValue(key, out var row))
                _listenerRows[key] = row = NewListenerRow(key, l);
            row.Info = l;
            FillProcess(row, l.Process);
            row.ConnectionCount = l.Protocol == Protocol.Tcp ? inbound.GetValueOrDefault((l.Process, l.Port)) : 0;
            row.ConnectionsText = l.Protocol == Protocol.Tcp ? Format.Number(row.ConnectionCount) : "—";
            rows.Add(row);
        }

        Keep(_listenerRows, rows, r => r.Key);
        Reconcile(ListenerRows, rows);
        Note(matches.Count, rows.Count);
    }

    private ListenerRow NewListenerRow(string key, ListenerInfo l)
    {
        var (exposure, exposed) = Exposure(l.Address);
        var portName = NetworkText.PortName(l.Port, l.Protocol);
        return new ListenerRow(key, Select)
        {
            ProtocolText = l.Protocol == Protocol.Tcp ? "TCP" : "UDP",
            AddressText = l.Address.ToString(),
            PortText = l.Port.ToString(CultureInfo.InvariantCulture),
            SinceText = l.Since is { } since ? NetworkText.When(since) : "—",
            ExposureText = portName is null ? exposure : $"{exposure} · {portName}",
            IsExposed = exposed,
        };
    }

    private static string ListenerKey(ListenerInfo l) =>
        $"{(int)l.Protocol}:{l.Address.High:x}:{l.Address.Low:x}:{l.Port}:{l.Process.Id}:{l.Process.CreateTime}";

    /// <summary>Who can reach a socket bound to this address.</summary>
    private static (string Text, bool Exposed) Exposure(NetAddress address) => address.Scope switch
    {
        AddressScope.Any => ("Every network", true),
        AddressScope.Loopback => ("This computer only", false),
        AddressScope.Private or AddressScope.LinkLocal => ("Local network", true),
        AddressScope.Public => ("Internet", true),
        _ => (AddressScopes.Describe(address.Scope), true),
    };

    private void RenderProcesses(NetworkSearch search, bool resort)
    {
        var all = new Dictionary<ProcessRef, Aggregate>();
        foreach (var c in _connections.Values)
        {
            if (!all.TryGetValue(c.Process, out var a))
                all[c.Process] = a = new Aggregate();
            if (c.IsOpen)
                a.Open++;
            a.Connections.Add(c);
            a.Hosts.Add(c.Key.Remote);
            a.Sent += c.Sent;
            a.Received += c.Received;
        }

        foreach (var l in _listeners)
        {
            if (!all.TryGetValue(l.Process, out var a))
                all[l.Process] = a = new Aggregate();
            a.Listening++;
        }

        var matches = all.Where(e => Matches(e.Key, e.Value, search)).Select(e => e.Key).ToList();
        foreach (var key in matches)
        {
            if (!_processRows.TryGetValue(key, out var row))
                _processRows[key] = row = new ProcessTrafficRow(key, Select);

            var a = all[key];
            var (send, receive) = _rates.GetValueOrDefault(key);
            FillProcess(row, key);
            var info = _processes.GetValueOrDefault(key);
            row.IsRunning = info is { HasEnded: false };
            row.StatusText = row.IsRunning ? "Running" : "Ended";
            row.IsWatched = IsWatched(key);
            (row.Open, row.Listening, row.Hosts, row.Sent, row.Received, row.SendRate, row.ReceiveRate) =
                (a.Open, a.Listening, a.Hosts.Count, a.Sent, a.Received, send, receive);
            row.ConnectionsText = Format.Number(a.Open);
            row.ListeningText = Format.Number(a.Listening);
            row.HostsText = Format.Number(a.Hosts.Count);
            row.ReceiveRateText = _isTracing ? Format.BitsPerSecond(receive * 8) : "—";
            row.SendRateText = _isTracing ? Format.BitsPerSecond(send * 8) : "—";
            row.ReceivedText = _isTracing || a.Received > 0 ? Format.Bytes(a.Received) : "—";
            row.SentText = _isTracing || a.Sent > 0 ? Format.Bytes(a.Sent) : "—";
        }

        var wanted = Rank(matches.Select(k => _processRows[k]).ToList(), r => r.Process, _processRows, ProcessRows, resort, SortProcesses);
        Keep(_processRows, wanted, r => r.Process);
        Reconcile(ProcessRows, wanted);
        Note(matches.Count, wanted.Count);
    }

    private sealed class Aggregate
    {
        public int Open { get; set; }

        public int Listening { get; set; }

        public HashSet<NetAddress> Hosts { get; } = [];

        public List<ConnectionInfo> Connections { get; } = [];

        public long Sent { get; set; }

        public long Received { get; set; }
    }

    /// <summary>
    /// The items to show, in order: sorted afresh when <paramref name="resort"/>, else the rows
    /// already shown keep their places and new ones join at the end.
    /// </summary>
    private static List<T> Rank<T, TKey, TRow>(
        List<T> matches, Func<T, TKey> key, Dictionary<TKey, TRow> rows, ObservableCollection<TRow> shown, bool resort,
        Func<List<T>, IEnumerable<T>> sort)
        where TKey : notnull
        where TRow : class
    {
        if (resort || shown.Count == 0)
            return [.. sort(matches).Take(MaxRows)];

        var position = new Dictionary<TRow, int>(shown.Count);
        for (var i = 0; i < shown.Count; i++)
            position[shown[i]] = i;

        var kept = new List<(int, T)>();
        var added = new List<T>();
        foreach (var m in matches)
        {
            if (rows.TryGetValue(key(m), out var row) && position.TryGetValue(row, out var at))
                kept.Add((at, m));
            else
                added.Add(m);
        }

        kept.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        var result = kept.Select(k => k.Item2).ToList();
        result.AddRange(sort(added).Take(Math.Max(0, MaxRows - result.Count)));
        return result;
    }

    /// <summary>Forgets rows no longer shown, so their objects can go.</summary>
    private static void Keep<TKey, TRow>(Dictionary<TKey, TRow> rows, List<TRow> shown, Func<TRow, TKey> key)
        where TKey : notnull
    {
        if (rows.Count == shown.Count)
            return;
        var keep = shown.Select(key).ToHashSet();
        foreach (var k in rows.Keys.Where(k => !keep.Contains(k)).ToList())
            rows.Remove(k);
    }

    private void Note(int matching, int shown)
    {
        IsEmpty = shown == 0;
        RowsNote = matching > shown
            ? $"Showing {Format.Number(shown)} of {Format.Number(matching)}. Search to narrow it down."
            : string.Empty;
    }

    // ---------- Sorting ----------

    private IEnumerable<ConnectionInfo> SortConnections(List<ConnectionInfo> list)
    {
        var (column, descending) = _sorts[_view];
        IOrderedEnumerable<ConnectionInfo> ordered = column.Key switch
        {
            SortKey.Process => Order(list, c => ProcessName(c.Process), descending, StringComparer.CurrentCultureIgnoreCase),
            SortKey.Protocol => Order(list, c => (int)c.Protocol, descending),
            SortKey.Local => Order(list, c => (c.Key.Local, c.Key.LocalPort), descending),
            SortKey.Remote => Order(list, c => _hosts.Get(c.Key.Remote)?.Name ?? "￿" + c.Key.Remote, descending, StringComparer.CurrentCultureIgnoreCase),
            SortKey.Location => Order(list, c => _geo.Lookup(c.Key.Remote)?.Place ?? "￿" + AddressScopes.Describe(c.Key.Remote.Scope), descending, StringComparer.CurrentCultureIgnoreCase),
            SortKey.State => Order(list, c => TcpStates.Describe(c), descending, StringComparer.CurrentCultureIgnoreCase),
            SortKey.Sent => Order(list, c => (c.SendRate, c.Sent), descending),
            SortKey.Received => Order(list, c => (c.ReceiveRate, c.Received), descending),
            _ => _view == NetworkTab.History
                ? Order(list, c => c.IsOpen ? DateTime.MaxValue : c.LastSeen, descending)
                : Order(list, c => c.Opened ?? c.FirstSeen, descending),
        };
        return ordered.ThenByDescending(c => c.Id);
    }

    private IEnumerable<ListenerInfo> SortListeners(List<ListenerInfo> list, Dictionary<(ProcessRef, ushort), int> inbound)
    {
        var (column, descending) = _sorts[NetworkTab.Listening];
        IOrderedEnumerable<ListenerInfo> ordered = column.Key switch
        {
            SortKey.Process => Order(list, l => ProcessName(l.Process), descending, StringComparer.CurrentCultureIgnoreCase),
            SortKey.Protocol => Order(list, l => (int)l.Protocol, descending),
            SortKey.Local => Order(list, l => l.Address, descending),
            SortKey.Port => Order(list, l => l.Port, descending),
            SortKey.Connections => Order(list, l => inbound.GetValueOrDefault((l.Process, l.Port)), descending),
            SortKey.Seen => Order(list, l => l.Since ?? DateTime.MinValue, descending),
            _ => Order(list, l => ExposureRank(l.Address), descending),
        };
        return ordered.ThenBy(l => l.Port).ThenBy(l => (int)l.Protocol);
    }

    private static int ExposureRank(NetAddress address) => address.Scope switch
    {
        AddressScope.Public => 4,
        AddressScope.Any => 3,
        AddressScope.Private or AddressScope.LinkLocal => 2,
        AddressScope.Loopback => 0,
        _ => 1,
    };

    private IEnumerable<ProcessTrafficRow> SortProcesses(List<ProcessTrafficRow> list)
    {
        var (column, descending) = _sorts[NetworkTab.Processes];
        IOrderedEnumerable<ProcessTrafficRow> ordered = column.Key switch
        {
            SortKey.Process => Order(list, r => r.ProcessName, descending, StringComparer.CurrentCultureIgnoreCase),
            SortKey.Connections => Order(list, r => r.Open, descending),
            SortKey.Port => Order(list, r => r.Listening, descending),
            SortKey.Hosts => Order(list, r => r.Hosts, descending),
            SortKey.SendRate => Order(list, r => (r.SendRate, r.Sent), descending),
            SortKey.Received => Order(list, r => r.Received, descending),
            SortKey.Sent => Order(list, r => r.Sent, descending),
            _ => Order(list, r => (r.ReceiveRate, r.Received), descending),
        };
        return ordered.ThenByDescending(r => r.Open).ThenBy(r => r.ProcessName, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IOrderedEnumerable<T> Order<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer);

    // ---------- Search and filters ----------

    private bool Matches(ConnectionInfo c, NetworkSearch search)
    {
        if (_watchedOnly && !IsWatched(c.Process))
            return false;
        foreach (var group in Filters)
        {
            if (!group.Passes(chip => chip.Connection?.Invoke(c) ?? true))
                return false;
        }

        foreach (var term in search.Terms)
        {
            if (TermMatches(c, term) == term.Negated)
                return false;
        }

        return true;
    }

    private bool TermMatches(ConnectionInfo c, SearchTerm term)
    {
        var v = term.Value;
        switch (term.Field)
        {
            case SearchField.Pid:
                return InRange(c.Process.Id, term);
            case SearchField.Process:
                return MatchesProcess(c.Process, v);
            case SearchField.Port:
                return InRange(c.Key.RemotePort, term) || InRange(c.Key.LocalPort, term);
            case SearchField.Ip:
                return c.Key.Remote.ToString().Contains(v, StringComparison.OrdinalIgnoreCase)
                       || c.Key.Local.ToString().Contains(v, StringComparison.OrdinalIgnoreCase);
            case SearchField.Host:
                return _hosts.Get(c.Key.Remote)?.Name.Contains(v, StringComparison.OrdinalIgnoreCase) == true;
            case SearchField.Country:
                return CountryMatches(_geo.Lookup(c.Key.Remote), v);
            case SearchField.Org:
                return OrgMatches(_geo.Lookup(c.Key.Remote), v);
            case SearchField.Protocol:
                return NetworkText.Protocol(c.Key).Equals(v, StringComparison.OrdinalIgnoreCase);
            case SearchField.State:
                return TcpStates.Describe(c).StartsWith(v, StringComparison.OrdinalIgnoreCase)
                       || TcpStates.Technical(c.State).Contains(v, StringComparison.OrdinalIgnoreCase)
                       || c.ShortLived && "short-lived".StartsWith(v, StringComparison.OrdinalIgnoreCase);
            case SearchField.Direction:
                return (c.IsInbound ? "incoming" : "outgoing").StartsWith(v, StringComparison.OrdinalIgnoreCase);
        }

        // Free text: anything shown about it.
        if (MatchesProcess(c.Process, v))
            return true;
        if (c.Key.Remote.ToString().Contains(v, StringComparison.OrdinalIgnoreCase)
            || c.Key.Local.ToString().Contains(v, StringComparison.OrdinalIgnoreCase))
            return true;
        if (ushort.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && (c.Key.RemotePort == port || c.Key.LocalPort == port))
            return true;
        if (_hosts.Get(c.Key.Remote)?.Name.Contains(v, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        if (_geo.Lookup(c.Key.Remote) is { } geo
            && (geo.Place?.Contains(v, StringComparison.OrdinalIgnoreCase) == true
                || geo.Network?.Contains(v, StringComparison.OrdinalIgnoreCase) == true
                || string.Equals(geo.CountryCode, v, StringComparison.OrdinalIgnoreCase)))
            return true;
        return NetworkText.Protocol(c.Key).Equals(v, StringComparison.OrdinalIgnoreCase)
               || TcpStates.Describe(c).Equals(v, StringComparison.OrdinalIgnoreCase);
    }

    private bool Matches(ListenerInfo l, NetworkSearch search)
    {
        if (_watchedOnly && !IsWatched(l.Process))
            return false;
        foreach (var group in Filters)
        {
            if (!group.Passes(chip => chip.Listener?.Invoke(l) ?? true))
                return false;
        }

        foreach (var term in search.Terms)
        {
            // Terms about the other end (host, country, state...) don't apply to a port nobody is on yet.
            bool? hit = term.Field switch
            {
                SearchField.Pid => InRange(l.Process.Id, term),
                SearchField.Process => MatchesProcess(l.Process, term.Value),
                SearchField.Port => InRange(l.Port, term),
                SearchField.Ip => l.Address.ToString().Contains(term.Value, StringComparison.OrdinalIgnoreCase),
                SearchField.Protocol => (l.Protocol == Protocol.Tcp ? "TCP" : "UDP").Equals(term.Value, StringComparison.OrdinalIgnoreCase),
                SearchField.Any => MatchesProcess(l.Process, term.Value)
                                   || l.Address.ToString().Contains(term.Value, StringComparison.OrdinalIgnoreCase)
                                   || l.Port.ToString(CultureInfo.InvariantCulture) == term.Value
                                   || NetworkText.PortName(l.Port, l.Protocol)?.Contains(term.Value, StringComparison.OrdinalIgnoreCase) == true
                                   || (l.Protocol == Protocol.Tcp ? "TCP" : "UDP").Equals(term.Value, StringComparison.OrdinalIgnoreCase),
                _ => null,
            };
            if (hit is { } h && h == term.Negated)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Process terms test the process; terms about connections (port, host, country...) hold if any of
    /// its connections matches, so <c>country:RU</c> finds the processes that talked to Russia.
    /// </summary>
    private bool Matches(ProcessRef process, Aggregate aggregate, NetworkSearch search)
    {
        if (_watchedOnly && !IsWatched(process))
            return false;

        var running = _processes.GetValueOrDefault(process) is { HasEnded: false };
        var (send, receive) = _rates.GetValueOrDefault(process);
        var facts = new ProcessFilterFacts(running, aggregate.Sent + aggregate.Received > 0 || send + receive > 0, aggregate.Listening, aggregate.Open);
        foreach (var group in Filters)
        {
            if (!group.Passes(chip => chip.Process?.Invoke(facts) ?? true))
                return false;
        }

        foreach (var term in search.Terms)
        {
            var hit = term.Field switch
            {
                SearchField.Pid => InRange(process.Id, term),
                SearchField.Process => MatchesProcess(process, term.Value),
                SearchField.Any => MatchesProcess(process, term.Value) || aggregate.Connections.Any(c => TermMatches(c, term)),
                _ => aggregate.Connections.Any(c => TermMatches(c, term)),
            };
            if (hit == term.Negated)
                return false;
        }

        return true;
    }

    private static bool InRange(long value, SearchTerm term) => term.Low is { } low && value >= low && value <= term.High;

    private static bool CountryMatches(GeoInfo? geo, string value) =>
        geo is not null
        && (string.Equals(geo.CountryCode, value, StringComparison.OrdinalIgnoreCase)
            || value.Length > 2 && geo.Country?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);

    private static bool OrgMatches(GeoInfo? geo, string value)
    {
        if (geo is null)
            return false;
        var number = value.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (geo.Asn is { } asn && long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var wanted))
            return asn == wanted;
        return geo.Organization?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool MatchesProcess(ProcessRef process, string text)
    {
        if (process.Id.ToString(CultureInfo.InvariantCulture) == text)
            return true;
        if (!_processes.TryGetValue(process, out var info))
            return false;
        return info.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
               || info.Path?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
               || info.Services.Any(s => s.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Filter chips ----------

    private FilterGroup[] _connectionFilters = [];
    private FilterGroup[] _historyFilters = [];
    private FilterGroup[] _listenerFilters = [];
    private FilterGroup[] _processFilters = [];
    private FilterGroup[] _mapFilters = [];

    /// <summary>The chip groups for the view on screen.</summary>
    public IReadOnlyList<FilterGroup> Filters => _view switch
    {
        NetworkTab.Connections => _connectionFilters,
        NetworkTab.History => _historyFilters,
        NetworkTab.Listening => _listenerFilters,
        NetworkTab.Map => _mapFilters,
        _ => _processFilters,
    };

    public bool HasActiveFilters => Filters.Any(g => g.IsActive);

    public ICommand ClearFiltersCommand { get; }

    private IEnumerable<FilterGroup> AllFilters =>
        _connectionFilters.Concat(_historyFilters).Concat(_listenerFilters).Concat(_processFilters).Concat(_mapFilters).Distinct();

    private void CreateFilters()
    {
        static bool In(NetAddress a, params AddressScope[] scopes) => scopes.Contains(a.Scope);
        FilterChip Chip(string key, string label, string tip,
            Func<ConnectionInfo, bool>? connection = null, Func<ListenerInfo, bool>? listener = null, Func<ProcessFilterFacts, bool>? process = null) =>
            new(key, label, tip, OnFiltersChanged) { Connection = connection, Listener = listener, Process = process };

        var protocol = new FilterGroup("Protocol",
        [
            Chip("c.tcp", "TCP", "TCP connections", c => c.Protocol == Protocol.Tcp),
            Chip("c.udp", "UDP", "UDP conversations (seen as administrator)", c => c.Protocol == Protocol.Udp),
        ]);
        var direction = new FilterGroup("Direction",
        [
            Chip("c.out", "Outgoing", "This computer connected out", c => !c.IsInbound),
            Chip("c.in", "Incoming", "Something connected to a port here", c => c.IsInbound),
        ]);
        var remote = new FilterGroup("Other end",
        [
            Chip("c.internet", "Internet", "Public addresses", c => c.Key.Remote.IsRoutable),
            Chip("c.lan", "Local network", "Private, link-local, multicast and broadcast addresses",
                c => In(c.Key.Remote, AddressScope.Private, AddressScope.LinkLocal, AddressScope.Multicast, AddressScope.Broadcast)),
            Chip("c.self", "This computer", "Loopback: programs on this computer talking to each other",
                c => In(c.Key.Remote, AddressScope.Loopback, AddressScope.Any)),
        ]);
        var traffic = new FilterGroup("Traffic",
        [
            Chip("c.traffic", "With traffic", "Sent or received something (counted as administrator)", c => c.Sent + c.Received > 0),
            Chip("c.now", "Busy now", "Sending or receiving at this moment (as administrator)", c => c.SendRate + c.ReceiveRate > 0),
        ]);
        var status = new FilterGroup("Status",
        [
            Chip("h.open", "Open", "Still open", c => c.IsOpen),
            Chip("h.closed", "Closed", "Closed while monitored", c => !c.IsOpen),
            Chip("h.short", "Short-lived", "Opened and closed between two samples (seen as administrator)", c => c.ShortLived),
        ]);

        var listenerProtocol = new FilterGroup("Protocol",
        [
            Chip("l.tcp", "TCP", "TCP ports waiting for connections", listener: l => l.Protocol == Protocol.Tcp),
            Chip("l.udp", "UDP", "Bound UDP ports", listener: l => l.Protocol == Protocol.Udp),
        ]);
        var reach = new FilterGroup("Reachable from",
        [
            Chip("l.any", "Every network", "Bound to every address", listener: l => In(l.Address, AddressScope.Any)),
            Chip("l.network", "One network", "Bound to one network address, local or public",
                listener: l => In(l.Address, AddressScope.Private, AddressScope.LinkLocal, AddressScope.Public)),
            Chip("l.self", "This computer only", "Bound to loopback", listener: l => In(l.Address, AddressScope.Loopback)),
        ]);
        var listenerProcess = new FilterGroup("Process",
        [
            Chip("l.connected", "With connections", "Something is connected to it now", listener: l => InboundCount(l) > 0),
        ]);

        var running = new FilterGroup("Status",
        [
            Chip("p.running", "Running", "Still running", process: p => p.Running),
            Chip("p.ended", "Ended", "Ended while monitored", process: p => !p.Running),
        ]);
        var activity = new FilterGroup("Activity",
        [
            Chip("p.open", "Connected", "Has open connections", process: p => p.Open > 0),
            Chip("p.listening", "Listening", "Waits on a port", process: p => p.Listening > 0),
            Chip("p.traffic", "With traffic", "Sent or received something (counted as administrator)", process: p => p.HasTraffic),
        ]);

        _connectionFilters = [protocol, direction, remote, traffic];
        _historyFilters = [status, protocol, direction, remote, traffic];
        _listenerFilters = [listenerProtocol, reach, listenerProcess];
        _processFilters = [running, activity];
        _mapFilters = [status, protocol, direction, traffic];

        foreach (var group in AllFilters)
        {
            foreach (var chip in group.Chips)
                chip.Restore(_settings.Filters.Contains(chip.Key));
        }
    }

    /// <summary>Open TCP connections to a listening port, as of the last render.</summary>
    private int InboundCount(ListenerInfo l) =>
        l.Protocol == Protocol.Tcp ? _inbound.GetValueOrDefault((l.Process, l.Port)) : 0;

    private Dictionary<(ProcessRef, ushort), int> _inbound = [];

    private void OnFiltersChanged()
    {
        _settings.Filters.Clear();
        foreach (var group in AllFilters)
        {
            foreach (var chip in group.Chips.Where(c => c.IsChecked))
                _settings.Filters.Add(chip.Key);
        }

        _settings.Save();
        OnPropertyChanged(nameof(HasActiveFilters));
        Render(resort: true);
    }

    private void ClearFilters()
    {
        foreach (var group in Filters)
        {
            foreach (var chip in group.Chips)
                chip.Restore(false);
        }

        OnFiltersChanged();
    }

    // ---------- Filling rows ----------

    private string ProcessName(ProcessRef process) =>
        _processes.TryGetValue(process, out var info) ? info.Name : $"Process {process.Id.ToString(CultureInfo.CurrentCulture)}";

    private bool IsWatched(ProcessRef process) =>
        _settings.Watched.Count > 0 && _processes.TryGetValue(process, out var info) && _settings.Watched.Contains(info.Name);

    private void FillProcess(NetworkRow row, ProcessRef process)
    {
        row.ProcessName = ProcessName(process);
        var info = _processes.GetValueOrDefault(process);
        var caption = $"PID {process.Id.ToString(CultureInfo.CurrentCulture)}";
        if (info?.ServiceText is { } services)
            caption += $" · {services}";
        if (info is { HasEnded: true })
            caption += " · ended";
        row.ProcessCaption = caption;
    }

    private void Fill(ConnectionRow row, ConnectionInfo c)
    {
        if (!ReferenceEquals(row.Info, c))
        {
            row.Info = c;
            row.ProtocolText = NetworkText.Protocol(c.Key);
            row.DirectionText = c.IsInbound ? "Incoming" : "Outgoing";
            row.LocalText = c.Key.Local.WithPort(c.Key.LocalPort);
            row.StateText = c.ShortLived ? "Short-lived" : TcpStates.Describe(c);
            row.IsActive = c.IsOpen && (c.Protocol == Protocol.Udp || c.State is TcpState.Established or TcpState.None);
            row.IsShortLived = c.ShortLived;
            row.SentText = _isTracing || c.Sent > 0 ? Format.Bytes(c.Sent) : "—";
            row.ReceivedText = _isTracing || c.Received > 0 ? Format.Bytes(c.Received) : "—";
            row.SendRateText = NetworkText.Rate(c.SendRate);
            row.ReceiveRateText = NetworkText.Rate(c.ReceiveRate);
            row.SeenText = c.IsOpen ? NetworkText.When(c.Opened ?? c.FirstSeen) : NetworkText.When(c.LastSeen);
        }

        FillProcess(row, c.Process);

        var remote = c.Key.Remote;
        var address = remote.WithPort(c.Key.RemotePort);
        var host = _hosts.Get(remote);
        if (host is null)
            _hosts.Request(remote);
        row.RemoteText = host?.Name ?? address;
        row.RemoteCaption = host is null ? string.Empty : address;

        var geo = _geo.Lookup(remote);
        row.LocationText = geo?.Place ?? (remote.IsRoutable ? "—" : AddressScopes.Describe(remote.Scope));
        row.LocationCaption = geo?.Network ?? string.Empty;
    }

    // ---------- Selection and details ----------

    private void Select(NetworkRow? row)
    {
        if (row is not null && ReferenceEquals(row, _selected))
            row = null;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = row;
        _relatedLines.Clear();
        if (row is null)
        {
            Details = null;
            return;
        }

        row.IsSelected = true;
        var (kind, process) = row switch
        {
            ConnectionRow c => (DetailsKind.Connection, c.Info!.Process),
            ListenerRow l => (DetailsKind.Listener, l.Info!.Process),
            ProcessTrafficRow p => (DetailsKind.Process, p.Process),
            MapPlace => (DetailsKind.Place, default(ProcessRef)),
            _ => throw new UnreachableException(),
        };

        var details = new NetworkDetails(row.Key, kind, process, new NetworkDetails.Actions(
            () => Select(null), OpenProcess, ToggleWatch, ShowConnections, Copy, AskCloseConnection));
        if (kind == DetailsKind.Process)
        {
            if (!_traffic.TryGetValue(process, out var traffic) && _isTracing)
                _traffic[process] = traffic = new Traffic();
            details.Receive = traffic?.Receive;
            details.Send = traffic?.Send;
        }

        Details = details;
        RefreshDetails();
    }

    public NetworkDetails? Details
    {
        get => _details;
        private set
        {
            if (Set(ref _details, value))
                OnPropertyChanged(nameof(HasDetails));
        }
    }

    public bool HasDetails => _details is not null;

    private void RefreshDetails()
    {
        if (_details is not { } details || _selected is not { } row)
            return;

        switch (row)
        {
            case ConnectionRow c when c.Info is { } info:
                FillConnectionDetails(details, _connections.GetValueOrDefault(info.Id) ?? info);
                break;
            case ListenerRow l when l.Info is { } info:
                FillListenerDetails(details, info, l);
                break;
            case ProcessTrafficRow p:
                FillProcessDetails(details, p);
                break;
            case MapPlace m:
                FillPlaceDetails(details, m);
                break;
        }

        var process = _processes.GetValueOrDefault(details.Process);
        details.IsWatched = IsWatched(details.Process);
        details.CanOpenProcess = process is { HasEnded: false };
    }

    private List<NetworkFact> ProcessFacts(ProcessRef key)
    {
        var info = _processes.GetValueOrDefault(key);
        var facts = new List<NetworkFact>
        {
            new("Process", $"{ProcessName(key)} (PID {key.Id.ToString(CultureInfo.CurrentCulture)}){(info is { HasEnded: true } ? ", ended" : string.Empty)}"),
            new("Path", info?.Path ?? (info is { HasEnded: true } ? "Not known: it ended before it could be read"
                : Elevation.IsElevated ? "Windows doesn't say" : "Needs administrator rights")),
        };
        if (info is { Services.Count: > 0 })
            facts.Add(new("Services", string.Join(Environment.NewLine, info.Services)));
        return facts;
    }

    private void FillConnectionDetails(NetworkDetails details, ConnectionInfo c)
    {
        var remote = c.Key.Remote;
        var host = _hosts.Get(remote);
        var geo = _geo.Lookup(remote);

        details.Title = host?.Name ?? remote.WithPort(c.Key.RemotePort);
        details.Subtitle = $"{ProcessName(c.Process)} · {(c.IsInbound ? "incoming" : "outgoing")} {NetworkText.Protocol(c.Key)}";
        details.StatusText = c.ShortLived ? "Short-lived" : TcpStates.Describe(c);
        details.IsSuccess = c.IsOpen && (c.Protocol == Protocol.Udp || c.State is TcpState.Established or TcpState.None);
        details.IsWarning = c.ShortLived;

        var facts = ProcessFacts(c.Process);
        facts.Add(new("Protocol", NetworkText.ProtocolLong(c.Key)));
        facts.Add(new("Direction", c.IsInbound ? "Incoming: something connected to a port this process listens on" : "Outgoing: this process connected out"));
        facts.Add(new("Local", c.Key.Local.WithPort(c.Key.LocalPort)));
        facts.Add(new("Remote", RemoteWithService(c)));
        facts.Add(new("Host name", host switch
        {
            null => _hosts.ReverseLookups ? "Not known" : "Not known (reverse lookups are off)",
            { Source: HostSource.Lookup } => $"{host.Name} (an app looked it up)",
            _ => $"{host.Name} (reverse lookup)",
        }));
        facts.Add(new("Address", remote.IsRoutable ? "On the internet" : AddressScopes.Describe(remote.Scope)));
        if (geo?.Place is { } place)
            facts.Add(new("Location", geo.Region is { } region && geo.City is not null ? $"{geo.City}, {region}, {geo.Country ?? geo.CountryCode}" : place));
        if (geo?.Network is { } network)
            facts.Add(new("Network", network));
        if (remote.IsRoutable && !_geo.HasAny)
            facts.Add(new("Location", "Add a location database in Settings"));

        facts.Add(new("State", c.Protocol == Protocol.Tcp ? $"{TcpStates.Describe(c)} ({TcpStates.Technical(c.State)})" : TcpStates.Describe(c)));
        if (c.ShortLived)
            facts.Add(new("Short-lived", "Opened and closed between two samples; only the trace saw it"));
        if (c.Opened is { } opened)
            facts.Add(new("Opened", NetworkText.WhenLong(opened)));
        facts.Add(new("First seen", NetworkText.WhenLong(c.FirstSeen)));
        if (!c.IsOpen)
            facts.Add(new("Last seen", NetworkText.WhenLong(c.LastSeen)));
        if (_isTracing || c.Sent + c.Received > 0)
        {
            facts.Add(new("Sent", TrafficText(c.Sent, c.SendRate)));
            facts.Add(new("Received", TrafficText(c.Received, c.ReceiveRate)));
            if (c.Retransmits > 0)
                facts.Add(new("Retransmits", Format.Number(c.Retransmits)));
        }

        details.Facts = facts;
        details.Attribution = geo is not null && IsDbIp() ? GeoLocator.Attribution : string.Empty;

        var canClose = c is { IsOpen: true, Protocol: Protocol.Tcp } && c.Key.Local.IsV4 && c.State != TcpState.TimeWait;
        details.CanCloseConnection = canClose;
        details.CloseHint = c is { IsOpen: true, Protocol: Protocol.Tcp } && !c.Key.Local.IsV4
            ? "Windows can only reset IPv4 connections."
            : string.Empty;

        // Everything else that talked to the same address, from any process. Not for this computer's
        // own addresses, where that's every local connection.
        var related = remote.Scope is not (AddressScope.Loopback or AddressScope.Any);
        details.RelatedTitle = related ? "Other connections to this address" : string.Empty;
        var lines = new List<RelatedLine>();
        foreach (var other in related ? _connections.Values : Enumerable.Empty<ConnectionInfo>())
        {
            if (other.Id == c.Id || other.Key.Remote != remote)
                continue;
            var line = Line($"c:{other.Id}", ProcessName(other.Process), remote.ToString());
            line.Secondary = $"{NetworkText.Protocol(other.Key)} {other.Key.LocalPort} → {other.Key.RemotePort} · {TcpStates.Describe(other)}";
            line.Value = other.Sent + other.Received > 0 ? Format.Bytes(other.Sent + other.Received) : NetworkText.When(other.IsOpen ? other.FirstSeen : other.LastSeen);
            line.Weight = other.IsOpen ? double.MaxValue : other.LastSeen.Ticks;
            lines.Add(line);
        }

        SetRelated(details, lines);
    }

    private string RemoteWithService(ConnectionInfo c)
    {
        var text = c.Key.Remote.WithPort(c.Key.RemotePort);
        var service = NetworkText.PortName(c.IsInbound ? c.Key.LocalPort : c.Key.RemotePort, c.Protocol);
        return service is null ? text : $"{text} ({service})";
    }

    private void FillListenerDetails(NetworkDetails details, ListenerInfo l, ListenerRow row)
    {
        var protocol = l.Protocol == Protocol.Tcp ? "TCP" : "UDP";
        details.Title = $"{protocol} port {l.Port.ToString(CultureInfo.InvariantCulture)}";
        details.Subtitle = ProcessName(l.Process);
        var (exposure, exposed) = Exposure(l.Address);
        details.StatusText = exposure;
        details.IsSuccess = false;
        details.IsWarning = exposed;

        var facts = ProcessFacts(l.Process);
        facts.Add(new("Protocol", $"{protocol} over {(l.Address.IsV4 ? "IPv4" : "IPv6")}"));
        facts.Add(new("Address", l.Address.WithPort(l.Port)));
        if (NetworkText.PortName(l.Port, l.Protocol) is { } name)
            facts.Add(new("Usually", name));
        facts.Add(new("Reachable from", l.Address.Scope switch
        {
            AddressScope.Any => "Every network this computer is on, unless the firewall blocks it",
            AddressScope.Loopback => "Only programs on this computer",
            AddressScope.Private or AddressScope.LinkLocal => "The local network on that address, unless the firewall blocks it",
            AddressScope.Public => "The internet on that address, unless the firewall blocks it",
            _ => AddressScopes.Describe(l.Address.Scope),
        }));
        if (l.Since is { } since)
            facts.Add(new("Since", NetworkText.WhenLong(since)));
        if (l.Protocol == Protocol.Tcp)
            facts.Add(new("Connections", Format.Number(row.ConnectionCount)));
        details.Facts = facts;
        details.Attribution = string.Empty;
        details.CanCloseConnection = false;
        details.CloseHint = string.Empty;

        details.RelatedTitle = l.Protocol == Protocol.Tcp ? "Connected to this port" : string.Empty;
        var lines = new List<RelatedLine>();
        if (l.Protocol == Protocol.Tcp)
        {
            foreach (var c in _connections.Values)
            {
                if (!c.IsInbound || c.Process != l.Process || c.Key.LocalPort != l.Port || c.Protocol != Protocol.Tcp)
                    continue;
                var line = Line($"c:{c.Id}", _hosts.Get(c.Key.Remote)?.Name ?? c.Key.Remote.ToString(), c.Key.Remote.ToString());
                line.Secondary = $"{c.Key.Remote.WithPort(c.Key.RemotePort)} · {TcpStates.Describe(c)}";
                line.Value = NetworkText.When(c.IsOpen ? c.FirstSeen : c.LastSeen);
                line.Weight = c.IsOpen ? double.MaxValue : c.LastSeen.Ticks;
                lines.Add(line);
            }
        }

        SetRelated(details, lines);
    }

    private void FillProcessDetails(NetworkDetails details, ProcessTrafficRow row)
    {
        var info = _processes.GetValueOrDefault(row.Process);
        details.Title = row.ProcessName;
        details.Subtitle = info?.Path ?? string.Empty;
        details.StatusText = row.StatusText;
        details.IsSuccess = row.IsRunning;
        details.IsWarning = false;

        var facts = ProcessFacts(row.Process);
        facts.Add(new("Open", Format.Number(row.Open)));
        var ports = _listeners.Where(l => l.Process == row.Process)
            .Select(l => $"{(l.Protocol == Protocol.Tcp ? "TCP" : "UDP")} {l.Port.ToString(CultureInfo.InvariantCulture)}")
            .Distinct().ToList();
        if (ports.Count > 0)
            facts.Add(new("Listening on", ports.Count > 12 ? $"{string.Join(", ", ports.Take(12))} and {ports.Count - 12} more" : string.Join(", ", ports)));
        facts.Add(new("Remote hosts", Format.Number(row.Hosts)));
        if (_isTracing || row.Sent + row.Received > 0)
        {
            facts.Add(new("Received", TrafficText(row.Received, row.ReceiveRate)));
            facts.Add(new("Sent", TrafficText(row.Sent, row.SendRate)));
        }

        var first = _connections.Values.Where(c => c.Process == row.Process).Select(c => c.FirstSeen).DefaultIfEmpty().Min();
        if (first != default)
            facts.Add(new("First seen", NetworkText.WhenLong(first)));
        details.Facts = facts;
        details.CanCloseConnection = false;
        details.CloseHint = string.Empty;

        if (details.Receive is not null)
        {
            var peak = Math.Max(details.Receive.Max(), details.Send!.Max());
            details.Maximum = Format.NiceCeiling(peak / 1000, 10) * 1000;
            details.Scale = Format.BitsPerSecond(details.Maximum);
        }

        // Remote hosts, busiest first.
        details.RelatedTitle = "Remote hosts";
        var byHost = new Dictionary<NetAddress, (int Count, long Bytes, bool Open, DateTime Last)>();
        foreach (var c in _connections.Values)
        {
            if (c.Process != row.Process || c.Key.Remote.Scope == AddressScope.Any)
                continue;
            var e = byHost.GetValueOrDefault(c.Key.Remote);
            byHost[c.Key.Remote] = (e.Count + 1, e.Bytes + c.Sent + c.Received, e.Open || c.IsOpen, c.LastSeen > e.Last ? c.LastSeen : e.Last);
        }

        var lines = new List<RelatedLine>();
        var anyGeo = false;
        foreach (var (address, e) in byHost)
        {
            var geo = _geo.Lookup(address);
            anyGeo |= geo is not null;
            var line = Line($"h:{address}", _hosts.Get(address)?.Name ?? address.ToString(), $"ip:{address}");
            line.Secondary = geo?.Place is { } place
                ? geo.Network is { } owner ? $"{place} · {owner}" : place
                : address.IsRoutable ? address.ToString() : $"{address} · {AddressScopes.Describe(address.Scope)}";
            line.Value = e.Count == 1 ? "1 connection" : $"{Format.Number(e.Count)} connections";
            if (e.Bytes > 0)
                line.Value += $" · {Format.Bytes(e.Bytes)}";
            line.Weight = e.Bytes > 0 ? e.Bytes : e.Count;
            lines.Add(line);
        }

        details.Attribution = anyGeo && IsDbIp() ? GeoLocator.Attribution : string.Empty;
        SetRelated(details, lines);
    }

    private bool IsDbIp() =>
        _geo.Location?.DatabaseType.StartsWith("DBIP", StringComparison.OrdinalIgnoreCase) == true
        || _geo.Network?.DatabaseType.StartsWith("DBIP", StringComparison.OrdinalIgnoreCase) == true;

    private static string TrafficText(long total, double rate) =>
        rate > 0 ? $"{Format.Bytes(total)} (now {Format.BitsPerSecond(rate * 8)})" : Format.Bytes(total);

    private RelatedLine Line(string key, string primary, string search)
    {
        if (_relatedLines.TryGetValue(key, out var line) && line.Primary == primary)
            return line;
        line = new RelatedLine(key, primary, OpenRelated) { Search = search };
        _relatedLines[key] = line;
        return line;
    }

    private const int MaxRelated = 200;

    private static void SetRelated(NetworkDetails details, List<RelatedLine> lines)
    {
        var wanted = lines.OrderByDescending(l => l.Weight).ThenBy(l => l.Primary, StringComparer.CurrentCultureIgnoreCase).Take(MaxRelated).ToList();
        Reconcile(details.Related, wanted);
    }

    /// <summary>A related line narrows History to it.</summary>
    private void OpenRelated(RelatedLine line)
    {
        IsHistoryView = true;
        SearchText = line.Search;
    }

    // ---------- Actions ----------

    private void OpenProcess()
    {
        if (_details is { } details && _processes.GetValueOrDefault(details.Process) is { HasEnded: false })
            _navigator.ShowProcesses(new ProcessQuery(ProcessId: details.Process.Id, CreateTime: details.Process.CreateTime));
    }

    private void ShowConnections()
    {
        if (_selected is MapPlace place)
        {
            IsHistoryView = true;
            SearchText = place.Search;
            return;
        }

        if (_details is not { } details)
            return;
        var pid = details.Process.Id;
        IsHistoryView = true;
        SearchText = PidPrefix + pid.ToString(CultureInfo.InvariantCulture);
    }

    private void ToggleWatch()
    {
        if (_details is not { } details || !_processes.TryGetValue(details.Process, out var info))
            return;

        if (!_settings.Watched.Remove(info.Name))
            _settings.Watched.Add(info.Name);
        _settings.Save();
        OnPropertyChanged(nameof(HasWatched));
        OnPropertyChanged(nameof(WatchedTip));
        UpdateMonitoring();
        Render(resort: false);
    }

    private void Copy()
    {
        if (_selected is ConnectionRow { Info: { } c })
            CopyRequested?.Invoke(c.Key.Remote.ToString());
        else if (_selected is ListenerRow { Info: { } l })
            CopyRequested?.Invoke(l.Address.WithPort(l.Port));
        else if (_selected is ProcessTrafficRow p && _processes.GetValueOrDefault(p.Process)?.Path is { } path)
            CopyRequested?.Invoke(path);
    }

    public Confirmation? Confirmation
    {
        get => _confirmation;
        private set
        {
            if (Set(ref _confirmation, value))
            {
                OnPropertyChanged(nameof(HasConfirmation));
                OnPropertyChanged(nameof(HasDialog));
            }
        }
    }

    public bool HasConfirmation => _confirmation is not null;

    private void AskCloseConnection()
    {
        if (_selected is not ConnectionRow { Info: { } c } || _details is not { } details)
            return;

        var key = details.Key;
        var name = ProcessName(c.Process);
        var remote = _hosts.Get(c.Key.Remote)?.Name ?? c.Key.Remote.WithPort(c.Key.RemotePort);
        Confirmation = new Confirmation(
            "Close this connection?",
            $"Windows resets the connection between {name} and {remote}. {name} sees it dropped, and may connect again.",
            Elevation.IsElevated ? null : "This needs administrator rights; without them Windows refuses.",
            "Close connection",
            () =>
            {
                Confirmation = null;
                details.Message = "Closing…";
                details.MessageIsError = false;
                _ = Task.Run(() =>
                {
                    var error = SocketTable.Close(c.Key.Local, c.Key.LocalPort, c.Key.Remote, c.Key.RemotePort);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_details is not { } shown || shown.Key != key)
                            return;
                        shown.Message = error switch
                        {
                            0 => "Closed.",
                            5 or 317 => "Windows refused: closing a connection needs administrator rights.",
                            _ => $"Windows couldn't close it (error {error.ToString(CultureInfo.InvariantCulture)}).",
                        };
                        shown.MessageIsError = error != 0;
                    });
                });
            },
            () => Confirmation = null);
    }

    private void ClearHistory()
    {
        foreach (var id in _connections.Where(e => !e.Value.IsOpen).Select(e => e.Key).ToList())
            _connections.Remove(id);
        _clearRequested = true;
        _recordingSince = DateTime.UtcNow;
        if (_selected is ConnectionRow { Info.IsOpen: false })
            Select(null);
        Render(resort: true);
    }

    /// <summary>Esc: a dialog first, then the details pane. False when there was nothing to close.</summary>
    public bool Dismiss()
    {
        if (_confirmation is not null)
        {
            Confirmation = null;
            return true;
        }

        if (_geoSetup is not null)
        {
            CloseSettings();
            return true;
        }

        if (_details is null)
            return false;

        Select(null);
        return true;
    }

    // ---------- Export ----------

    private string ExportName()
    {
        var what = _view switch
        {
            NetworkTab.Connections => "connections",
            NetworkTab.History => "connection history",
            NetworkTab.Listening => "listening ports",
            NetworkTab.Map => "connections on the map",
            _ => "network by process",
        };
        return $"{what} {DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)}.csv";
    }

    /// <summary>Writes everything the current view matches (not only the rows shown) to a CSV file.</summary>
    public async Task ExportAsync(string path)
    {
        var csv = BuildCsv(out var count);
        try
        {
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ShowNotice($"Saved {Format.Number(count)} rows to {Path.GetFileName(path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice($"Couldn't save the file: {ex.Message}");
        }
    }

    private string BuildCsv(out int count)
    {
        var search = NetworkSearch.Parse(_searchText);
        var csv = new StringBuilder();
        count = 0;
        switch (_view)
        {
            case NetworkTab.Connections or NetworkTab.History or NetworkTab.Map:
                csv.Append("Process,PID,Path,Services,Protocol,Direction,Local address,Local port,Remote address,Remote port,Host,Host source,Country,Place,Network,State,Open,Opened,First seen,Last seen,Sent bytes,Received bytes,Retransmits\r\n");
                foreach (var c in _connections.Values.OrderBy(c => c.FirstSeen))
                {
                    if (_view == NetworkTab.Connections && !c.IsOpen || _view == NetworkTab.Map && Place(c) is null || !Matches(c, search))
                        continue;
                    var p = _processes.GetValueOrDefault(c.Process);
                    var host = _hosts.Get(c.Key.Remote);
                    var geo = _geo.Lookup(c.Key.Remote);
                    NetworkText.Csv(csv, ProcessName(c.Process));
                    NetworkText.Csv(csv, NetworkText.Invariant(c.Process.Id));
                    NetworkText.Csv(csv, p?.Path);
                    NetworkText.Csv(csv, p is null ? null : string.Join("; ", p.Services));
                    NetworkText.Csv(csv, NetworkText.Protocol(c.Key));
                    NetworkText.Csv(csv, c.IsInbound ? "Incoming" : "Outgoing");
                    NetworkText.Csv(csv, c.Key.Local.ToString());
                    NetworkText.Csv(csv, NetworkText.Invariant(c.Key.LocalPort));
                    NetworkText.Csv(csv, c.Key.Remote.ToString());
                    NetworkText.Csv(csv, NetworkText.Invariant(c.Key.RemotePort));
                    NetworkText.Csv(csv, host?.Name);
                    NetworkText.Csv(csv, host is null ? null : host.Source == HostSource.Lookup ? "lookup" : "reverse");
                    NetworkText.Csv(csv, geo?.CountryCode);
                    NetworkText.Csv(csv, geo?.Place ?? (c.Key.Remote.IsRoutable ? null : AddressScopes.Describe(c.Key.Remote.Scope)));
                    NetworkText.Csv(csv, geo?.Network);
                    NetworkText.Csv(csv, c.ShortLived ? "Short-lived" : TcpStates.Describe(c));
                    NetworkText.Csv(csv, c.IsOpen ? "yes" : "no");
                    NetworkText.Csv(csv, NetworkText.Iso(c.Opened));
                    NetworkText.Csv(csv, NetworkText.Iso(c.FirstSeen));
                    NetworkText.Csv(csv, NetworkText.Iso(c.LastSeen));
                    NetworkText.Csv(csv, NetworkText.Invariant(c.Sent));
                    NetworkText.Csv(csv, NetworkText.Invariant(c.Received));
                    NetworkText.Csv(csv, NetworkText.Invariant(c.Retransmits), last: true);
                    count++;
                }

                break;

            case NetworkTab.Listening:
                csv.Append("Process,PID,Path,Services,Protocol,Address,Port,Usually,Reachable from,Since\r\n");
                foreach (var l in _listeners.OrderBy(l => l.Port))
                {
                    if (!Matches(l, search))
                        continue;
                    var p = _processes.GetValueOrDefault(l.Process);
                    NetworkText.Csv(csv, ProcessName(l.Process));
                    NetworkText.Csv(csv, NetworkText.Invariant(l.Process.Id));
                    NetworkText.Csv(csv, p?.Path);
                    NetworkText.Csv(csv, p is null ? null : string.Join("; ", p.Services));
                    NetworkText.Csv(csv, l.Protocol == Protocol.Tcp ? "TCP" : "UDP");
                    NetworkText.Csv(csv, l.Address.ToString());
                    NetworkText.Csv(csv, NetworkText.Invariant(l.Port));
                    NetworkText.Csv(csv, NetworkText.PortName(l.Port, l.Protocol));
                    NetworkText.Csv(csv, Exposure(l.Address).Text);
                    NetworkText.Csv(csv, NetworkText.Iso(l.Since), last: true);
                    count++;
                }

                break;

            default:
                csv.Append("Process,PID,Path,Services,Running,Open connections,Listening,Remote hosts,Received bytes,Sent bytes\r\n");
                foreach (var row in _processRows.Values.OrderBy(r => r.ProcessName, StringComparer.CurrentCultureIgnoreCase))
                {
                    var p = _processes.GetValueOrDefault(row.Process);
                    NetworkText.Csv(csv, row.ProcessName);
                    NetworkText.Csv(csv, NetworkText.Invariant(row.Process.Id));
                    NetworkText.Csv(csv, p?.Path);
                    NetworkText.Csv(csv, p is null ? null : string.Join("; ", p.Services));
                    NetworkText.Csv(csv, row.IsRunning ? "yes" : "no");
                    NetworkText.Csv(csv, NetworkText.Invariant(row.Open));
                    NetworkText.Csv(csv, NetworkText.Invariant(row.Listening));
                    NetworkText.Csv(csv, NetworkText.Invariant(row.Hosts));
                    NetworkText.Csv(csv, NetworkText.Invariant(row.Received));
                    NetworkText.Csv(csv, NetworkText.Invariant(row.Sent), last: true);
                    count++;
                }

                break;
        }

        return csv.ToString();
    }

    // ---------- Settings: recording, names and locations ----------

    public GeoSetup? GeoSetup
    {
        get => _geoSetup;
        private set
        {
            if (Set(ref _geoSetup, value))
                OnPropertyChanged(nameof(HasGeoSetup));
        }
    }

    public bool HasGeoSetup => _geoSetup is not null;

    /// <summary>Any dialog over the page: the rest of it is disabled meanwhile.</summary>
    public bool HasDialog => HasConfirmation || HasGeoSetup;

    public bool IsBackgroundOff
    {
        get => _settings.Background == BackgroundRecording.Off;
        set => SetBackground(value, BackgroundRecording.Off);
    }

    public bool IsBackgroundWatched
    {
        get => _settings.Background == BackgroundRecording.Watched;
        set => SetBackground(value, BackgroundRecording.Watched);
    }

    public bool IsBackgroundAll
    {
        get => _settings.Background == BackgroundRecording.All;
        set => SetBackground(value, BackgroundRecording.All);
    }

    public string BackgroundDescription => _settings.Background switch
    {
        BackgroundRecording.Off => "Connections are recorded only while this page is open.",
        BackgroundRecording.Watched => _settings.Watched.Count == 0
            ? "Watched processes are recorded while this page is closed. Nothing is watched yet: open a process or connection and choose Watch."
            : $"While this page is closed, new connections of {string.Join(", ", _settings.Watched.Order(StringComparer.OrdinalIgnoreCase))} are still recorded.",
        _ => "Every connection is recorded, also while this page is closed. This costs a little CPU all the time.",
    };

    private void SetBackground(bool on, BackgroundRecording mode)
    {
        if (!on || _settings.Background == mode)
            return;
        _settings.Background = mode;
        _settings.Save();
        OnPropertyChanged(nameof(IsBackgroundOff));
        OnPropertyChanged(nameof(IsBackgroundWatched));
        OnPropertyChanged(nameof(IsBackgroundAll));
        OnPropertyChanged(nameof(BackgroundDescription));
        UpdateMonitoring();
    }

    /// <summary>Ask DNS servers for names of addresses nothing looked up. Sends queries of its own.</summary>
    public bool ReverseLookups
    {
        get => _settings.ReverseLookups;
        set
        {
            if (_settings.ReverseLookups == value)
                return;
            _settings.ReverseLookups = value;
            _hosts.ReverseLookups = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoReverseLookups));
        }
    }

    public bool NoReverseLookups
    {
        get => !_settings.ReverseLookups;
        set
        {
            if (value)
                ReverseLookups = false;
        }
    }

    private void OpenSettings()
    {
        var setup = new GeoSetup(
            GeoLocator.Sources.Select(s => new GeoSourceItem(s, Download)),
            CloseSettings,
            () => _download?.Cancel(),
            () => ImportRequested?.Invoke(),
            RemoveGeo);
        FillGeoSetup(setup);
        GeoSetup = setup;
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(BackgroundDescription));
    }

    private void CloseSettings()
    {
        _download?.Cancel();
        GeoSetup = null;
        OnPropertyChanged(nameof(HasDialog));
    }

    private void FillGeoSetup(GeoSetup setup)
    {
        setup.HasLocation = _geo.Location is not null;
        setup.HasNetwork = _geo.Network is not null;
        setup.LocationStatus = _geo.Location is { } location ? GeoLocator.Describe(location) : "None. Locations show as a dash.";
        setup.NetworkStatus = _geo.Network is { } network ? GeoLocator.Describe(network) : "None. Network owners aren't shown.";
    }

    private async void Download(GeoSource source)
    {
        if (_geoSetup is not { IsBusy: false } setup)
            return;

        using var cancel = new CancellationTokenSource();
        _download = cancel;
        setup.IsBusy = true;
        setup.Progress = 0;
        setup.ProgressText = $"Downloading {source.Title.ToLowerInvariant()}…";
        setup.Message = string.Empty;
        try
        {
            await _geo.DownloadAsync(source, new Progress<double>(p =>
            {
                setup.Progress = p * 100;
                setup.ProgressText = $"Downloading {source.Title.ToLowerInvariant()}… {Format.Percent(p * 100)}";
            }), cancel.Token);
            setup.Message = $"{source.Title} installed.";
            setup.MessageIsError = false;
        }
        catch (OperationCanceledException)
        {
            setup.Message = "Download cancelled.";
            setup.MessageIsError = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            setup.Message = $"Couldn't download it: {ex.Message}";
            setup.MessageIsError = true;
        }
        finally
        {
            _download = null;
            setup.IsBusy = false;
            FillGeoSetup(setup);
        }
    }

    /// <summary>Adds a .mmdb file the user picked. UI thread.</summary>
    public async Task ImportGeoAsync(string path)
    {
        if (_geoSetup is not { IsBusy: false } setup)
            return;

        setup.IsBusy = true;
        setup.ProgressText = "Checking the file…";
        setup.Message = string.Empty;
        try
        {
            var kind = await _geo.ImportAsync(path, CancellationToken.None);
            setup.Message = kind == GeoKind.Location ? "Location database added." : "Network owner database added.";
            setup.MessageIsError = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            setup.Message = $"That file can't be used: {ex.Message}";
            setup.MessageIsError = true;
        }
        finally
        {
            setup.IsBusy = false;
            FillGeoSetup(setup);
        }
    }

    private void RemoveGeo(GeoKind kind)
    {
        _geo.Remove(kind);
        if (_geoSetup is { } setup)
            FillGeoSetup(setup);
    }

    private void OnGeoChanged()
    {
        OnPropertyChanged(nameof(HasMap));
        if (_view == NetworkTab.Map && !HasMap)
            IsConnectionsView = true;
        foreach (var row in _connectionRows.Values)
            row.Info = null;
        OnPropertyChanged(nameof(ShowGeoHint));
        if (_isShown)
            Render(resort: true);
    }

    // ---------- Map ----------

    private readonly Dictionary<string, MapPlace> _places = [];
    private IReadOnlyDictionary<string, int> _mapCountries = new Dictionary<string, int>();
    private int _mapVersion;
    private string _mapNote = string.Empty;

    /// <summary>Places, busiest first; the map draws them and the list below it shows them.</summary>
    public ObservableCollection<MapPlace> MapPlaces { get; } = [];

    /// <summary>Connections by country code, for shading.</summary>
    public IReadOnlyDictionary<string, int> MapCountries
    {
        get => _mapCountries;
        private set => Set(ref _mapCountries, value);
    }

    /// <summary>Bumped each render, so the map redraws places changed in place.</summary>
    public int MapVersion
    {
        get => _mapVersion;
        private set => Set(ref _mapVersion, value);
    }

    /// <summary>Where lines start: this computer's Windows region, if the map knows it.</summary>
    public string? MapHome { get; } =
        WorldMapData.Labels.ContainsKey(RegionInfo.CurrentRegion.TwoLetterISORegionName) ? RegionInfo.CurrentRegion.TwoLetterISORegionName : null;

    public string MapLegend => MapHome is null
        ? "Dots are where the other ends are, sized by connections; countries are shaded by how many end there."
        : $"Lines start at {RegionInfo.CurrentRegion.EnglishName}, this computer's Windows region, not a measured location. Dots are where the other ends are, sized by connections; countries are shaded by how many end there.";

    /// <summary>What couldn't be placed, and how precise the places are.</summary>
    public string MapNote
    {
        get => _mapNote;
        private set
        {
            if (Set(ref _mapNote, value))
                OnPropertyChanged(nameof(HasMapNote));
        }
    }

    public bool HasMapNote => _mapNote.Length > 0;

    public string MapAttribution => IsDbIp()
        ? $"{GeoLocator.Attribution} Map outline: Natural Earth."
        : "Map outline: Natural Earth.";

    /// <summary>Where the database puts a connection's other end: a city with coordinates, or a country. Null for private addresses or unknown ones.</summary>
    private (string Key, string Name, string Caption, double Longitude, double Latitude, string? Country, bool IsCountry, string Search)? Place(ConnectionInfo c)
    {
        if (_geo.Lookup(c.Key.Remote) is not { } geo)
            return null;

        var country = geo.Country ?? geo.CountryCode ?? "Unknown country";
        if (geo is { HasCoordinates: true })
        {
            // About 10 km: one dot per city, not per street.
            var latitude = Math.Round(geo.Latitude!.Value, 1);
            var longitude = Math.Round(geo.Longitude!.Value, 1);
            var name = geo.City ?? geo.Region ?? country;
            var search = geo.CountryCode is { } code ? $"country:{code} \"{name}\"" : $"\"{name}\"";
            return ($"ll:{latitude.ToString(CultureInfo.InvariantCulture)},{longitude.ToString(CultureInfo.InvariantCulture)}",
                name, geo.City is null ? country : $"{(geo.Region is { } region && region != geo.City ? region + ", " : string.Empty)}{country}",
                longitude, latitude, geo.CountryCode, false, search);
        }

        if (geo.CountryCode is { } cc && WorldMapData.Labels.TryGetValue(cc, out var label))
            return ($"cc:{cc}", country, "Whole country: the database doesn't say where in it", label.X, label.Y, cc, true, $"country:{cc}");

        return null;
    }

    private void RenderMap(NetworkSearch search, bool resort)
    {
        var groups = new Dictionary<string, (MapPlace Place, HashSet<ProcessRef> Processes)>();
        var countries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int unplaced = 0, matching = 0, countryOnly = 0;
        foreach (var c in _connections.Values)
        {
            if (!c.Key.Remote.IsRoutable || !Matches(c, search))
                continue;
            matching++;
            if (Place(c) is not { } p)
            {
                unplaced++;
                continue;
            }

            if (!groups.TryGetValue(p.Key, out var group))
            {
                if (!_places.TryGetValue(p.Key, out var place))
                    _places[p.Key] = place = new MapPlace(p.Key, Select);
                place.Connections.Clear();
                place.Count = place.Open = place.Incoming = place.Outgoing = 0;
                place.Bytes = 0;
                (place.Name, place.Caption, place.Longitude, place.Latitude, place.CountryCode, place.IsCountry, place.Search) =
                    (p.Name, p.Caption, p.Longitude, p.Latitude, p.Country, p.IsCountry, p.Search);
                groups[p.Key] = group = (place, []);
            }

            var m = group.Place;
            m.Connections.Add(c);
            m.Count++;
            if (c.IsOpen)
                m.Open++;
            if (c.IsInbound)
                m.Incoming++;
            else
                m.Outgoing++;
            m.Bytes += c.Sent + c.Received;
            group.Processes.Add(c.Process);
            if (p.Country is { } code)
                countries[code] = countries.GetValueOrDefault(code) + 1;
            if (p.IsCountry)
                countryOnly++;
        }

        foreach (var (place, processes) in groups.Values)
        {
            place.DirectionText = (place.Incoming, place.Outgoing) switch
            {
                (0, _) => "Outgoing",
                (_, 0) => "Incoming",
                _ => "Both ways",
            };
            place.CountText = Format.Number(place.Count);
            place.OpenText = Format.Number(place.Open);
            place.ProcessesText = Format.Number(processes.Count);
            place.BytesText = place.Bytes > 0 ? Format.Bytes(place.Bytes) : "—";
            place.ProcessName = processes.Count == 1 ? ProcessName(processes.First()) : $"{Format.Number(processes.Count)} processes";
        }

        var all = groups.Values.Select(g => g.Place).ToList();
        var wanted = Rank(all, p => p.Key, _places, MapPlaces, resort,
            list => list.OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
        Keep(_places, wanted, p => p.Key);
        Reconcile(MapPlaces, wanted);
        MapCountries = countries;
        MapVersion++;
        Note(all.Count, wanted.Count);

        var notes = new List<string>();
        if (unplaced > 0)
            notes.Add($"{Format.Number(unplaced)} of {Format.Number(matching)} internet connections aren't in the database");
        if (countryOnly > 0)
            notes.Add($"{Format.Number(countryOnly)} are placed by country only; a city database places them closer");
        MapNote = notes.Count == 0 ? string.Empty : string.Join("; ", notes) + ".";
        OnPropertyChanged(nameof(MapAttribution));
    }

    private void FillPlaceDetails(NetworkDetails details, MapPlace place)
    {
        details.Title = place.Name;
        details.Subtitle = place.Caption;
        details.StatusText = place.Open > 0 ? $"{Format.Number(place.Open)} open" : "None open";
        details.IsSuccess = place.Open > 0;
        details.IsWarning = false;

        var owners = place.Connections
            .Select(c => _geo.Lookup(c.Key.Remote)?.Network)
            .Where(n => n is not null)
            .GroupBy(n => n!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();
        var processes = place.Connections.Select(c => ProcessName(c.Process)).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

        var facts = new List<NetworkFact>
        {
            new("Connections", $"{Format.Number(place.Count)} recorded, {Format.Number(place.Open)} open now"),
            new("Direction", $"{Format.Number(place.Outgoing)} outgoing, {Format.Number(place.Incoming)} incoming"),
            new("Processes", processes.Count > 8 ? $"{string.Join(", ", processes.Take(8))} and {processes.Count - 8} more" : string.Join(", ", processes)),
            new("Addresses", Format.Number(place.Connections.Select(c => c.Key.Remote).Distinct().Count())),
        };
        if (owners.Count > 0)
            facts.Add(new("Networks", owners.Count > 4 ? $"{string.Join(Environment.NewLine, owners.Take(4))}{Environment.NewLine}and {owners.Count - 4} more" : string.Join(Environment.NewLine, owners)));
        if (place.Bytes > 0)
            facts.Add(new("Traffic", Format.Bytes(place.Bytes)));
        facts.Add(new("Position", place.IsCountry
            ? "Country only: the database doesn't say where in it"
            : $"{place.Latitude.ToString("0.0", CultureInfo.CurrentCulture)}°, {place.Longitude.ToString("0.0", CultureInfo.CurrentCulture)}° (approximate)"));
        details.Facts = facts;
        details.Attribution = IsDbIp() ? GeoLocator.Attribution : string.Empty;
        details.CanCloseConnection = false;
        details.CloseHint = string.Empty;

        details.RelatedTitle = "Connections";
        var lines = new List<RelatedLine>();
        foreach (var c in place.Connections)
        {
            var line = Line($"c:{c.Id}", ProcessName(c.Process), $"ip:{c.Key.Remote}");
            line.Secondary = $"{_hosts.Get(c.Key.Remote)?.Name ?? c.Key.Remote.ToString()} · {NetworkText.Protocol(c.Key)} {c.Key.RemotePort.ToString(CultureInfo.InvariantCulture)} · {TcpStates.Describe(c)}";
            line.Value = c.Sent + c.Received > 0 ? Format.Bytes(c.Sent + c.Received) : NetworkText.When(c.IsOpen ? c.FirstSeen : c.LastSeen);
            line.Weight = c.IsOpen ? double.MaxValue : c.LastSeen.Ticks;
            lines.Add(line);
        }

        SetRelated(details, lines);
    }


    // ---------- Helpers ----------

    /// <summary>Moves, inserts and removes so <paramref name="items"/> matches <paramref name="wanted"/>, keeping rows that stay.</summary>
    private static void Reconcile<T>(ObservableCollection<T> items, List<T> wanted) where T : class
    {
        var keep = wanted.ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
                items.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], wanted[i]))
                continue;

            var at = items.IndexOf(wanted[i]);
            if (at >= 0)
                items.Move(at, i);
            else
                items.Insert(i, wanted[i]);
        }
    }
}
