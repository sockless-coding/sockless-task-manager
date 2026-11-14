using System.Globalization;
using System.Runtime.CompilerServices;
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

/// <summary>What to do on a failure, in the recovery page's lists.</summary>
public sealed record RecoveryOption(string Title)
{
    internal RecoveryAction Action { get; init; }
}

/// <summary>A unit for a <see cref="DurationField"/>.</summary>
public sealed record DurationUnit(string Title)
{
    internal ulong Milliseconds { get; init; }
}

/// <summary>The edit form's pages, as the Services console's tabs.</summary>
public enum EditorPage
{
    General,
    LogOn,
    Recovery,
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
    private IReadOnlyList<ServiceFact> _logOnFacts = [];
    private IReadOnlyList<ServiceFact> _recoveryFacts = [];
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
        EditLogOnCommand = new RelayCommand(() => actions.EditPage(EditorPage.LogOn));
        EditRecoveryCommand = new RelayCommand(() => actions.EditPage(EditorPage.Recovery));
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

    /// <summary>The edit form, open at its log-on page.</summary>
    public ICommand EditLogOnCommand { get; }

    /// <summary>The edit form, open at its recovery page.</summary>
    public ICommand EditRecoveryCommand { get; }

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

    /// <summary>Who it runs as. Empty for drivers, which have no account.</summary>
    public IReadOnlyList<ServiceFact> LogOnFacts
    {
        get => _logOnFacts;
        set
        {
            if (!_logOnFacts.SequenceEqual(value))
            {
                _logOnFacts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasLogOn));
            }
        }
    }

    public bool HasLogOn => _logOnFacts.Count > 0;

    /// <summary>What Windows does when it fails. Empty for drivers, and where the settings can't be read.</summary>
    public IReadOnlyList<ServiceFact> RecoveryFacts
    {
        get => _recoveryFacts;
        set
        {
            if (!_recoveryFacts.SequenceEqual(value))
            {
                _recoveryFacts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRecovery));
            }
        }
    }

    public bool HasRecovery => _recoveryFacts.Count > 0;

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
    Action<EditorPage> EditPage,
    Action Uninstall,
    Action OpenProcess);

/// <summary>
/// The edit form, in the Services console's three pages: General (name, description, how it starts,
/// its command line), Log on (the account it runs as) and Recovery (what Windows does when it
/// fails). Saving sends what changed on every page, and only that.
/// </summary>
public sealed class ServiceEditor : ObservableObject
{
    internal const string LocalSystem = "LocalSystem";
    internal const string LocalService = @"NT AUTHORITY\LocalService";
    internal const string NetworkService = @"NT AUTHORITY\NetworkService";

    /// <summary>What the Services console appends for "Append fail count": the program gets the count as /fail=N.</summary>
    internal const string FailCountSuffix = "/fail=%1%";

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

    private static readonly RecoveryOption[] Actions =
    [
        new("Take no action") { Action = RecoveryAction.None },
        new("Restart the service") { Action = RecoveryAction.Restart },
        new("Run a program") { Action = RecoveryAction.RunCommand },
        new("Restart the computer") { Action = RecoveryAction.Reboot },
    ];

    private readonly ServiceConfig _original;
    private readonly string _originalDisplayName;
    private EditorPage _page;
    private string _displayName;
    private string _description;
    private StartOption _start;
    private string _imagePath;
    private AccountKind _accountKind;
    private string _accountName;
    private string _password = string.Empty;
    private string _confirmPassword = string.Empty;
    private bool _allowDesktop;
    private RecoveryOption _first;
    private RecoveryOption _second;
    private RecoveryOption _subsequent;
    private string _command;
    private bool _appendFailCount;
    private string _rebootMessage;
    private bool _onErrorStops;
    private string _error = string.Empty;

