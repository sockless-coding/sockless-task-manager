using System.Collections.ObjectModel;
using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Users;

/// <summary>A label and value in the details pane.</summary>
public sealed record UserFact(string Label, string Value);

/// <summary>One of the selected user's processes. Opening it shows the process on the Processes page.</summary>
public sealed class UserProcessLine : ObservableObject
{
    private string _cpuText = string.Empty;
    private string _memoryText = string.Empty;

    internal UserProcessLine(string name, int id, Action<UserProcessLine> open)
    {
        Name = name;
        IdText = id.ToString(System.Globalization.CultureInfo.CurrentCulture);
        OpenCommand = new RelayCommand(() => open(this));
    }

    public string Name { get; }

    public ICommand OpenCommand { get; }

    public string AccessibleName => $"{Name}, PID {IdText}";

    /// <summary>Which process, for life: an id can be reused once it exits.</summary>
    internal (int Id, long CreateTime) Key { get; init; }

    public string IdText { get; }

    public string CpuText
    {
        get => _cpuText;
        set => Set(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        set => Set(ref _memoryText, value);
    }

    /// <summary>Last sample's memory, which orders the list.</summary>
    internal ulong Memory { get; set; }
}

/// <summary>
/// The details pane for the selected row: who it is, its session or account facts, the processes
/// it runs, and for a session, disconnect and sign out.
/// </summary>
public sealed class UserDetails : ObservableObject
{
    private string _name = string.Empty;
    private string _subtitle = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSuccess;
    private IReadOnlyList<UserFact> _facts = [];
    private string _processesTitle = string.Empty;
    private string _hiddenText = string.Empty;
    private bool _canDisconnect;
    private bool _canManage;
    private string _message = string.Empty;
    private bool _messageIsError;

    internal UserDetails(string key, bool isSession, Action close, Action disconnect, Action signOut, Action showProcesses)
    {
        Key = key;
        IsSession = isSession;
        CloseCommand = new RelayCommand(close);
        DisconnectCommand = new RelayCommand(disconnect);
        SignOutCommand = new RelayCommand(signOut);
        ShowProcessesCommand = new RelayCommand(showProcesses);
    }

    internal string Key { get; }

    /// <summary>A signed-in session, which can be disconnected and signed out; otherwise an account.</summary>
    public bool IsSession { get; }

    public ICommand CloseCommand { get; }

    public ICommand DisconnectCommand { get; }

    public ICommand SignOutCommand { get; }

    /// <summary>All of them on the Processes page, filtered to this user (and session).</summary>
    public ICommand ShowProcessesCommand { get; }

    /// <summary>False for the unreadable group: the Processes page can't filter on an owner it doesn't know.</summary>
    public bool CanShowProcesses { get; init; } = true;

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
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

    public IReadOnlyList<UserFact> Facts
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

    public ObservableCollection<UserProcessLine> Processes { get; } = [];

    public string ProcessesTitle
    {
        get => _processesTitle;
        set => Set(ref _processesTitle, value);
    }

    /// <summary>What isn't listed because Windows won't say who runs it.</summary>
    public string HiddenText
    {
        get => _hiddenText;
        set
        {
            if (Set(ref _hiddenText, value))
            {
                OnPropertyChanged(nameof(HasHidden));
                OnPropertyChanged(nameof(OfferElevation));
            }
        }
    }

    public bool HasHidden => _hiddenText.Length > 0;

    public bool OfferElevation => HasHidden && !Elevation.IsElevated;

    /// <summary>Connected now, so there is something to disconnect.</summary>
    public bool CanDisconnect
    {
        get => _canDisconnect;
        set => Set(ref _canDisconnect, value);
    }

    /// <summary>This app's own session, or elevated: Windows lets it act on the session.</summary>
    public bool CanManage
    {
        get => _canManage;
        set
        {
            if (Set(ref _canManage, value))
                OnPropertyChanged(nameof(NeedsElevation));
        }
    }

    public bool NeedsElevation => IsSession && !_canManage;

    /// <summary>The outcome of the last action.</summary>
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
