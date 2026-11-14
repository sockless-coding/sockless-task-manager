using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Services;

/// <summary>A label and value in the details pane.</summary>
public sealed record ServiceFact(string Label, string Value);

/// <summary>A startup type in the edit form's list.</summary>
public sealed record StartOption(string Title, string Description)
{
    internal StartMode Mode { get; init; }
}

/// <summary>
/// The details pane for the selected service: what it is and runs, how it starts and as whom, what
/// it needs and what needs it; start, stop, restart, pause, edit and uninstall.
/// </summary>
public sealed class ServiceDetails : ObservableObject
{
    private string _displayName = string.Empty;
    private string _description = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSuccess;
    private bool _isWarning;
    private IReadOnlyList<ServiceFact> _facts = [];
    private bool _canStart;
    private bool _canStop;
    private bool _canPause;
    private bool _canContinue;
    private bool _canEdit;
    private bool _canUninstall;
    private bool _isMissingProgram;
    private bool _isBusy;
    private int _processId;
    private string _message = string.Empty;
    private bool _messageIsError;
    private ServiceEditor? _editor;

    internal ServiceDetails(string name, ServiceDetailActions actions)
    {
        Name = name;
        CloseCommand = new RelayCommand(actions.Close);
        StartCommand = new RelayCommand(actions.Start);
        StopCommand = new RelayCommand(actions.Stop);
        RestartCommand = new RelayCommand(actions.Restart);
        PauseCommand = new RelayCommand(actions.Pause);
        ContinueCommand = new RelayCommand(actions.Continue);
        EditCommand = new RelayCommand(actions.Edit);
        UninstallCommand = new RelayCommand(actions.Uninstall);
        OpenProcessCommand = new RelayCommand(actions.OpenProcess);
    }

    /// <summary>The service's key name.</summary>
    public string Name { get; }

    public ICommand CloseCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ContinueCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand UninstallCommand { get; }

    /// <summary>Its process on the Processes page.</summary>
    public ICommand OpenProcessCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public bool NeedsElevation => !Elevation.IsElevated;

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string Description
    {
        get => _description;
        set
        {
            if (Set(ref _description, value))
                OnPropertyChanged(nameof(HasDescription));
        }
    }

    public bool HasDescription => _description.Length > 0;

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

    public IReadOnlyList<ServiceFact> Facts
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

    public bool CanStart
    {
        get => _canStart;
        set => Set(ref _canStart, value);
    }

    public bool CanStop
    {
        get => _canStop;
        set => Set(ref _canStop, value);
    }

    /// <summary>Running and accepts pause; Pause shows only then.</summary>
    public bool CanPause
    {
        get => _canPause;
        set => Set(ref _canPause, value);
    }

    /// <summary>Paused; Resume shows only then.</summary>
    public bool CanContinue
    {
        get => _canContinue;
        set => Set(ref _canContinue, value);
    }

    /// <summary>Its settings could be read, and nothing's under way.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set => Set(ref _canEdit, value);
    }

    public bool CanUninstall
    {
        get => _canUninstall;
        set => Set(ref _canUninstall, value);
    }

    /// <summary>The program it's registered to run isn't on disk.</summary>
    public bool IsMissingProgram
    {
        get => _isMissingProgram;
        set => Set(ref _isMissingProgram, value);
    }

    /// <summary>An action is under way; the buttons wait for it.</summary>
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

    public int ProcessId
    {
        get => _processId;
        set
        {
            if (Set(ref _processId, value))
            {
                OnPropertyChanged(nameof(HasProcess));
                OnPropertyChanged(nameof(ProcessText));
            }
        }
    }

    public bool HasProcess => _processId > 0;

    public string ProcessText => $"Process {_processId.ToString(System.Globalization.CultureInfo.CurrentCulture)}";

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

    /// <summary>The edit form, while it's open.</summary>
    public ServiceEditor? Editor
    {
        get => _editor;
        set
        {
            if (Set(ref _editor, value))
            {
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(IsViewing));
            }
        }
    }

    public bool IsEditing => _editor is not null;

    public bool IsViewing => _editor is null;
}

internal sealed record ServiceDetailActions(
    Action Close,
    Action Start,
    Action Stop,
    Action Restart,
    Action Pause,
    Action Continue,
    Action Edit,
    Action Uninstall,
    Action OpenProcess);

/// <summary>
/// The edit form: the service's name and description, how it starts, its command line and the
/// account it runs as. Only what's changed is sent to Windows.
/// </summary>
public sealed class ServiceEditor : ObservableObject
{
    internal const string LocalSystem = "LocalSystem";
    internal const string LocalService = @"NT AUTHORITY\LocalService";
    internal const string NetworkService = @"NT AUTHORITY\NetworkService";

    private static readonly StartOption[] ServiceStarts =
    [
        new("Automatic", "Starts with Windows") { Mode = StartMode.Automatic },
        new("Automatic (delayed start)", "Starts shortly after Windows has") { Mode = StartMode.AutomaticDelayed },
        new("Manual", "Starts when something asks for it") { Mode = StartMode.Manual },
        new("Disabled", "Never starts") { Mode = StartMode.Disabled },
    ];

    private static readonly StartOption[] DriverStarts =
    [
        new("Boot", "Loaded by the boot loader") { Mode = StartMode.Boot },
        new("System", "Loaded while the kernel starts") { Mode = StartMode.System },
        new("Automatic", "Loaded with Windows") { Mode = StartMode.Automatic },
        new("Manual", "Loaded when something asks for it") { Mode = StartMode.Manual },
        new("Disabled", "Never loaded") { Mode = StartMode.Disabled },
    ];