    internal ServiceEditor(string displayName, ServiceConfig config, bool isDriver, EditorPage page, Action<ServiceChange> save, Action cancel)
    {
        _original = config;
        _originalDisplayName = displayName;
        _displayName = displayName;
        _description = config.Description;
        _imagePath = config.ImagePath;
        IsDriver = isDriver;
        _page = isDriver ? EditorPage.General : page;
        StartOptions = isDriver ? DriverStarts : ServiceStarts;
        _start = StartOptions.FirstOrDefault(o => o.Mode == config.Start)
            ?? StartOptions.First(o => o.Mode == StartMode.Manual);
        (_accountKind, _accountName) = Classify(config.Account);
        _allowDesktop = config.IsInteractive;

        var recovery = config.Recovery;
        _first = Option(recovery.First);
        _second = Option(recovery.Second);
        _subsequent = Option(recovery.Subsequent);
        (_command, _appendFailCount) = SplitFailCount(recovery.Command);
        _rebootMessage = recovery.RebootMessage;
        _onErrorStops = recovery.OnErrorStops;
        ResetAfter = DurationField.FromSeconds(recovery.ResetSeconds, allowsNever: true);
        ResetAfter.Label = "Reset the failure count after";
        RestartAfter = DurationField.FromMilliseconds(recovery.RestartDelay);
        RestartAfter.Label = "Restart the service after";
        RebootAfter = DurationField.FromMilliseconds(recovery.RebootDelay);
        RebootAfter.Label = "Restart the computer after";

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

    /// <summary>Drivers have no account and no recovery, and two more startup types.</summary>
    public bool IsDriver { get; }

    /// <summary>The Log on and Recovery pages; drivers have only General.</summary>
    public bool HasPages => !IsDriver;

    internal EditorPage Page
    {
        get => _page;
        set
        {
            if (IsDriver || _page == value)
                return;

            _page = value;
            OnPropertyChanged(nameof(IsGeneralPage));
            OnPropertyChanged(nameof(IsLogOnPage));
            OnPropertyChanged(nameof(IsRecoveryPage));
        }
    }

    public bool IsGeneralPage
    {
        get => _page == EditorPage.General;
        set
        {
            if (value)
                Page = EditorPage.General;
        }
    }

    public bool IsLogOnPage
    {
        get => _page == EditorPage.LogOn;
        set
        {
            if (value)
                Page = EditorPage.LogOn;
        }
    }

    public bool IsRecoveryPage
    {
        get => _page == EditorPage.Recovery;
        set
        {
            if (value)
                Page = EditorPage.Recovery;
        }
    }

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

    // ---------- Log on ----------

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

    /// <summary>SERVICE_INTERACTIVE_PROCESS: only for Local System.</summary>
    public bool AllowDesktop
    {
        get => _allowDesktop;
        set => Set(ref _allowDesktop, value);
    }

    // ---------- Recovery ----------

    public IReadOnlyList<RecoveryOption> RecoveryOptions => Actions;

    public RecoveryOption FirstFailure
    {
        get => _first;
        set => SetAction(ref _first, value);
    }

    public RecoveryOption SecondFailure
    {
        get => _second;
        set => SetAction(ref _second, value);
    }

    public RecoveryOption SubsequentFailures
    {
        get => _subsequent;
        set => SetAction(ref _subsequent, value);
    }

    /// <summary>How long without a failure before the count starts again; empty for never.</summary>
    public DurationField ResetAfter { get; }

    public DurationField RestartAfter { get; }

    public DurationField RebootAfter { get; }

    /// <summary>The program for "Run a program", with its arguments.</summary>
    public string Command
    {
        get => _command;
        set => Set(ref _command, value);
    }

    public bool AppendFailCount
    {
        get => _appendFailCount;
        set => Set(ref _appendFailCount, value);
    }

    /// <summary>Sent to signed-in users before the computer restarts; empty for Windows' own.</summary>
    public string RebootMessage
    {
        get => _rebootMessage;
        set => Set(ref _rebootMessage, value);
    }

    /// <summary>Count a stop with an error code as a failure, not only a crash.</summary>
    public bool OnErrorStops
    {
        get => _onErrorStops;
        set => Set(ref _onErrorStops, value);
    }

    public bool HasAnyAction => Uses(RecoveryAction.Restart) || Uses(RecoveryAction.RunCommand) || Uses(RecoveryAction.Reboot);

    public bool ShowsRestart => Uses(RecoveryAction.Restart);

    public bool ShowsCommand => Uses(RecoveryAction.RunCommand);

    public bool ShowsReboot => Uses(RecoveryAction.Reboot);

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

    private bool Uses(RecoveryAction action) => _first.Action == action || _second.Action == action || _subsequent.Action == action;

    private void SetAction(ref RecoveryOption field, RecoveryOption? value, [CallerMemberName] string? propertyName = null)
    {
        if (value is null || !Set(ref field, value, propertyName))
            return;

        OnPropertyChanged(nameof(HasAnyAction));
        OnPropertyChanged(nameof(ShowsRestart));
        OnPropertyChanged(nameof(ShowsCommand));
        OnPropertyChanged(nameof(ShowsReboot));
    }

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

    /// <summary>The first problem found, on the page it's on.</summary>
    private bool Validate()
    {
        (Error, var page) = string.IsNullOrWhiteSpace(_displayName) ? ("Give it a name.", EditorPage.General)
            : string.IsNullOrWhiteSpace(_imagePath) ? ("Say what program it runs.", EditorPage.General)
            : IsDriver ? (string.Empty, _page)
            : _accountKind == AccountKind.Other && string.IsNullOrWhiteSpace(_accountName) ? ("Name the account it runs as.", EditorPage.LogOn)
            : _accountKind == AccountKind.Other && _password != _confirmPassword ? ("The passwords don't match.", EditorPage.LogOn)
            : HasAnyAction && ResetAfter.Seconds() is null ? ("Say how long until the failure count starts again, as a number, or leave it empty for never.", EditorPage.Recovery)
            : ShowsRestart && RestartAfter.Milliseconds() is null ? ("Say how long to wait before restarting the service, as a number.", EditorPage.Recovery)
            : ShowsReboot && RebootAfter.Milliseconds() is null ? ("Say how long to wait before restarting the computer, as a number.", EditorPage.Recovery)
            : ShowsCommand && string.IsNullOrWhiteSpace(_command) ? ("Say what program to run.", EditorPage.Recovery)
            : (string.Empty, _page);
        Page = page;
        return Error.Length == 0;
    }

    /// <summary>Only what differs from what Windows has; null when nothing does.</summary>
    private ServiceChange? Change()
    {
        var displayName = _displayName.Trim();
        var imagePath = _imagePath.Trim();
        string? account = null;
        string? password = null;
        uint? type = null;
        ServiceRecovery? recovery = null;
        if (!IsDriver)
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

            // Windows refuses the desktop for any other account, so another account clears it.
            var interactive = _accountKind == AccountKind.LocalSystem && _allowDesktop;
            var wantedType = interactive ? _original.Type | ServiceManager.InteractiveProcess : _original.Type & ~ServiceManager.InteractiveProcess;
            if (wantedType != _original.Type)
                type = wantedType;

            var original = _original.Recovery;
            var command = _command.Trim();
            var edited = new ServiceRecovery(
                _first.Action,
                _second.Action,
                _subsequent.Action,
                HasAnyAction ? ResetAfter.Seconds() ?? original.ResetSeconds : original.ResetSeconds,
                ShowsRestart ? RestartAfter.Milliseconds() ?? original.RestartDelay : original.RestartDelay,
                ShowsReboot ? RebootAfter.Milliseconds() ?? original.RebootDelay : original.RebootDelay,
                original.CommandDelay,
                _appendFailCount && command.Length > 0 ? $"{command} {FailCountSuffix}" : command,
                _rebootMessage.Trim(),
                _onErrorStops);
            if (edited != original)
                recovery = edited;
        }

        var change = new ServiceChange(
            displayName == _originalDisplayName ? null : displayName,
            _description == _original.Description ? null : _description,
            _start.Mode == _original.Start ? null : _start.Mode,
            imagePath == _original.ImagePath ? null : imagePath,
            account,
            password,
            type,
            recovery);

        return change == new ServiceChange(null, null, null, null, null, null, null, null) ? null : change;
    }

