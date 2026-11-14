using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Services;

/// <summary>
/// The Services page's state: every service (and optionally driver), grouped by who made it or in one
/// list, filtered by state and a search, sorted by a column. The selected one's details can start,
/// stop, restart, pause, edit and uninstall it. Samples only while the page is on screen.
/// </summary>
public sealed class ServiceList : ObservableObject
{
    private const int StateAll = 0;
    private const int StateRunning = 1;
    private const int StateStopped = 2;

    private readonly Navigator _navigator;
    private readonly ServiceActions _rowActions;

    // Sampling thread.
    private readonly ServicesSampler _sampler = new();

    // Shared between threads.
    private volatile bool _isShown;
    private volatile bool _includeDrivers;
    private ServicesSnapshot? _pending;

    // UI thread.
    private readonly Dictionary<string, ServiceRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServiceGroupRow> _groups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folded groups, by name. Windows starts folded: it's most of the list, and rarely what you came for.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase) { Publisher.WindowsName };

    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private ServicesSnapshot? _latest;
    private int _stateFilter = StateAll;
    private bool _isGrouped = true;
    private string _searchText = string.Empty;
    private ServiceColumn _sortColumn;
    private string _summary = "Reading services…";
    private string _notice = string.Empty;
    private ServiceRow? _selected;
    private ServiceDetails? _details;
    private Confirmation? _confirmation;
    private Geometry? _confirmIcon;
    private bool _confirmIsDanger;
    private bool _isEmpty;
    private bool _isLoaded;

    internal ServiceList(Navigator navigator)
    {
        _navigator = navigator;
        _rowActions = new ServiceActions(Select, row => Act(row, Start), row => Act(row, AskStop), row => Act(row, AskRestart),
            row => Act(row, Pause), row => Act(row, Continue), row => Act(row, Edit), row => Act(row, AskUninstall),
            row => CopyRequested?.Invoke(row.Name));

        NameColumn = new ServiceColumn("Name", ServiceSortKey.Name, Sort);
        StatusColumn = new ServiceColumn("Status", ServiceSortKey.Status, Sort);
        StartupColumn = new ServiceColumn("Startup", ServiceSortKey.Startup, Sort);
        AccountColumn = new ServiceColumn("Log on as", ServiceSortKey.Account, Sort);
        ProcessIdColumn = new ServiceColumn("PID", ServiceSortKey.ProcessId, Sort, alignRight: true);
        PublisherColumn = new ServiceColumn("Publisher", ServiceSortKey.Publisher, Sort);
        _sortColumn = NameColumn;
        NameColumn.IsActive = true;

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
    }

    /// <summary>The view puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>Set by the view; the page samples only while it's shown.</summary>
    public bool IsShown
    {
        get => _isShown;
        set => _isShown = value;
    }

    /// <summary>Group headers and services, in display order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    public ServiceColumn NameColumn { get; }

    public ServiceColumn StatusColumn { get; }

    public ServiceColumn StartupColumn { get; }

    public ServiceColumn AccountColumn { get; }

    public ServiceColumn ProcessIdColumn { get; }

