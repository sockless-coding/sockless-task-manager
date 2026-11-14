using System.Collections.ObjectModel;
using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

/// <summary>A label and value in the details pane.</summary>
public sealed record NetworkFact(string Label, string Value);

/// <summary>A line in the details pane's list (remote hosts, connections on a port…); opening it narrows the page to it.</summary>
public sealed class RelatedLine : ObservableObject
{
    private string _secondary = string.Empty;
    private string _value = string.Empty;

    internal RelatedLine(string key, string primary, Action<RelatedLine> open)
    {
        Key = key;
        Primary = primary;
        OpenCommand = new RelayCommand(() => open(this));
    }

    internal string Key { get; }

    /// <summary>What opening the line searches for.</summary>
    internal string Search { get; init; } = string.Empty;

    /// <summary>Sort value: larger first.</summary>
    internal double Weight { get; set; }

    public string Primary { get; }

    public string Secondary
    {
        get => _secondary;
        set => Set(ref _secondary, value);
    }

    public string Value
    {
        get => _value;
        set => Set(ref _value, value);
    }

    public ICommand OpenCommand { get; }

    public string AccessibleName => $"{Primary}, {_secondary}, {_value}";
}

internal enum DetailsKind
{
    Connection,
    Listener,
    Process,
}

/// <summary>
/// The details pane for the selected row: a connection, a listening port, or a process with its
/// traffic graph. Each offers a way to its process on the Processes page, to watch that process,
/// and to narrow the page to what's related.
/// </summary>
public sealed class NetworkDetails : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSuccess;
    private bool _isWarning;
    private IReadOnlyList<NetworkFact> _facts = [];
    private string _relatedTitle = string.Empty;
    private bool _isWatched;
    private bool _canOpenProcess;
    private bool _canCloseConnection;
    private string _closeHint = string.Empty;
    private string _message = string.Empty;
    private bool _messageIsError;
    private double _maximum = 100_000;
    private string _scale = string.Empty;
    private string _attribution = string.Empty;

    internal NetworkDetails(string key, DetailsKind kind, ProcessRef process, Actions actions)
    {
        Key = key;
        Kind = kind;
        Process = process;
        CloseCommand = new RelayCommand(actions.Close);
        OpenProcessCommand = new RelayCommand(actions.OpenProcess);
        WatchCommand = new RelayCommand(actions.ToggleWatch);
        ShowConnectionsCommand = new RelayCommand(actions.ShowConnections);
        CopyCommand = new RelayCommand(actions.Copy);
        CloseConnectionCommand = new RelayCommand(actions.CloseConnection);
    }

    internal sealed record Actions(Action Close, Action OpenProcess, Action ToggleWatch, Action ShowConnections, Action Copy, Action CloseConnection);

    internal string Key { get; }

    internal DetailsKind Kind { get; }

    internal ProcessRef Process { get; }

    public bool IsConnection => Kind == DetailsKind.Connection;

    public bool IsProcess => Kind == DetailsKind.Process;

    public ICommand CloseCommand { get; }

    public ICommand OpenProcessCommand { get; }

    public ICommand WatchCommand { get; }

    public ICommand ShowConnectionsCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand CloseConnectionCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public string CopyText => Kind == DetailsKind.Listener ? "Copy address" : "Copy remote address";

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (Set(ref _subtitle, value))
                OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool HasSubtitle => _subtitle.Length > 0;

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        set => Set(ref _isSuccess, value);
    }

    public bool IsWarning
    {
        get => _isWarning;
        set => Set(ref _isWarning, value);
    }

    public IReadOnlyList<NetworkFact> Facts
    {
        get => _facts;
        set
        {
            if (!_facts.SequenceEqual(value))
            {
                _facts = value;
                OnPropertyChanged();
            }
        }
    }

    public string RelatedTitle
    {
        get => _relatedTitle;
        set
        {
            if (Set(ref _relatedTitle, value))
                OnPropertyChanged(nameof(HasRelatedTitle));
        }
    }

    public bool HasRelatedTitle => _relatedTitle.Length > 0;

    public ObservableCollection<RelatedLine> Related { get; } = [];

    /// <summary>Data credit for the location database, where the details show a location.</summary>
    public string Attribution
    {
        get => _attribution;
        set
        {
            if (Set(ref _attribution, value))
                OnPropertyChanged(nameof(HasAttribution));
        }
    }

    public bool HasAttribution => _attribution.Length > 0;

    public bool IsWatched
    {
        get => _isWatched;
        set
        {
            if (Set(ref _isWatched, value))
            {
                OnPropertyChanged(nameof(WatchText));
                OnPropertyChanged(nameof(WatchTip));
            }
        }
    }

    public string WatchText => _isWatched ? "Watching" : "Watch";

    public string WatchTip => _isWatched
        ? "Stop watching every process with this name"
        : "Watch every process with this name: its connections are recorded even while this page is closed";

    /// <summary>Still running, so the Processes page can show it.</summary>
    public bool CanOpenProcess
    {
        get => _canOpenProcess;
        set => Set(ref _canOpenProcess, value);
    }

    /// <summary>An open IPv4 TCP connection: Windows can reset it.</summary>
    public bool CanCloseConnection
    {
        get => _canCloseConnection;
        set
        {
            if (Set(ref _canCloseConnection, value))
                OnPropertyChanged(nameof(OfferElevation));
        }
    }

    /// <summary>Why it can't be closed, when it can't.</summary>
    public string CloseHint
    {
        get => _closeHint;
        set
        {
            if (Set(ref _closeHint, value))
                OnPropertyChanged(nameof(HasCloseHint));
        }
    }

    public bool HasCloseHint => _closeHint.Length > 0;

    public bool OfferElevation => _canCloseConnection && !Elevation.IsElevated;

    public string Message
    {
        get => _message;
        set
        {
            if (Set(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    public bool MessageIsError
    {
        get => _messageIsError;
        set => Set(ref _messageIsError, value);
    }

    // ---------- Traffic graph (processes, while tracing) ----------

    public History? Receive { get; internal set; }

    public History? Send { get; internal set; }

    public bool HasGraph => Receive is not null;

    /// <summary>Bits per second at the top of the graph.</summary>
    public double Maximum
    {
        get => _maximum;
        set => Set(ref _maximum, value);
    }

    public string Scale
    {
        get => _scale;
        set => Set(ref _scale, value);
    }
}

/// <summary>A free database offered for download.</summary>
public sealed class GeoSourceItem
{
    internal GeoSourceItem(GeoSource source, Action<GeoSource> download)
    {
        Title = source.Title;
        Description = source.Description;
        DownloadCommand = new RelayCommand(() => download(source));
    }

    public string Title { get; }

    public string Description { get; }

    public ICommand DownloadCommand { get; }
}

/// <summary>
/// The "Names and locations" dialog: reverse lookups on or off, and the location and network-owner
/// databases, which the user downloads or adds; none ships with the app.
/// </summary>
public sealed class GeoSetup : ObservableObject
{
    private string _locationStatus = string.Empty;
    private string _networkStatus = string.Empty;
    private bool _hasLocation;
    private bool _hasNetwork;
    private bool _isBusy;
    private double _progress;
    private string _progressText = string.Empty;
    private string _message = string.Empty;
    private bool _messageIsError;

    internal GeoSetup(IEnumerable<GeoSourceItem> sources, Action close, Action cancel, Action import, Action<GeoKind> remove)
    {
        Sources = [.. sources];
        CloseCommand = new RelayCommand(close);
        CancelCommand = new RelayCommand(cancel);
        ImportCommand = new RelayCommand(import);
        RemoveLocationCommand = new RelayCommand(() => remove(GeoKind.Location));
        RemoveNetworkCommand = new RelayCommand(() => remove(GeoKind.Network));
    }

    public IReadOnlyList<GeoSourceItem> Sources { get; }

    public ICommand CloseCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ImportCommand { get; }

    public ICommand RemoveLocationCommand { get; }

    public ICommand RemoveNetworkCommand { get; }

    public string Attribution => GeoLocator.Attribution;

    public string Folder => GeoLocator.Folder;

    public string LocationStatus
    {
        get => _locationStatus;
        set => Set(ref _locationStatus, value);
    }

    public string NetworkStatus
    {
        get => _networkStatus;
        set => Set(ref _networkStatus, value);
    }

    public bool HasLocation
    {
        get => _hasLocation;
        set => Set(ref _hasLocation, value);
    }

    public bool HasNetwork
    {
        get => _hasNetwork;
        set => Set(ref _hasNetwork, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isBusy;

    /// <summary>0 to 100.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => Set(ref _progressText, value);
    }

    public string Message
    {
        get => _message;
        set
        {
            if (Set(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    public bool MessageIsError
    {
        get => _messageIsError;
        set => Set(ref _messageIsError, value);
    }
}