    private static RecoveryOption Option(RecoveryAction action) => Actions.FirstOrDefault(o => o.Action == action) ?? Actions[0];

    /// <summary>The command without the fail-count suffix, and whether it had one.</summary>
    private static (string, bool) SplitFailCount(string command)
    {
        var trimmed = command.TrimEnd();
        return trimmed.EndsWith(FailCountSuffix, StringComparison.OrdinalIgnoreCase)
            ? (trimmed[..^FailCountSuffix.Length].TrimEnd(), true)
            : (command, false);
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

/// <summary>
/// A length of time in the recovery page: a number and a unit, shown in the largest unit that keeps
/// it whole, so 86,400 seconds read as 1 day and 1,500 ms as 1.5 seconds.
/// </summary>
public sealed class DurationField : ObservableObject
{
    private static readonly DurationUnit[] AllUnits =
    [
        new("seconds") { Milliseconds = 1_000 },
        new("minutes") { Milliseconds = 60_000 },
        new("hours") { Milliseconds = 3_600_000 },
        new("days") { Milliseconds = 86_400_000 },
    ];

    private string _text;
    private DurationUnit _unit;

    private DurationField(string text, DurationUnit unit, bool allowsNever)
    {
        _text = text;
        _unit = unit;
        AllowsNever = allowsNever;
    }

    public IReadOnlyList<DurationUnit> Units => AllUnits;

    /// <summary>What it's for, for screen readers.</summary>
    public string Label { get; internal set; } = string.Empty;

    /// <summary>Empty means never (the reset period only).</summary>
    public bool AllowsNever { get; }

    public string Placeholder => AllowsNever ? "Never" : "0";

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public DurationUnit Unit
    {
        get => _unit;
        set
        {
            if (value is not null)
                Set(ref _unit, value);
        }
    }

    internal static DurationField FromMilliseconds(uint milliseconds)
    {
        var (number, unit) = Split(milliseconds);
        return new DurationField(number, unit, allowsNever: false);
    }

    internal static DurationField FromSeconds(uint seconds, bool allowsNever)
    {
        if (allowsNever && seconds == ServiceRecovery.Never)
            return new DurationField(string.Empty, AllUnits[3], allowsNever);

        var (number, unit) = Split(seconds * 1000UL);
        return new DurationField(number, unit, allowsNever);
    }

    /// <summary>"1 day", "90 seconds", "1.5 seconds".</summary>
    internal static string Describe(ulong milliseconds)
    {
        var (number, unit) = Split(milliseconds);
        var title = number == 1.ToString(CultureInfo.CurrentCulture) ? unit.Title[..^1] : unit.Title;
        return $"{number} {title}";
    }

    /// <summary>The time in milliseconds; null when it isn't a number, is negative or doesn't fit.</summary>
    internal uint? Milliseconds() =>
        Parse() is { } ms && ms <= uint.MaxValue ? (uint)ms : null;

    /// <summary>The time in whole seconds, or <see cref="ServiceRecovery.Never"/> when empty and allowed.</summary>
    internal uint? Seconds()
    {
        if (AllowsNever && string.IsNullOrWhiteSpace(_text))
            return ServiceRecovery.Never;

        return Parse() is { } ms && ms / 1000 < ServiceRecovery.Never ? (uint)Math.Round(ms / 1000.0) : null;
    }

    private ulong? Parse()
    {
        if (!double.TryParse(_text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var number)
            || double.IsNaN(number) || number < 0)
            return null;

        var ms = Math.Round(number * _unit.Milliseconds);
        return ms > ulong.MaxValue / 2 ? null : (ulong)ms;
    }

    private static (string, DurationUnit) Split(ulong milliseconds)
    {
        if (milliseconds == 0)
            return ("0", AllUnits[1]);

        for (var i = AllUnits.Length - 1; i >= 0; i--)
        {
            if (milliseconds % AllUnits[i].Milliseconds == 0)
                return ((milliseconds / AllUnits[i].Milliseconds).ToString(CultureInfo.CurrentCulture), AllUnits[i]);
        }

        return ((milliseconds / 1000.0).ToString("0.###", CultureInfo.CurrentCulture), AllUnits[0]);
    }
}