    public ServiceColumn PublisherColumn { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    /// <summary>Without elevation Windows lists services but won't let most be changed.</summary>
    public bool ShowElevationHint => !Elevation.IsElevated;

    public bool IsAllStates
    {
        get => _stateFilter == StateAll;
        set => SetStateFilter(value, StateAll);
    }

    public bool IsRunningOnly
    {
        get => _stateFilter == StateRunning;
        set => SetStateFilter(value, StateRunning);
    }

    public bool IsStoppedOnly
    {
        get => _stateFilter == StateStopped;
        set => SetStateFilter(value, StateStopped);
    }

    public bool IsGrouped
    {
        get => _isGrouped;
        set
        {
            if (Set(ref _isGrouped, value))
            {
                OnPropertyChanged(nameof(IsFlat));
                Render();
            }
        }
    }

    public bool IsFlat
    {
        get => !_isGrouped;
        set => IsGrouped = !value;
    }

    /// <summary>Kernel and file system drivers too, as well as services.</summary>
    public bool IncludeDrivers
    {
        get => _includeDrivers;
        set
        {
            if (_includeDrivers == value)
                return;

            _includeDrivers = value;
            OnPropertyChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearch));
                Render();
            }
        }
    }

    public bool HasSearch => _searchText.Length > 0;

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>The outcome of an action whose service has since left the list (uninstalled).</summary>
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

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    /// <summary>Nothing listed yet: the first read is under way.</summary>
    public bool IsLoading => !_isLoaded;

    public string EmptyTitle => HasSearch ? "No services match" : "No services to show";

    public string EmptyHint => HasSearch ? "Try another search, or show every state." : "Try showing every state.";

    public ServiceDetails? Details
    {
        get => _details;
        private set
        {
            if (Set(ref _details, value))
                OnPropertyChanged(nameof(HasDetails));
        }
    }

    public bool HasDetails => _details is not null;

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

    /// <summary>The confirm button's icon: what the action does.</summary>
    public Geometry? ConfirmIcon
    {
        get => _confirmIcon;
        private set => Set(ref _confirmIcon, value);
    }

    /// <summary>Uninstalling is destructive (rust); stopping what depends on it isn't.</summary>
    public bool ConfirmIsDanger
    {
        get => _confirmIsDanger;
        private set
        {
            if (Set(ref _confirmIsDanger, value))
                OnPropertyChanged(nameof(ConfirmIsSoft));
        }
    }

    public bool ConfirmIsSoft => !_confirmIsDanger;

    /// <summary>Esc: the dialog first, then the edit form, then the details pane. False when there was nothing to close.</summary>
    public bool Dismiss()
    {
        if (_confirmation is not null)
        {
            Confirmation = null;
            return true;
        }

        if (_details is { Editor: not null } editing)
        {
            editing.Editor = null;
            return true;
        }

        if (_details is null)
            return false;

        Select(null);
        return true;
    }

    // ---------- Sampling ----------

    internal void Sample()
    {
        if (!_isShown)
            return;

        Volatile.Write(ref _pending, _sampler.Sample(_includeDrivers));
    }

    internal void Apply()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot)
            return;

        _latest = snapshot;
        if (!_isLoaded)
        {
            _isLoaded = true;
            OnPropertyChanged(nameof(IsLoading));
        }

        Render();
    }

    private void SetStateFilter(bool isChecked, int filter)
    {
        if (!isChecked || _stateFilter == filter)
            return;

        _stateFilter = filter;
        OnPropertyChanged(nameof(IsAllStates));
        OnPropertyChanged(nameof(IsRunningOnly));
        OnPropertyChanged(nameof(IsStoppedOnly));
        Render();
    }

    private void Sort(ServiceColumn column)
    {
        if (ReferenceEquals(column, _sortColumn))
        {
            column.IsDescending = !column.IsDescending;
        }
        else
        {
            _sortColumn.IsActive = false;
            _sortColumn = column;
            column.IsActive = true;
            column.IsDescending = false;
        }

        Render();
    }

    // ---------- Rendering ----------

    private void Render()
    {
        if (_latest is not { } snapshot)
            return;

        var names = new HashSet<string>(snapshot.Services.Count, StringComparer.OrdinalIgnoreCase);
        var shown = new List<ServiceRow>(snapshot.Services.Count);
        foreach (var entry in snapshot.Services)
        {
            names.Add(entry.Status.Name);
            if (!_rows.TryGetValue(entry.Status.Name, out var row))
                _rows[entry.Status.Name] = row = new ServiceRow(entry.Status.Name, _rowActions);

            row.Entry = entry;
            Fill(row, entry);
            if (Matches(entry))
                shown.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !names.Contains(k)).ToList())
            _rows.Remove(gone);

        shown.Sort(Compare);
        var wanted = new List<object>(shown.Count + 32);
        if (_isGrouped)
        {
            var searching = HasSearch;
            var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in shown.GroupBy(r => r.Entry!.Publisher.Name, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(g => GroupRank(g.Key))
                         .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                groupNames.Add(group.Key);
                if (!_groups.TryGetValue(group.Key, out var header))
                    _groups[group.Key] = header = new ServiceGroupRow(group.Key, !_collapsed.Contains(group.Key), Toggle);

                var count = group.Count();
                var running = group.Count(r => r.Entry!.Status.State != ServiceState.Stopped);
                header.CountText = $"{Plural(count, snapshot.Drivers ? "item" : "service")} · {Format.Number(running)} running";

                // A search shows every match, folded or not.
                header.IsExpanded = searching || !_collapsed.Contains(group.Key);
                wanted.Add(header);
                if (header.IsExpanded)
                    wanted.AddRange(group);
            }

            foreach (var gone in _groups.Keys.Where(k => !groupNames.Contains(k)).ToList())
                _groups.Remove(gone);
        }
        else
        {
            wanted.AddRange(shown);
        }

        Reconcile(Items, wanted);
        IsEmpty = shown.Count == 0;
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        Summary = SummaryOf(snapshot);

        if (_selected is not null)
        {
            if (!_rows.TryGetValue(_selected.Name, out var current))
                Select(null);
            else if (_details is { } details && current.Entry is { } entry)
                FillDetails(details, entry, snapshot);
        }
    }

    private bool Matches(ServiceEntry entry)
    {
        var state = entry.Status.State;
        if (_stateFilter == StateRunning && state == ServiceState.Stopped)
            return false;
        if (_stateFilter == StateStopped && state != ServiceState.Stopped)
            return false;

        if (_searchText.Length == 0)
            return true;

        var search = _searchText.Trim();
        return Contains(entry.Status.DisplayName, search)
            || Contains(entry.Status.Name, search)
            || Contains(entry.Publisher.Name, search)
            || Contains(entry.Publisher.Program, search)
            || (entry.Config is { } config && (Contains(config.Description, search) || Contains(config.ImagePath, search)));
    }

    private static bool Contains(string text, string search) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private int Compare(ServiceRow a, ServiceRow b)
    {
        var x = a.Entry!;
        var y = b.Entry!;
        var result = _sortColumn.Key switch
        {
            ServiceSortKey.Status => StateRank(x.Status.State).CompareTo(StateRank(y.Status.State)),
            ServiceSortKey.Startup => StartRank(x.Config).CompareTo(StartRank(y.Config)),
            ServiceSortKey.Account => string.Compare(a.AccountText, b.AccountText, StringComparison.CurrentCultureIgnoreCase),
            ServiceSortKey.ProcessId => x.Status.ProcessId.CompareTo(y.Status.ProcessId),
            ServiceSortKey.Publisher => string.Compare(x.Publisher.Name, y.Publisher.Name, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };

        if (result == 0)
            result = string.Compare(x.Status.DisplayName, y.Status.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        if (result == 0)
            result = string.Compare(x.Status.Name, y.Status.Name, StringComparison.OrdinalIgnoreCase);
        return _sortColumn.IsDescending ? -result : result;
    }

    /// <summary>Missing programs first (worth a look), then other publishers A to Z, then Microsoft's and Windows' own.</summary>
    private static int GroupRank(string publisher) => publisher switch
    {
        Publisher.MissingName => 0,
        Publisher.UnknownName => 2,
        Publisher.MicrosoftName => 3,
        Publisher.WindowsName => 4,
        _ => 1,
    };

    private static int StateRank(ServiceState state) => state switch
    {
        ServiceState.Running => 0,
        ServiceState.StartPending or ServiceState.ContinuePending => 1,
        ServiceState.Paused or ServiceState.PausePending => 2,
        ServiceState.StopPending => 3,
        ServiceState.Stopped => 4,
        _ => 5,
    };

    private static int StartRank(ServiceConfig? config) => config?.Start switch
    {
        StartMode.Boot => 0,
        StartMode.System => 1,
        StartMode.Automatic => 2,
        StartMode.AutomaticDelayed => 3,
        StartMode.Manual => config.HasTriggers ? 4 : 5,
        StartMode.Disabled => 6,
        _ => 7,
    };

    private void Toggle(ServiceGroupRow group)
    {
        // While searching every group is open; folding one then would only hide matches.
        if (HasSearch)
            return;

        if (!_collapsed.Remove(group.Name))
            _collapsed.Add(group.Name);
        Render();
    }

    private void Fill(ServiceRow row, ServiceEntry entry)
    {
        var status = entry.Status;
        row.DisplayName = status.DisplayName;
        row.StatusText = StateText(status.State);
        row.IsSuccess = status.State == ServiceState.Running;
        row.IsWarning = status.State is not (ServiceState.Running or ServiceState.Stopped);
        row.StartupText = StartText(entry.Config);
        row.AccountText = IsDriver(status.Type) ? "—" : AccountText(entry.Config?.Account);
        row.ProcessIdText = status.ProcessId > 0 ? status.ProcessId.ToString(CultureInfo.CurrentCulture) : string.Empty;
        row.PublisherText = entry.Publisher.Name;
        row.ToolTip = entry.Config is { Description.Length: > 0 } config ? $"{status.Name}\n\n{config.Description}" : status.Name;

        var busy = _busy.Contains(status.Name);
        row.CanStart = !busy && status.State == ServiceState.Stopped && entry.Config?.Start != StartMode.Disabled;
        row.CanStop = !busy && status.State is ServiceState.Running or ServiceState.Paused && (status.Controls & ServiceManager.AcceptStop) != 0;
        row.CanPause = !busy && status.State == ServiceState.Running && (status.Controls & ServiceManager.AcceptPauseContinue) != 0;
        row.CanContinue = !busy && status.State == ServiceState.Paused;
    }

    private string SummaryOf(ServicesSnapshot snapshot)
    {
        var total = snapshot.Services.Count;
        var running = snapshot.Services.Count(s => s.Status.State != ServiceState.Stopped);
        var publishers = snapshot.Services.Select(s => s.Publisher.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var what = snapshot.Drivers ? "services and drivers" : total == 1 ? "service" : "services";
        return $"{Format.Number(total)} {what}, {Format.Number(running)} running · {Plural(publishers, "publisher")}";
    }

    // ---------- Details ----------

    private void Select(ServiceRow? row)
    {
        if (row is not null && ReferenceEquals(row, _selected))
            row = null;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = row;
        if (row?.Entry is not { } entry || _latest is not { } snapshot)
        {
            _selected = null;
            Details = null;
            return;
        }

        row.IsSelected = true;
        Notice = string.Empty;
        var details = new ServiceDetails(row.Name, new ServiceDetailActions(
            () => Select(null), Start, AskStop, AskRestart, Pause, Continue, Edit, page => Edit(page), AskUninstall, OpenProcess));
        FillDetails(details, entry, snapshot);
        Details = details;

        // Read its settings afresh, in case they changed elsewhere since.
        _sampler.Refresh(row.Name);
    }

    /// <summary>Selects the row (if it isn't already), then acts on it: the outcome shows in its details.</summary>
    private void Act(ServiceRow row, Action action)
    {
        if (!ReferenceEquals(row, _selected))
            Select(row);
        if (ReferenceEquals(row, _selected))
            action();
    }

    private void FillDetails(ServiceDetails details, ServiceEntry entry, ServicesSnapshot snapshot)
    {
        var status = entry.Status;
        var config = entry.Config;
        details.DisplayName = status.DisplayName;
        details.Description = config?.Description ?? string.Empty;
        details.StatusText = StateText(status.State);
        details.IsSuccess = status.State == ServiceState.Running;
        details.IsWarning = status.State is not (ServiceState.Running or ServiceState.Stopped);
        details.ProcessId = status.ProcessId;
        details.IsMissingProgram = entry.Publisher.IsMissing;

        var row = _rows[status.Name];
        details.CanStart = row.CanStart;
        details.CanStop = row.CanStop;
        details.CanPause = row.CanPause;
        details.CanContinue = row.CanContinue;
        details.CanEdit = config is not null && !_busy.Contains(status.Name);
        details.CanUninstall = !_busy.Contains(status.Name);

        var facts = new List<ServiceFact>
        {
            new("Service name", status.Name),
            new("Type", TypeText(status.Type, entry.Publisher.Program)),
            new("Startup type", StartText(config)),
        };

        if (config is not null)
            facts.Add(new ServiceFact("Command line", config.ImagePath));

        var program = config is null ? string.Empty : ServicePublisher.ProgramPath(config.ImagePath);
        if (entry.Publisher.Program.Length > 0 && !string.Equals(entry.Publisher.Program, program, StringComparison.OrdinalIgnoreCase))
            facts.Add(new ServiceFact("Service DLL", entry.Publisher.Program));

        facts.Add(new ServiceFact("Publisher", entry.Publisher.Product is { } product && !string.Equals(product, entry.Publisher.Name, StringComparison.OrdinalIgnoreCase)
            ? $"{entry.Publisher.Name} · {product}"
            : entry.Publisher.Name));

        if (status.State == ServiceState.Stopped && status.ExitCode is not (0 or 1077))
            facts.Add(new ServiceFact("Last exit", ServiceManager.Failure(status.ExitCode)));

        var byName = snapshot.Services.ToDictionary(s => s.Status.Name, StringComparer.OrdinalIgnoreCase);
        if (config is { Dependencies.Count: > 0 })
        {
            facts.Add(new ServiceFact("Depends on", string.Join("\n", config.Dependencies.Select(d => d.StartsWith('+')
                ? $"Group {d[1..]}"
                : byName.TryGetValue(d, out var needed) ? needed.Status.DisplayName : d))));
        }

        var dependents = snapshot.Services
            .Where(s => s.Config?.Dependencies.Any(d => string.Equals(d, status.Name, StringComparison.OrdinalIgnoreCase)) == true)
            .Select(s => s.Status.DisplayName)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (dependents.Count > 0)
            facts.Add(new ServiceFact("Needed by", string.Join("\n", dependents)));

        if (status.ProcessId > 0)
        {
            var sharing = snapshot.Services
                .Where(s => s.Status.ProcessId == status.ProcessId && !ReferenceEquals(s, entry) && s.Status.Name != status.Name)
                .Select(s => s.Status.DisplayName)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (sharing.Count > 0)
            {
                facts.Add(new ServiceFact("Shares its process with", sharing.Count <= 6
                    ? string.Join("\n", sharing)
                    : $"{string.Join("\n", sharing.Take(5))}\nand {Format.Number(sharing.Count - 5)} more"));
            }
        }

        details.Facts = facts;

        var isDriver = IsDriver(status.Type);
        details.LogOnFacts = isDriver ? [] : LogOnFacts(config);
        details.RecoveryFacts = isDriver || config is null ? [] : RecoveryFacts(config.Recovery);
    }

    private static List<ServiceFact> LogOnFacts(ServiceConfig? config)
    {
        var facts = new List<ServiceFact> { new("Account", config is null ? "—" : AccountRaw(config.Account)) };
        if (config is { IsInteractive: true })
            facts.Add(new ServiceFact("Desktop", "May interact with it (session 0 only)"));
        return facts;
    }

    /// <summary>As the Services console's Recovery tab reads, one line per failure; one line when nothing's set.</summary>
    private static List<ServiceFact> RecoveryFacts(ServiceRecovery recovery)
    {
        if (!recovery.Uses(RecoveryAction.Restart) && !recovery.Uses(RecoveryAction.RunCommand) && !recovery.Uses(RecoveryAction.Reboot))
            return [new ServiceFact("On failure", "Take no action")];

        var facts = new List<ServiceFact>
        {
            new("First failure", RecoveryText(recovery.First, recovery)),
            new("Second failure", RecoveryText(recovery.Second, recovery)),
            new("Later failures", RecoveryText(recovery.Subsequent, recovery)),
            new("Count resets", recovery.ResetSeconds == ServiceRecovery.Never
                ? "Never"
                : $"After {DurationField.Describe(recovery.ResetSeconds * 1000UL)} without a failure"),
        };

        if (recovery.Uses(RecoveryAction.RunCommand))
            facts.Add(new ServiceFact("Program", recovery.Command.Length > 0 ? recovery.Command : "—"));
        if (recovery.Uses(RecoveryAction.Reboot) && recovery.RebootMessage.Length > 0)
            facts.Add(new ServiceFact("Restart message", recovery.RebootMessage));
        facts.Add(new ServiceFact("Stops with errors", recovery.OnErrorStops ? "Count as failures" : "Don't count"));
        return facts;
    }

    private static string RecoveryText(RecoveryAction action, ServiceRecovery recovery) => action switch
    {
        RecoveryAction.Restart => $"Restart the service after {DurationField.Describe(recovery.RestartDelay)}",
        RecoveryAction.RunCommand => recovery.CommandDelay == 0
            ? "Run a program"
            : $"Run a program after {DurationField.Describe(recovery.CommandDelay)}",
        RecoveryAction.Reboot => $"Restart the computer after {DurationField.Describe(recovery.RebootDelay)}",
        _ => "Take no action",
    };

    private void OpenProcess()
    {
        if (_details is { ProcessId: > 0 } details)
            _navigator.ShowProcesses(new ProcessQuery(ProcessId: details.ProcessId));
    }

    // ---------- Actions ----------

    private (string Name, string DisplayName, ServiceEntry Entry)? Target() =>
        _selected?.Entry is { } entry ? (entry.Status.Name, entry.Status.DisplayName, entry) : null;

    private void Start()
    {
        if (Target() is not { } target)
            return;

        var (name, display, _) = target;

        Run(name, $"Starting {display}…", () => ServiceManager.Start(name), $"{display} is running.");
    }

    private void Pause()
    {
        if (Target() is not { } target)
            return;

        var (name, display, _) = target;

        Run(name, $"Pausing {display}…", () => ServiceManager.Pause(name), $"{display} is paused.");
    }

    private void Continue()
    {
        if (Target() is not { } target)
            return;

        var (name, display, _) = target;

        Run(name, $"Resuming {display}…", () => ServiceManager.Continue(name), $"{display} is running.");
    }

    private void AskStop() => WithDependents(restart: false);

    private void AskRestart() => WithDependents(restart: true);

    /// <summary>
    /// Windows won't stop a service while services that need it run. Like the Services console, offer
    /// to stop those first (and for a restart, start them again after).
    /// </summary>
    private void WithDependents(bool restart)
    {
        if (Target() is not { } target)
            return;

        var (name, display, _) = target;

        _ = Task.Run(() => ServiceManager.ActiveDependents(name)).ContinueWith(task =>
        {
            if (_selected?.Name != name)
                return;

            var dependents = task.IsCompletedSuccessfully ? task.Result : [];
            if (dependents.Count == 0)
            {
                Stop(name, display, dependents, restart);
                return;
            }

            var list = dependents.Count <= 8
                ? string.Join("\n", dependents.Select(d => $"·  {d.DisplayName}"))
                : $"{string.Join("\n", dependents.Take(7).Select(d => $"·  {d.DisplayName}"))}\n·  and {Format.Number(dependents.Count - 7)} more";
            var verb = restart ? "Restart" : "Stop";
            Ask(
                $"{verb} {display} and {Plural(dependents.Count, "service")} that need it?",
                restart
                    ? $"These need {display}, so they stop first and start again after it:\n{list}"
                    : $"These need {display}, so they stop too:\n{list}",
                null,
                restart ? $"Restart {Format.Number(dependents.Count + 1)}" : $"Stop {Format.Number(dependents.Count + 1)}",
                restart ? "IconRefresh" : "IconStop",
                danger: false,
                () => Stop(name, display, dependents, restart));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Stop(string name, string display, List<(string Name, string DisplayName)> dependents, bool restart)
    {
        Run(name, restart ? $"Restarting {display}…" : $"Stopping {display}…", () =>
        {
            foreach (var dependent in dependents)
            {
                if (ServiceManager.Stop(dependent.Name) is { } failure)
                    return $"{dependent.DisplayName} didn't stop. {failure}";
            }

            if (ServiceManager.Stop(name) is { } stopFailure)
                return stopFailure;

            if (!restart)
                return null;

            if (ServiceManager.Start(name) is { } startFailure)
                return $"It stopped, but didn't start again. {startFailure}";

            // Started in reverse: the ones stopped last (closest to this service) come back first.
            foreach (var dependent in Enumerable.Reverse(dependents))
            {
                if (ServiceManager.Start(dependent.Name) is { } failure)
                    return $"It restarted, but {dependent.DisplayName} didn't start again. {failure}";
            }

            return null;
        }, restart ? $"{display} restarted." : $"{display} is stopped.");
    }

    /// <summary>The Edit button: opens the form at its first page, or closes it.</summary>
    private void Edit() => Edit(null);

    /// <summary>With a page, opens the form there (or turns an open form to it); without one, toggles the form.</summary>
    private void Edit(EditorPage? page)
    {
        if (_details is not { } details || Target() is not { } target)
            return;

        var (name, display, entry) = target;

        if (details.Editor is { } open)
        {
            if (page is { } wanted)
                open.Page = wanted;
            else
                details.Editor = null;
            return;
        }

        if (entry.Config is not { } config)
        {
            details.Message = "Windows won't show this service's settings.";
            details.MessageIsError = true;
            return;
        }

        details.Message = string.Empty;
        details.Editor = new ServiceEditor(display, config, IsDriver(entry.Status.Type), page ?? EditorPage.General,
            change => Run(name, $"Saving {display}…", () => ServiceManager.Change(name, change), "Saved.", ok =>
            {
                if (ok && _details is { } shown && shown.Name == name)
                    shown.Editor = null;
            }),
            () =>
            {
                if (_details is { } shown && shown.Name == name)
                    shown.Editor = null;
            });
    }

    private void AskUninstall()
    {
        if (Target() is not { } target)
            return;

        var (name, display, entry) = target;

        var running = entry.Status.State != ServiceState.Stopped;
        var isWindows = entry.Publisher.Name is Publisher.WindowsName or Publisher.MicrosoftName;
        var what = IsDriver(entry.Status.Type) ? "driver" : "service";
        Ask(
            $"Uninstall {display}?",
            $"Windows removes the {what} \"{name}\": it won't start again{(running ? ", and it leaves this list once it stops" : string.Empty)}. "
            + "The program's files stay on disk, and only reinstalling what it came with brings it back.",
            isWindows
                ? $"This {what} is part of {entry.Publisher.Name}. Removing it can break Windows features, or keep Windows from starting."
                : null,
            "Uninstall",
            "IconTrash",
            danger: true,
            () => Run(name, $"Uninstalling {display}…", () => ServiceManager.Remove(name),
                running ? $"{display} is uninstalled. It leaves the list once it stops." : $"{display} is uninstalled.",
                ok =>
                {
                    // Gone from the list soon: say so above the table, where it stays readable.
                    if (ok && !running)
                        Notice = $"{display} is uninstalled.";
                }));
    }

    private void Ask(string title, string message, string? warning, string confirmText, string icon, bool danger, Action confirm)
    {
        ConfirmIcon = ThemeResource.Geometry(icon);
        ConfirmIsDanger = danger;
        Confirmation = new Confirmation(title, message, warning, confirmText,
            () =>
            {
                Confirmation = null;
                confirm();
            },
            () => Confirmation = null);
    }

    /// <summary>Off the UI thread: the SCM can take seconds, and a start or stop waits for the service.</summary>
    private void Run(string name, string progress, Func<string?> action, string success, Action<bool>? done = null)
    {
        _busy.Add(name);
        Notice = string.Empty;
        if (_details is { } details && details.Name == name)
        {
            details.Message = progress;
            details.MessageIsError = false;
            details.IsBusy = true;
        }

        Render();

        _ = Task.Run(() =>
        {
            string? failure;
            try
            {
                failure = action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Service action failed: {ex}");
                failure = "Windows couldn't do that.";
            }

            _sampler.Refresh(name);
            Dispatcher.UIThread.Post(() =>
            {
                _busy.Remove(name);
                if (_details is { } shown && shown.Name == name)
                {
                    shown.IsBusy = false;
                    shown.Message = failure ?? success;
                    shown.MessageIsError = failure is not null;
                }

                done?.Invoke(failure is null);
                Render();
            });
        });
    }

    // ---------- Text ----------

    internal static bool IsDriver(uint type) => (type & (ServiceManager.KernelDriver | ServiceManager.FileSystemDriver)) != 0;

    private static string StateText(ServiceState state) => state switch
    {
        ServiceState.Running => "Running",
        ServiceState.Stopped => "Stopped",
        ServiceState.Paused => "Paused",
        ServiceState.StartPending => "Starting…",
        ServiceState.StopPending => "Stopping…",
        ServiceState.PausePending => "Pausing…",
        ServiceState.ContinuePending => "Resuming…",
        _ => "Unknown",
    };

    private static string StartText(ServiceConfig? config) => config?.Start switch
    {
        null => "—",
        StartMode.Boot => "Boot",
        StartMode.System => "System",
        StartMode.Automatic => config.HasTriggers ? "Automatic (trigger)" : "Automatic",
        StartMode.AutomaticDelayed => "Automatic (delayed)",
        StartMode.Manual => config.HasTriggers ? "Manual (trigger)" : "Manual",
        StartMode.Disabled => "Disabled",
        _ => "—",
    };

    private static string AccountText(string? account) => account switch
    {
        null or "" => "—",
        _ when account.Equals(ServiceEditor.LocalSystem, StringComparison.OrdinalIgnoreCase) => "Local System",
        _ when account.Equals(ServiceEditor.LocalService, StringComparison.OrdinalIgnoreCase) => "Local Service",
        _ when account.Equals(ServiceEditor.NetworkService, StringComparison.OrdinalIgnoreCase) => "Network Service",
        _ => account,
    };

    /// <summary>The friendly name, with the account as Windows has it when they differ.</summary>
    private static string AccountRaw(string account)
    {
        var text = AccountText(account);
        return text == account || account.Length == 0 || account.Equals(ServiceEditor.LocalSystem, StringComparison.OrdinalIgnoreCase)
            ? text
            : $"{text} ({account})";
    }

    private static string TypeText(uint type, string program)
    {
        if ((type & ServiceManager.KernelDriver) != 0)
            return "Kernel driver";
        if ((type & ServiceManager.FileSystemDriver) != 0)
            return "File system driver";

        var perUser = (type & ServiceManager.UserService) != 0
            ? (type & ServiceManager.UserServiceInstance) != 0 ? "Per-user service, " : "Per-user service template, "
            : string.Empty;
        var host = (type & ServiceManager.Win32ShareProcess) != 0
            ? program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "shares svchost.exe" : "shares its process"
            : "own process";
        return perUser.Length > 0 ? perUser + host : char.ToUpperInvariant(host[0]) + host[1..];
    }

    private static string Plural(int count, string noun) => $"{Format.Number(count)} {noun}{(count == 1 ? string.Empty : "s")}";

    /// <summary>Moves, inserts and removes so <paramref name="items"/> matches <paramref name="wanted"/>, keeping rows that stay.</summary>
    private static void Reconcile(ObservableCollection<object> items, List<object> wanted)
    {
        var keep = new HashSet<object>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
                items.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], wanted[i]))
                continue;

            var at = IndexOf(items, wanted[i], i + 1);
            if (at >= 0)
                items.Move(at, i);
            else
                items.Insert(i, wanted[i]);
        }
    }

    private static int IndexOf(ObservableCollection<object> items, object item, int from)
    {
        for (var i = from; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
                return i;
        }

        return -1;
    }
}
