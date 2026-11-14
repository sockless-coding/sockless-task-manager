using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

/// <summary>What a table can be sorted by.</summary>
internal enum SortKey
{
    Process,
    Protocol,
    Local,
    Remote,
    Location,
    State,
    Sent,
    Received,
    Seen,
    Port,
    Exposure,
    Connections,
    Hosts,
    ReceiveRate,
    SendRate,
}

/// <summary>A sortable column title. Active while it's the sort of the table it heads.</summary>
public sealed class NetworkColumn : ObservableObject
{
    private bool _isActive;
    private bool _isDescending;

    internal NetworkColumn(string title, SortKey key, bool descendingFirst, Action<NetworkColumn> sort, bool alignRight = false)
    {
        Title = title;
        Key = key;
        DescendingFirst = descendingFirst;
        Alignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        SortCommand = new RelayCommand(() => sort(this));
    }

    public string Title { get; }

    internal SortKey Key { get; }

    /// <summary>Numbers sort largest first on the first click; names A to Z.</summary>
    internal bool DescendingFirst { get; }

    public HorizontalAlignment Alignment { get; }

    public ICommand SortCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsDescending
    {
        get => _isDescending;
        set
        {
            if (Set(ref _isDescending, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public Geometry? Glyph => ThemeResource.Geometry(_isDescending ? "IconChevronDown" : "IconChevronUp");

    public string AccessibleName => _isActive
        ? $"{Title}, sorted {(_isDescending ? "descending" : "ascending")}"
        : $"Sort by {Title}";
}

/// <summary>The connection tables' sortable columns.</summary>
public sealed class ConnectionColumns
{
    /// <param name="seenTitle">"Since" for open connections, "Last seen" for the history.</param>
    internal ConnectionColumns(Action<NetworkColumn> sort, string seenTitle)
    {
        Process = new("Process", SortKey.Process, false, sort);
        Protocol = new("Protocol", SortKey.Protocol, false, sort);
        Local = new("Local", SortKey.Local, false, sort);
        Remote = new("Remote", SortKey.Remote, false, sort);
        Location = new("Location", SortKey.Location, false, sort);
        State = new("State", SortKey.State, false, sort);
        Sent = new("Sent", SortKey.Sent, true, sort, alignRight: true);
        Received = new("Received", SortKey.Received, true, sort, alignRight: true);
        Seen = new(seenTitle, SortKey.Seen, true, sort, alignRight: true);
        All = [Process, Protocol, Local, Remote, Location, State, Sent, Received, Seen];
    }

    public NetworkColumn Process { get; }

    public NetworkColumn Protocol { get; }

    public NetworkColumn Local { get; }

    public NetworkColumn Remote { get; }

    public NetworkColumn Location { get; }

    public NetworkColumn State { get; }

    public NetworkColumn Sent { get; }

    public NetworkColumn Received { get; }

    public NetworkColumn Seen { get; }

    internal NetworkColumn[] All { get; }
}

/// <summary>The listening ports table's sortable columns.</summary>
public sealed class ListenerColumns
{
    internal ListenerColumns(Action<NetworkColumn> sort)
    {
        Process = new("Process", SortKey.Process, false, sort);
        Protocol = new("Protocol", SortKey.Protocol, false, sort);
        Local = new("Address", SortKey.Local, false, sort);
        Port = new("Port", SortKey.Port, false, sort, alignRight: true);
        Exposure = new("Reachable from", SortKey.Exposure, true, sort);
        Connections = new("Connections", SortKey.Connections, true, sort, alignRight: true);
        Seen = new("Since", SortKey.Seen, true, sort, alignRight: true);
        All = [Process, Protocol, Local, Port, Exposure, Connections, Seen];
    }

    public NetworkColumn Process { get; }

    public NetworkColumn Protocol { get; }

    public NetworkColumn Local { get; }

    public NetworkColumn Port { get; }

    public NetworkColumn Exposure { get; }

    public NetworkColumn Connections { get; }

    public NetworkColumn Seen { get; }

    internal NetworkColumn[] All { get; }
}

/// <summary>The processes table's sortable columns.</summary>
public sealed class ProcessColumns
{
    internal ProcessColumns(Action<NetworkColumn> sort)
    {
        Process = new("Process", SortKey.Process, false, sort);
        Connections = new("Open", SortKey.Connections, true, sort, alignRight: true);
        Port = new("Listening", SortKey.Port, true, sort, alignRight: true);
        Hosts = new("Remote hosts", SortKey.Hosts, true, sort, alignRight: true);
        ReceiveRate = new("Receive", SortKey.ReceiveRate, true, sort, alignRight: true);
        SendRate = new("Send", SortKey.SendRate, true, sort, alignRight: true);
        Received = new("Received", SortKey.Received, true, sort, alignRight: true);
        Sent = new("Sent", SortKey.Sent, true, sort, alignRight: true);
        All = [Process, Connections, Port, Hosts, ReceiveRate, SendRate, Received, Sent];
    }

    public NetworkColumn Process { get; }

    public NetworkColumn Connections { get; }

    public NetworkColumn Port { get; }

    public NetworkColumn Hosts { get; }

    public NetworkColumn ReceiveRate { get; }

    public NetworkColumn SendRate { get; }

    public NetworkColumn Received { get; }

    public NetworkColumn Sent { get; }

    internal NetworkColumn[] All { get; }
}

/// <summary>Shared by every row: selection and a name for screen readers.</summary>
public abstract class NetworkRow : ObservableObject
{
    private bool _isSelected;
    private string _processName = string.Empty;
    private string _processCaption = string.Empty;

    protected NetworkRow(string key, Action<NetworkRow> select)
    {
        Key = key;
        SelectCommand = new RelayCommand(() => select(this));
    }

    internal string Key { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (Set(ref _processName, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>"PID 1234 · DNS Client".</summary>
    public string ProcessCaption
    {
        get => _processCaption;
        set => Set(ref _processCaption, value);
    }

    public abstract string AccessibleName { get; }
}

/// <summary>One connection in the Connections or History table, updated in place.</summary>
public sealed class ConnectionRow : NetworkRow
{
    private string _protocolText = string.Empty;
    private string _directionText = string.Empty;
    private string _localText = string.Empty;
    private string _remoteText = string.Empty;
    private string _remoteCaption = string.Empty;
    private string _locationText = string.Empty;
    private string _locationCaption = string.Empty;
    private string _stateText = string.Empty;
    private bool _isActive;
    private bool _isShortLived;
    private string _sentText = string.Empty;
    private string _receivedText = string.Empty;
    private string _sendRateText = string.Empty;
    private string _receiveRateText = string.Empty;
    private string _seenText = string.Empty;

    internal ConnectionRow(long id, Action<NetworkRow> select)
        : base($"c:{id}", select)
    {
        Id = id;
    }

    internal long Id { get; }

    internal ConnectionInfo? Info { get; set; }

    public string ProtocolText
    {
        get => _protocolText;
        set => Set(ref _protocolText, value);
    }

    /// <summary>"Outgoing" or "Incoming": who connected to whom.</summary>
    public string DirectionText
    {
        get => _directionText;
        set => Set(ref _directionText, value);
    }

    public string LocalText
    {
        get => _localText;
        set => Set(ref _localText, value);
    }

    /// <summary>The host name if known, else the address and port.</summary>
    public string RemoteText
    {
        get => _remoteText;
        set
        {
            if (Set(ref _remoteText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>The address and port under a host name; empty when the address is the main line.</summary>
    public string RemoteCaption
    {
        get => _remoteCaption;
        set
        {
            if (Set(ref _remoteCaption, value))
                OnPropertyChanged(nameof(HasRemoteCaption));
        }
    }

    public bool HasRemoteCaption => _remoteCaption.Length > 0;

    public string LocationText
    {
        get => _locationText;
        set => Set(ref _locationText, value);
    }

    /// <summary>The network that owns the address.</summary>
    public string LocationCaption
    {
        get => _locationCaption;
        set
        {
            if (Set(ref _locationCaption, value))
                OnPropertyChanged(nameof(HasLocationCaption));
        }
    }

    public bool HasLocationCaption => _locationCaption.Length > 0;

    /// <summary>A word, always: Connected, Closed, Active…</summary>
    public string StateText
    {
        get => _stateText;
        set
        {
            if (Set(ref _stateText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>Connected or carrying traffic: the success badge.</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    /// <summary>Opened and closed between two samples: the warning badge says so.</summary>
    public bool IsShortLived
    {
        get => _isShortLived;
        set => Set(ref _isShortLived, value);
    }

    public string SentText
    {
        get => _sentText;
        set => Set(ref _sentText, value);
    }

    public string ReceivedText
    {
        get => _receivedText;
        set => Set(ref _receivedText, value);
    }

    public string SendRateText
    {
        get => _sendRateText;
        set
        {
            if (Set(ref _sendRateText, value))
                OnPropertyChanged(nameof(HasSendRate));
        }
    }

    public bool HasSendRate => _sendRateText.Length > 0;

    public string ReceiveRateText
    {
        get => _receiveRateText;
        set
        {
            if (Set(ref _receiveRateText, value))
                OnPropertyChanged(nameof(HasReceiveRate));
        }
    }

    public bool HasReceiveRate => _receiveRateText.Length > 0;

    /// <summary>When it opened (open), or was last seen (closed).</summary>
    public string SeenText
    {
        get => _seenText;
        set => Set(ref _seenText, value);
    }

    public override string AccessibleName => $"{ProcessName} to {_remoteText}, {_stateText}";
}

/// <summary>One listening or bound socket.</summary>
public sealed class ListenerRow : NetworkRow
{
    private string _connectionsText = string.Empty;
    private string _exposureText = string.Empty;
    private bool _isExposed;

    internal ListenerRow(string key, Action<NetworkRow> select)
        : base(key, select)
    {
    }

    internal ListenerInfo? Info { get; set; }

    internal int ConnectionCount { get; set; }

    public string ProtocolText { get; internal set; } = string.Empty;

    public string AddressText { get; internal set; } = string.Empty;

    public string PortText { get; internal set; } = string.Empty;

    /// <summary>"Any network", "This computer", "Local network"…</summary>
    public string ExposureText
    {
        get => _exposureText;
        set => Set(ref _exposureText, value);
    }

    /// <summary>Reachable from other computers: the warning badge.</summary>
    public bool IsExposed
    {
        get => _isExposed;
        set => Set(ref _isExposed, value);
    }

    public string SinceText { get; internal set; } = string.Empty;

    public string ConnectionsText
    {
        get => _connectionsText;
        set => Set(ref _connectionsText, value);
    }

    public override string AccessibleName => $"{ProcessName}, {ProtocolText} port {PortText}, {_exposureText}";
}

/// <summary>One process that has used the network while it was monitored.</summary>
public sealed class ProcessTrafficRow : NetworkRow
{
    private string _statusText = string.Empty;
    private bool _isRunning;
    private bool _isWatched;
    private string _connectionsText = string.Empty;
    private string _listeningText = string.Empty;
    private string _hostsText = string.Empty;
    private string _receiveRateText = string.Empty;
    private string _sendRateText = string.Empty;
    private string _receivedText = string.Empty;
    private string _sentText = string.Empty;

    internal ProcessTrafficRow(ProcessRef process, Action<NetworkRow> select)
        : base($"p:{process.Id}:{process.CreateTime}", select)
    {
        Process = process;
    }

    internal ProcessRef Process { get; }

    // Sort values, as of the last render.
    internal int Open { get; set; }

    internal int Listening { get; set; }

    internal int Hosts { get; set; }

    internal double ReceiveRate { get; set; }

    internal double SendRate { get; set; }

    internal long Received { get; set; }

    internal long Sent { get; set; }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => Set(ref _isRunning, value);
    }

    public bool IsWatched
    {
        get => _isWatched;
        set => Set(ref _isWatched, value);
    }

    public string ConnectionsText
    {
        get => _connectionsText;
        set => Set(ref _connectionsText, value);
    }

    public string ListeningText
    {
        get => _listeningText;
        set => Set(ref _listeningText, value);
    }

    public string HostsText
    {
        get => _hostsText;
        set => Set(ref _hostsText, value);
    }

    public string ReceiveRateText
    {
        get => _receiveRateText;
        set => Set(ref _receiveRateText, value);
    }

    public string SendRateText
    {
        get => _sendRateText;
        set => Set(ref _sendRateText, value);
    }

    public string ReceivedText
    {
        get => _receivedText;
        set => Set(ref _receivedText, value);
    }

    public string SentText
    {
        get => _sentText;
        set => Set(ref _sentText, value);
    }

    public override string AccessibleName => $"{ProcessName}, {_statusText}{(_isWatched ? ", watched" : string.Empty)}";
}