    private readonly ServiceConfig _original;
    private readonly string _originalDisplayName;
    private string _displayName;
    private string _description;
    private StartOption _start;
    private string _imagePath;
    private AccountKind _accountKind;
    private string _accountName;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private string _error = string.Empty;

    internal ServiceEditor(string displayName, ServiceConfig config, bool isDriver, Action<ServiceChange> save, Action cancel)
    {
        _original = config;
        _originalDisplayName = displayName;
        _displayName = displayName;
        _description = config.Description;
        _imagePath = config.ImagePath;
        IsDriver = isDriver;
        StartOptions = isDriver ? DriverStarts : ServiceStarts;
        _start = StartOptions.FirstOrDefault(o => o.Mode == config.Start)
            ?? StartOptions.First(o => o.Mode == StartMode.Manual);
        (_accountKind, _accountName) = Classify(config.Account);
        SaveCommand = new RelayCommand(() =>
        {
            if (Validate() && Change() is { } change)
                save(change);
            else if (Error.Length == 0)
                cancel();
        });
        CancelCommand = new RelayCommand(cancel);
    }

    private enum AccountKind
    {
        LocalSystem,
        LocalService,
        NetworkService,
        Other,
    }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>Drivers have no account, and two more startup types.</summary>
    public bool IsDriver { get; }

    public bool HasAccount => !IsDriver;

    public IReadOnlyList<StartOption> StartOptions { get; }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string Description
    {
        get => _description;
        set => Set(ref _description, value);
    }

    public StartOption Start
    {
        get => _start;
        set
        {
            if (value is not null)
                Set(ref _start, value);
        }
    }

    public string ImagePath
    {
        get => _imagePath;
        set => Set(ref _imagePath, value);
    }

    public bool IsLocalSystem
    {
        get => _accountKind == AccountKind.LocalSystem;
        set => SetAccount(value, AccountKind.LocalSystem);
    }

    public bool IsLocalService
    {
        get => _accountKind == AccountKind.LocalService;
        set => SetAccount(value, AccountKind.LocalService);
    }

    public bool IsNetworkService
    {
        get => _accountKind == AccountKind.NetworkService;
        set => SetAccount(value, AccountKind.NetworkService);
    }

    public bool IsOtherAccount
    {
        get => _accountKind == AccountKind.Other;
        set => SetAccount(value, AccountKind.Other);
    }

    /// <summary>DOMAIN\user, .\user, user@domain or NT SERVICE\name.</summary>
    public string AccountName
    {
        get => _accountName;
        set => Set(ref _accountName, value);
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public string ConfirmPassword
    {
        get => _confirmPassword;
        set => Set(ref _confirmPassword, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    private void SetAccount(bool isChecked, AccountKind kind)
    {
        if (!isChecked || _accountKind == kind)
            return;

        _accountKind = kind;
        OnPropertyChanged(nameof(IsLocalSystem));
        OnPropertyChanged(nameof(IsLocalService));
        OnPropertyChanged(nameof(IsNetworkService));
        OnPropertyChanged(nameof(IsOtherAccount));
    }

    private bool Validate()
    {
        Error = string.IsNullOrWhiteSpace(_displayName) ? "Give it a name."
            : string.IsNullOrWhiteSpace(_imagePath) ? "Say what program it runs."
            : HasAccount && _accountKind == AccountKind.Other && string.IsNullOrWhiteSpace(_accountName) ? "Name the account it runs as."
            : HasAccount && _accountKind == AccountKind.Other && _password != _confirmPassword ? "The passwords don't match."
            : string.Empty;
        return Error.Length == 0;
    }

    /// <summary>Only what differs from what Windows has; null when nothing does.</summary>
    private ServiceChange? Change()
    {
        var displayName = _displayName.Trim();
        var imagePath = _imagePath.Trim();
        string? account = null;
        string? password = null;
        if (HasAccount)
        {
            var wanted = _accountKind switch
            {
                AccountKind.LocalSystem => LocalSystem,
                AccountKind.LocalService => LocalService,
                AccountKind.NetworkService => NetworkService,
                _ => Qualify(_accountName.Trim()),
            };

            if (!string.Equals(wanted, _original.Account, StringComparison.OrdinalIgnoreCase) || _password.Length > 0)
            {
                account = wanted;
                password = _accountKind == AccountKind.Other ? _password : string.Empty;
            }
        }

        var change = new ServiceChange(
            displayName == _originalDisplayName ? null : displayName,
            _description == _original.Description ? null : _description,
            _start.Mode == _original.Start ? null : _start.Mode,
            imagePath == _original.ImagePath ? null : imagePath,
            account,
            password);

        return change == new ServiceChange(null, null, null, null, null, null) ? null : change;
    }

    /// <summary>A bare user name means a local account: ".\name".</summary>
    private static string Qualify(string account) =>
        account.Contains('\\') || account.Contains('@') ? account : $@".\{account}";

    private static (AccountKind, string) Classify(string account)
    {
        if (account.Length == 0 || string.Equals(account, LocalSystem, StringComparison.OrdinalIgnoreCase)
            || string.Equals(account, @".\LocalSystem", StringComparison.OrdinalIgnoreCase))
            return (AccountKind.LocalSystem, string.Empty);
        if (string.Equals(account, LocalService, StringComparison.OrdinalIgnoreCase)
            || string.Equals(account, @"NT AUTHORITY\Local Service", StringComparison.OrdinalIgnoreCase))
            return (AccountKind.LocalService, string.Empty);
        if (string.Equals(account, NetworkService, StringComparison.OrdinalIgnoreCase)
            || string.Equals(account, @"NT AUTHORITY\Network Service", StringComparison.OrdinalIgnoreCase))
            return (AccountKind.NetworkService, string.Empty);
        return (AccountKind.Other, account);
    }
}
