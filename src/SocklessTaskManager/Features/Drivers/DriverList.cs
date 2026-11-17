using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// The Drivers page's state, in five tabs: devices and their drivers, loaded kernel modules, driver
/// updates from Windows Update, a history of device and driver changes, and crashes. Scans when first
/// shown and on Refresh; asks Windows Update when first shown and on "Check again"; follows device
/// changes through <see cref="DriverMonitor"/>, which costs nothing until a device changes. Nothing
/// runs per tick.
/// </summary>
public sealed class DriverList : ObservableObject
{
    private const int TabDevices = 0;
    private const int TabModules = 1;
    private const int TabUpdates = 2;
    private const int TabChanges = 3;
    private const int TabCrashes = 4;

    private const int DevicesConnected = 0;
    private const int DevicesProblems = 1;
    private const int DevicesThirdParty = 2;
    private const int DevicesAll = 3;

    private const int ModulesAll = 0;
    private const int ModulesThirdParty = 1;
    private const int ModulesLook = 2;

    private const int ByClass = 0;
    private const int BySource = 1;
    private const int Flat = 2;

    private const int ChangesAll = 0;
    private const int ChangesProblems = 1;
    private const int ChangesLive = 2;

    /// <summary>A live change and the log's record of the same thing are a couple of seconds apart.</summary>
    private static readonly TimeSpan SameChange = TimeSpan.FromMinutes(2);

    private readonly DriverMonitor _monitor;
    private readonly DriverScanner _scanner = new();

    // UI thread.
    private readonly Dictionary<string, DeviceGroupRow> _groups = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _collapsed = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly HashSet<string> _installing = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DeviceInfo> _devicesById = new(StringComparer.OrdinalIgnoreCase);
    private List<DeviceRow> _deviceRows = [];
    private List<ModuleRow> _moduleRows = [];
    private List<UpdateRow> _updateRows = [];
    private List<ChangeRow> _changeRows = [];
    private List<CrashRow> _crashRows = [];
    private DriverSnapshot? _snapshot;
    private List<DriverUpdate>? _updates;
    private string? _updateFailure;
    private List<DriverChange>? _history;
    private CrashSnapshot? _crashes;
    private int _tab = TabDevices;
    private int _deviceShow = DevicesConnected;
    private int _moduleShow = ModulesAll;
    private int _changeShow = ChangesAll;
    private int _layout = ByClass;
    private IReadOnlyList<ToolLink> _tools = [];
    private string _searchText = string.Empty;
    private DriverColumn _deviceSort;
    private DriverColumn _moduleSort;
    private string _summary = "Reading devices and drivers…";
    private string _notice = string.Empty;
    private bool _noticeIsError;
    private DriverRow? _selected;
    private int _selectedTab;
    private DriverDetails? _details;
    private Confirmation? _confirmation;
    private Geometry? _confirmIcon;
    private bool _isLoaded;
    private bool _isScanning;
    private bool _rescan;
    private bool _isCheckingUpdates;
    private bool _isReadingHistory;
    private bool _isReadingCrashes;
    private bool _isInstallingAll;
    private bool _stopInstallingAll;
    private bool _wasShown;

    internal DriverList(DriverMonitor monitor)
    {
        _monitor = monitor;
        _monitor.Changed += () => Dispatcher.UIThread.Post(OnDevicesChanged);

        DeviceNameColumn = new DriverColumn("Device", DriverSortKey.Name, SortDevices);
        DeviceStatusColumn = new DriverColumn("Status", DriverSortKey.Status, SortDevices);
        DeviceDriverColumn = new DriverColumn("Driver", DriverSortKey.Driver, SortDevices);
        DeviceSourceColumn = new DriverColumn("Source", DriverSortKey.Source, SortDevices);
        DeviceVersionColumn = new DriverColumn("Version", DriverSortKey.Version, SortDevices);
        DeviceDateColumn = new DriverColumn("Date", DriverSortKey.Date, SortDevices);
        _deviceSort = DeviceNameColumn;
        DeviceNameColumn.IsActive = true;

        ModuleNameColumn = new DriverColumn("Module", DriverSortKey.Name, SortModules);
        ModuleSignatureColumn = new DriverColumn("Signed by", DriverSortKey.Signature, SortModules);
        ModuleDescriptionColumn = new DriverColumn("Driver", DriverSortKey.Description, SortModules);
        ModuleSourceColumn = new DriverColumn("Source", DriverSortKey.Source, SortModules);
        ModuleVersionColumn = new DriverColumn("Version", DriverSortKey.Version, SortModules);
        _moduleSort = ModuleNameColumn;
        ModuleNameColumn.IsActive = true;

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        RefreshCommand = new RelayCommand(Refresh);
        CheckUpdatesCommand = new RelayCommand(CheckUpdates);
        InstallAllCommand = new RelayCommand(AskInstallAll);
        StopInstallAllCommand = new RelayCommand(() => _stopInstallingAll = true);
        InstallInfCommand = new RelayCommand(() => InfRequested?.Invoke(false));
        DismissNoticeCommand = new RelayCommand(() => Notice = string.Empty);
    }

    /// <summary>The view puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The view asks for an INF file, then calls <see cref="InstallInf"/>. True: for the selected device.</summary>
    public event Action<bool>? InfRequested;

    /// <summary>Set by the view. The first time the page is shown, it reads everything.</summary>
    public bool IsShown
    {
        get => _wasShown;
        set
        {
            if (value && !_wasShown)
            {
                _wasShown = true;
                Refresh();
                CheckUpdates();
            }
        }
    }

    public ObservableCollection<object> DeviceItems { get; } = [];

    public ObservableCollection<object> ModuleItems { get; } = [];

    public ObservableCollection<object> UpdateItems { get; } = [];

    public ObservableCollection<object> ChangeItems { get; } = [];

    public ObservableCollection<object> CrashItems { get; } = [];

    public DriverColumn DeviceNameColumn { get; }

    public DriverColumn DeviceStatusColumn { get; }

    public DriverColumn DeviceDriverColumn { get; }

    public DriverColumn DeviceSourceColumn { get; }

    public DriverColumn DeviceVersionColumn { get; }

    public DriverColumn DeviceDateColumn { get; }

    public DriverColumn ModuleNameColumn { get; }

    public DriverColumn ModuleSignatureColumn { get; }

    public DriverColumn ModuleDescriptionColumn { get; }

    public DriverColumn ModuleSourceColumn { get; }

    public DriverColumn ModuleVersionColumn { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand CheckUpdatesCommand { get; }

    public ICommand InstallAllCommand { get; }

    public ICommand StopInstallAllCommand { get; }

    public ICommand InstallInfCommand { get; }

    public ICommand DismissNoticeCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public bool IsElevated => Elevation.IsElevated;

    /// <summary>Without elevation, drivers can't be installed and crash dumps can't be read.</summary>
    public bool ShowElevationHint => !Elevation.IsElevated;

    // ---------- Tabs and filters ----------

    public bool IsDevicesTab
    {
        get => _tab == TabDevices;
        set => SetTab(value, TabDevices);
    }

    public bool IsModulesTab
    {
        get => _tab == TabModules;
        set => SetTab(value, TabModules);
    }

    public bool IsUpdatesTab
    {
        get => _tab == TabUpdates;
        set => SetTab(value, TabUpdates);
    }

    public bool IsChangesTab
    {
        get => _tab == TabChanges;
        set => SetTab(value, TabChanges);
    }

    public bool IsCrashesTab
    {
        get => _tab == TabCrashes;
        set => SetTab(value, TabCrashes);
    }

    public string UpdatesTabText => _updates is { Count: > 0 } updates ? $"Updates ({Format.Number(updates.Count)})" : "Updates";

    public bool IsDevicesConnected
    {
        get => _deviceShow == DevicesConnected;
        set => SetDeviceShow(value, DevicesConnected);
    }

    public bool IsDevicesProblems
    {
        get => _deviceShow == DevicesProblems;
        set => SetDeviceShow(value, DevicesProblems);
    }

    public bool IsDevicesThirdParty
    {
        get => _deviceShow == DevicesThirdParty;
        set => SetDeviceShow(value, DevicesThirdParty);
    }

    public bool IsDevicesAll
    {
        get => _deviceShow == DevicesAll;
        set => SetDeviceShow(value, DevicesAll);
    }

    public bool IsModulesAll
    {
        get => _moduleShow == ModulesAll;
        set => SetModuleShow(value, ModulesAll);
    }

    public bool IsModulesThirdParty
    {
        get => _moduleShow == ModulesThirdParty;
        set => SetModuleShow(value, ModulesThirdParty);
    }

    public bool IsModulesLook
    {
        get => _moduleShow == ModulesLook;
        set => SetModuleShow(value, ModulesLook);
    }

    public bool IsChangesAll
    {
        get => _changeShow == ChangesAll;
        set => SetChangeShow(value, ChangesAll);
    }

    public bool IsChangesProblems
    {
        get => _changeShow == ChangesProblems;
        set => SetChangeShow(value, ChangesProblems);
    }

    public bool IsChangesLive
    {
        get => _changeShow == ChangesLive;
        set => SetChangeShow(value, ChangesLive);
    }

    /// <summary>Devices under headings (their class or their driver's source), rather than one list.</summary>
    public bool IsGrouped => _layout != Flat;

    public bool IsByClass
    {
        get => _layout == ByClass;
        set => SetLayout(value, ByClass);
    }

    public bool IsBySource
    {
        get => _layout == BySource;
        set => SetLayout(value, BySource);
    }

    public bool IsFlat
    {
        get => _layout == Flat;
        set => SetLayout(value, Flat);
    }

    /// <summary>Vendors' update tools on this computer (NVIDIA App, Intel Driver &amp; Support Assistant…).</summary>
    public IReadOnlyList<ToolLink> Tools
    {
        get => _tools;
        private set
        {
            if (Set(ref _tools, value))
            {
                OnPropertyChanged(nameof(HasTools));
                OnPropertyChanged(nameof(ShowToolsHint));
            }
        }
    }

    public bool HasTools => _tools.Count > 0;

    public bool ShowToolsHint => _tab == TabUpdates && _tools.Count > 0;

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

    public string SearchHint => _tab switch
    {
        TabModules => "Search by name, file, service or signer",
        TabUpdates => "Search by update, device or provider",
        TabChanges => "Search by device or what happened",
        TabCrashes => "Search by driver or stop code",
        _ => "Search by name, provider, hardware id or class",
    };

    // ---------- Status ----------

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isScanning;

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set
        {
            if (Set(ref _isCheckingUpdates, value))
            {
                OnPropertyChanged(nameof(CanCheckUpdates));
                OnPropertyChanged(nameof(CanInstallAll));
            }
        }
    }

    public bool CanCheckUpdates => !_isCheckingUpdates && !_isInstallingAll && _installing.Count == 0;

    public bool CanInstallAll => Elevation.IsElevated && !_isCheckingUpdates && !_isInstallingAll && _installing.Count == 0 && _updates is { Count: > 0 };

    public bool IsInstallingAll
    {
        get => _isInstallingAll;
        private set
        {
            if (Set(ref _isInstallingAll, value))
            {
                OnPropertyChanged(nameof(IsNotInstallingAll));
                OnPropertyChanged(nameof(CanInstallAll));
                OnPropertyChanged(nameof(CanCheckUpdates));
            }
        }
    }

    public bool IsNotInstallingAll => !_isInstallingAll;

    /// <summary>A result that outlives the details pane: an INF installed from the toolbar, a restart to finish.</summary>
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

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => Set(ref _noticeIsError, value);
    }

    /// <summary>Nothing listed yet: the first scan is under way.</summary>
    public bool IsLoading => !_isLoaded;

    public bool IsDevicesEmpty => _isLoaded && DeviceItems.Count == 0;

    public bool IsModulesEmpty => _isLoaded && ModuleItems.Count == 0;

    public bool IsUpdatesEmpty => UpdateItems.Count == 0;

    public bool IsChangesEmpty => _history is not null && ChangeItems.Count == 0;

    public bool IsCrashesEmpty => _crashes is not null && CrashItems.Count == 0;

    public string DevicesEmptyTitle => HasSearch ? "Nothing matches" : _deviceShow == DevicesProblems ? "No problems" : "Nothing to show";

    public string DevicesEmptyHint => HasSearch ? "Try another search, or show every device."
        : _deviceShow == DevicesProblems ? "Every connected device is working." : "Try showing every device.";

    public string ModulesEmptyTitle => HasSearch ? "Nothing matches" : _moduleShow == ModulesLook ? "Nothing to check" : "Nothing to show";

    public string ModulesEmptyHint => HasSearch ? "Try another search, or show every module."
        : _moduleShow == ModulesLook ? "Every loaded module is signed and where it should be." : "Try showing every module.";

    public string UpdatesEmptyTitle => _isCheckingUpdates ? "Asking Windows Update…"
        : _updateFailure is not null ? "Couldn't check for updates"
        : HasSearch && _updates is { Count: > 0 } ? "Nothing matches"
        : "No driver updates";

    public string UpdatesEmptyHint => _isCheckingUpdates ? "This can take a minute."
        : _updateFailure ?? (HasSearch && _updates is { Count: > 0 } ? "Try another search."
            : "Windows Update has nothing newer for this computer's devices.");

    /// <summary>The last check failed while an earlier answer is still listed: say so above it (an empty list says so itself).</summary>
    public bool UpdatesFailed => _tab == TabUpdates && !_isCheckingUpdates && _updateFailure is not null && UpdateItems.Count > 0;

    public string ChangesEmptyTitle => HasSearch ? "Nothing matches" : "No changes";

    public string ChangesEmptyHint => HasSearch ? "Try another search." : _changeShow switch
    {
        ChangesLive => "No device has changed since the app started.",
        ChangesProblems => "No device problems or driver failures logged in the last 90 days.",
        _ => "Nothing logged in the last 90 days.",
    };

    public string CrashesEmptyTitle => HasSearch ? "Nothing matches" : "No crashes";

    public string CrashesEmptyHint => HasSearch ? "Try another search."
        : "No stop errors, unexpected restarts, display driver resets or recovered hangs in the last year.";

    // ---------- Details and dialog ----------

    public DriverDetails? Details
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

    public Geometry? ConfirmIcon
    {
        get => _confirmIcon;
        private set => Set(ref _confirmIcon, value);
    }

    /// <summary>Esc: the dialog first, then the details pane. False when there was nothing to close.</summary>
    public bool Dismiss()
    {
        if (_confirmation is not null)
        {
            Confirmation = null;
            return true;
        }

        if (_details is null)
            return false;

        Select(null);
        return true;
    }

    // ---------- Reading ----------

    /// <summary>Devices, modules and files again, then the history and crashes.</summary>
    public void Refresh()
    {
        ScanDevices();
        ReadHistory();
        ReadCrashes();
    }

    private void ScanDevices()
    {
        if (_isScanning)
        {
            _rescan = true;
            return;
        }

        IsScanning = true;
        UpdateSummary();
        _ = Task.Run(() =>
        {
            try
            {
                _scanner.Scan(snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Driver scan failed: {ex}");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsScanning = false;
                    UpdateSummary();
                    if (_rescan)
                    {
                        _rescan = false;
                        ScanDevices();
                    }
                });
            }
        });
    }

    private void ReadHistory()
    {
        if (_isReadingHistory)
            return;

        _isReadingHistory = true;
        _ = Task.Run(() =>
        {
            List<DriverChange> history;
            try
            {
                history = DriverHistory.Read();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Driver history failed: {ex}");
                history = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                _isReadingHistory = false;
                _history = history;
                BuildChangeRows();
                RenderChanges();
            });
        });
    }

    private void ReadCrashes()
    {
        if (_isReadingCrashes)
            return;

        _isReadingCrashes = true;
        _ = Task.Run(() =>
        {
            CrashSnapshot crashes;
            try
            {
                crashes = CrashHistory.Read();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Crash history failed: {ex}");
                crashes = new CrashSnapshot([], false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _isReadingCrashes = false;
                _crashes = crashes;
                _crashRows = [.. crashes.Crashes.Select(c => new CrashRow(c, Select))];
                RenderCrashes();
            });
        });
    }

    private void CheckUpdates()
    {
        if (_isCheckingUpdates)
            return;

        IsCheckingUpdates = true;
        _updateFailure = null;
        RenderUpdates();
        UpdateSummary();
        _ = Task.Run(() =>
        {
            List<DriverUpdate> updates;
            string? failure;
            try
            {
                (updates, failure) = WindowsUpdate.Search();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Windows Update search failed: {ex}");
                (updates, failure) = ([], "Windows Update couldn't be asked.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                _updates = failure is null ? updates : _updates;
                _updateFailure = failure;
                IsCheckingUpdates = false;
                OnPropertyChanged(nameof(UpdatesTabText));
                OnPropertyChanged(nameof(CanInstallAll));
                OnPropertyChanged(nameof(UpdatesFailed));
                BuildDeviceRows();
                BuildUpdateRows();
                Render();
                UpdateSummary();
            });
        });
    }

    private void Apply(DriverSnapshot snapshot)
    {
        _snapshot = snapshot;
        _devicesById = snapshot.Devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
        if (!_isLoaded)
        {
            _isLoaded = true;
            OnPropertyChanged(nameof(IsLoading));
        }

        BuildDeviceRows();
        _moduleRows = [.. snapshot.Modules.Select(m => new ModuleRow(m, snapshot.Origins.GetValueOrDefault(m.Path), Select))];
        if (!snapshot.Tools.Select(t => t.Name).SequenceEqual(_tools.Select(t => t.Name)))
            Tools = [.. snapshot.Tools.Select(t => new ToolLink(t.Name, () => OpenTool(t)))];
        BuildUpdateRows();
        BuildChangeRows();
        Render();
        UpdateSummary();
    }

    /// <summary>A device changed (the monitor read it again): show it, and what the log says about it.</summary>
    private void OnDevicesChanged()
    {
        if (!_wasShown)
            return;

        ScanDevices();
        ReadHistory();
    }

    // ---------- Rows ----------

    private void BuildDeviceRows()
    {
        if (_snapshot is not { } snapshot)
            return;

        _deviceRows = [.. snapshot.Devices.Select(d => new DeviceRow(d, DeviceStatus(d), DeviceDriver(d), snapshot.Origins.GetValueOrDefault(d.InstanceId), UpdateFor(d), Select))];
    }

    private void BuildUpdateRows() =>
        _updateRows = [.. (_updates ?? []).Select(u => new UpdateRow(u, DeviceFor(u), Select) { Status = UpdateStatus(u) })];

    private void BuildChangeRows()
    {
        var log = _history ?? [];
        var live = _monitor.Changes();

        // The log records installs and problems too: keep the live one only when the log has no match.
        var merged = new List<DriverChange>(log.Count + live.Count);
        merged.AddRange(log);
        foreach (var change in live)
        {
            var logged = change.Kind is ChangeKind.DriverChanged or ChangeKind.Problem or ChangeKind.Removed
                && log.Any(l => string.Equals(l.InstanceId, change.InstanceId, StringComparison.OrdinalIgnoreCase)
                    && (l.Time - change.Time).Duration() < SameChange
                    && (l.Kind == change.Kind || (change.Kind == ChangeKind.DriverChanged && l.Kind == ChangeKind.Installed)));
            if (!logged)
                merged.Add(change);
        }

        merged.Sort((a, b) => b.Time.CompareTo(a.Time));
        _changeRows = [.. merged.Select(c => new ChangeRow(c, SubjectOf(c), Select))];
    }

    private string SubjectOf(DriverChange change) =>
        change.InstanceId is { } id && _devicesById.TryGetValue(id, out var device) ? device.Name : change.Subject;

    private static Badge DeviceStatus(DeviceInfo device) =>
        !device.IsPresent ? new Badge("Not connected", BadgeKind.Neutral)
        : device.Problem == 22 ? new Badge("Turned off", BadgeKind.Neutral)
        : device.Problem != 0 ? new Badge("Problem", BadgeKind.Warning)
        : new Badge("Working", BadgeKind.Success);

    private static string DeviceDriver(DeviceInfo device) =>
        device.Provider ?? (device.HasDriver ? "—" : "No driver");

    private DriverUpdate? UpdateFor(DeviceInfo device)
    {
        if (_updates is not { Count: > 0 } updates)
            return null;

        return updates.FirstOrDefault(u => u.HardwareId is { } id
            && (device.HardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase) || device.CompatibleIds.Contains(id, StringComparer.OrdinalIgnoreCase)));
    }

    private DeviceInfo? DeviceFor(DriverUpdate update)
    {
        if (update.HardwareId is not { } id || _snapshot is not { } snapshot)
            return null;

        return snapshot.Devices
            .Where(d => d.HardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase) || d.CompatibleIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(d => d.IsPresent)
            .FirstOrDefault();
    }

    private Badge UpdateStatus(DriverUpdate update) =>
        _installing.Contains(update.UpdateId) ? new Badge("Installing…", BadgeKind.Info) : UpdateRow.Kind(update);

    // ---------- Rendering ----------

    private void Render()
    {
        RenderDevices();
        RenderModules();
        RenderUpdates();
        RenderChanges();
        RenderCrashes();
    }

    private void RenderDevices()
    {
        var shown = _deviceRows.Where(MatchesDevice).ToList();
        shown.Sort(CompareDevices);

        var wanted = new List<object>(shown.Count + 40);
        if (_layout != Flat)
        {
            var searching = HasSearch;
            var present = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var groups = _layout == BySource
                ? shown.GroupBy(SourceGroup, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => SourceRank(g.First()))
                    .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                : shown.GroupBy(r => r.Device.ClassTitle, StringComparer.CurrentCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
            foreach (var group in groups)
            {
                present.Add(group.Key);
                if (!_groups.TryGetValue(group.Key, out var header))
                    _groups[group.Key] = header = new DeviceGroupRow(group.Key, !_collapsed.Contains(group.Key), ToggleGroup);

                var count = group.Count();
                var problems = group.Count(r => r.Device.IsPresent && r.Device.Problem is not (0 or 22));
                var updates = group.Count(r => r.HasUpdate);
                header.CountText = string.Join(" · ", new[]
                {
                    Plural(count, "device", "devices"),
                    problems > 0 ? $"{Format.Number(problems)} with problems" : null,
                    updates > 0 ? Plural(updates, "update", "updates") : null,
                }.OfType<string>());
                header.HasProblems = problems > 0;
                header.IsExpanded = searching || !_collapsed.Contains(group.Key);
                wanted.Add(header);
                if (header.IsExpanded)
                    wanted.AddRange(group);
            }

            foreach (var gone in _groups.Keys.Where(k => !present.Contains(k)).ToList())
                _groups.Remove(gone);
        }
        else
        {
            wanted.AddRange(shown);
        }

        Reconcile(DeviceItems, wanted);
        OnPropertyChanged(nameof(IsDevicesEmpty));
        OnPropertyChanged(nameof(DevicesEmptyTitle));
        OnPropertyChanged(nameof(DevicesEmptyHint));
        RefreshSelection();
    }

    private void RenderModules()
    {
        var shown = _moduleRows.Where(MatchesModule).ToList();
        shown.Sort(CompareModules);
        Reconcile(ModuleItems, [.. shown]);
        OnPropertyChanged(nameof(IsModulesEmpty));
        OnPropertyChanged(nameof(ModulesEmptyTitle));
        OnPropertyChanged(nameof(ModulesEmptyHint));
        RefreshSelection();
    }

    private void RenderUpdates()
    {
        var shown = _updateRows.Where(r => Search(r.Title, r.DeviceText, r.ProviderText, r.Update.HardwareId, r.Update.DriverClass))
            .OrderBy(r => r.Update.IsOptional)
            .ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Reconcile(UpdateItems, [.. shown]);
        OnPropertyChanged(nameof(IsUpdatesEmpty));
        OnPropertyChanged(nameof(UpdatesEmptyTitle));
        OnPropertyChanged(nameof(UpdatesEmptyHint));
        OnPropertyChanged(nameof(UpdatesFailed));
        RefreshSelection();
    }

    private void RenderChanges()
    {
        var shown = _changeRows.Where(r => _changeShow switch
            {
                ChangesProblems => r.Change.Kind is ChangeKind.Problem or ChangeKind.FailedToLoad or ChangeKind.NeedsInstall,
                ChangesLive => r.Change.IsLive,
                _ => true,
            } && Search(r.Subject, r.Detail, r.Kind.Text, r.Change.InstanceId))
            .ToList();
        Reconcile(ChangeItems, [.. shown]);
        OnPropertyChanged(nameof(IsChangesEmpty));
        OnPropertyChanged(nameof(ChangesEmptyTitle));
        OnPropertyChanged(nameof(ChangesEmptyHint));
        RefreshSelection();
    }

    private void RenderCrashes()
    {
        var shown = _crashRows.Where(r => Search(r.Kind.Text, r.DriverName, r.CodeText, r.Crash.Dump)).ToList();
        Reconcile(CrashItems, [.. shown]);
        OnPropertyChanged(nameof(IsCrashesEmpty));
        OnPropertyChanged(nameof(CrashesEmptyTitle));
        OnPropertyChanged(nameof(CrashesEmptyHint));
        RefreshSelection();
    }

    /// <summary>The heading a device sits under by source: where its driver came from.</summary>
    private static string SourceGroup(DeviceRow row) => row.Origin is { } origin
        ? origin.Kind switch
        {
            OriginKind.Windows => "Shipped with Windows",
            OriginKind.WindowsUpdate => "Windows Update",
            OriginKind.Unknown => "Source not recorded",
            OriginKind.Program when origin.Package?.Publisher is { } publisher => $"Packages from {publisher}",
            _ => origin.Title,
        }
        : row.Device.HasDriver ? "Source not recorded" : "No driver";

    /// <summary>
    /// What's worth checking first: Windows Update, programs and apps, installers and folders, the
    /// unrecorded; then Windows' own (most devices, kept current with Windows), and no driver last.
    /// </summary>
    private static int SourceRank(DeviceRow row) => row.Origin is { } origin
        ? origin.Kind == OriginKind.Windows ? 100 : (int)origin.Kind
        : int.MaxValue;

    private bool MatchesDevice(DeviceRow row)
    {
        var d = row.Device;
        var shown = _deviceShow switch
        {
            DevicesConnected => d.IsPresent && (!d.IsHidden || d.Problem != 0),
            DevicesProblems => d.IsPresent && d.Problem != 0,
            DevicesThirdParty => d.IsThirdParty,
            _ => true,
        };

        return shown && Search(d.Name, d.InstanceId, d.ClassTitle, d.Provider, d.Version, d.Manufacturer, d.Inf, d.Service, row.SourceText)
            || (shown && HasSearch && d.HardwareIds.Any(id => Search(id)));
    }

    private bool MatchesModule(ModuleRow row)
    {
        var m = row.Module;
        var shown = _moduleShow switch
        {
            ModulesThirdParty => !m.File.IsWindows,
            ModulesLook => m.File.NeedsLook,
            _ => true,
        };

        return shown && Search(m.Name, m.Path, m.Service?.Name, m.Service?.DisplayName, m.File.Description, m.File.Company, m.File.Signature.Signer, row.SourceText);
    }

    /// <summary>True when there's no search, or any of the texts contains it.</summary>
    private bool Search(params string?[] texts)
    {
        if (_searchText.Length == 0)
            return true;

        var search = _searchText.Trim();
        return texts.Any(t => t is not null && t.Contains(search, StringComparison.CurrentCultureIgnoreCase));
    }

    private int CompareDevices(DeviceRow a, DeviceRow b)
    {
        var result = _deviceSort.Key switch
        {
            DriverSortKey.Status => Rank(a).CompareTo(Rank(b)),
            DriverSortKey.Driver => string.Compare(a.DriverText, b.DriverText, StringComparison.CurrentCultureIgnoreCase),
            DriverSortKey.Source => string.Compare(a.SourceText, b.SourceText, StringComparison.CurrentCultureIgnoreCase),
            DriverSortKey.Version => CompareVersions(a.Device.Version, b.Device.Version),
            DriverSortKey.Date => Nullable.Compare(a.Device.Date, b.Device.Date),
            _ => 0,
        };

        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        if (result == 0)
            result = string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        return _deviceSort.IsDescending ? -result : result;

        // Problems first, then turned off, working, not connected.
        static int Rank(DeviceRow row) => !row.Device.IsPresent ? 3 : row.Device.Problem == 22 ? 1 : row.Device.Problem != 0 ? 0 : 2;
    }

    private int CompareModules(ModuleRow a, ModuleRow b)
    {
        var result = _moduleSort.Key switch
        {
            DriverSortKey.Signature => a.Look.HasText != b.Look.HasText
                ? (a.Look.HasText ? -1 : 1)
                : string.Compare(a.SignatureText, b.SignatureText, StringComparison.CurrentCultureIgnoreCase),
            DriverSortKey.Description => string.Compare(a.DescriptionText, b.DescriptionText, StringComparison.CurrentCultureIgnoreCase),
            DriverSortKey.Source => string.Compare(a.SourceText, b.SourceText, StringComparison.CurrentCultureIgnoreCase),
            DriverSortKey.Version => CompareVersions(a.Module.File.Version, b.Module.File.Version),
            _ => 0,
        };

        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        return _moduleSort.IsDescending ? -result : result;
    }

    /// <summary>"32.0.101.8976" by its numbers; anything unparseable after those that are.</summary>
    private static int CompareVersions(string? a, string? b)
    {
        var va = ParseVersion(a);
        var vb = ParseVersion(b);
        return va is null || vb is null
            ? (va is null).CompareTo(vb is null) is var n && n != 0 ? n : string.Compare(a, b, StringComparison.OrdinalIgnoreCase)
            : va.CompareTo(vb);
    }

    private static Version? ParseVersion(string? text)
    {
        if (text is null)
            return null;

        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
            end++;
        return Version.TryParse(text[..end].Trim('.'), out var version) ? version : null;
    }

    private void UpdateSummary()
    {
        if (_snapshot is not { } snapshot)
        {
            Summary = "Reading devices and drivers…";
            return;
        }

        var present = snapshot.Devices.Count(d => d.IsPresent && (!d.IsHidden || d.Problem != 0));
        var problems = snapshot.Devices.Count(d => d.IsPresent && d.Problem is not (0 or 22));
        var parts = new List<string>
        {
            Plural(present, "device", "devices"),
            problems > 0 ? $"{Format.Number(problems)} with problems" : "no problems",
            Plural(snapshot.Modules.Count, "kernel module", "kernel modules"),
        };

        if (_isCheckingUpdates)
            parts.Add("asking Windows Update…");
        else if (_updates is { } updates)
            parts.Add(updates.Count == 0 ? "no driver updates" : Plural(updates.Count, "driver update", "driver updates"));

        if (snapshot.IsChecking)
            parts.Add("checking signatures…");
        else if (_isScanning)
            parts.Add("reading…");
        Summary = string.Join(" · ", parts);
    }

    // ---------- Tabs, filters, sorting ----------

    private void SetTab(bool isChecked, int tab)
    {
        if (!isChecked || _tab == tab)
            return;

        _tab = tab;
        OnPropertyChanged(nameof(IsDevicesTab));
        OnPropertyChanged(nameof(IsModulesTab));
        OnPropertyChanged(nameof(IsUpdatesTab));
        OnPropertyChanged(nameof(IsChangesTab));
        OnPropertyChanged(nameof(IsCrashesTab));
        OnPropertyChanged(nameof(SearchHint));
        OnPropertyChanged(nameof(ShowToolsHint));
        OnPropertyChanged(nameof(UpdatesFailed));
        if (_selectedTab != tab)
            Select(null);
    }

    private void SetLayout(bool isChecked, int layout)
    {
        if (!isChecked || _layout == layout)
            return;

        _layout = layout;
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(IsByClass));
        OnPropertyChanged(nameof(IsBySource));
        OnPropertyChanged(nameof(IsFlat));
        RenderDevices();
    }

    private void SetDeviceShow(bool isChecked, int show)
    {
        if (!isChecked || _deviceShow == show)
            return;

        _deviceShow = show;
        OnPropertyChanged(nameof(IsDevicesConnected));
        OnPropertyChanged(nameof(IsDevicesProblems));
        OnPropertyChanged(nameof(IsDevicesThirdParty));
        OnPropertyChanged(nameof(IsDevicesAll));
        RenderDevices();
    }

    private void SetModuleShow(bool isChecked, int show)
    {
        if (!isChecked || _moduleShow == show)
            return;

        _moduleShow = show;
        OnPropertyChanged(nameof(IsModulesAll));
        OnPropertyChanged(nameof(IsModulesThirdParty));
        OnPropertyChanged(nameof(IsModulesLook));
        RenderModules();
    }

    private void SetChangeShow(bool isChecked, int show)
    {
        if (!isChecked || _changeShow == show)
            return;

        _changeShow = show;
        OnPropertyChanged(nameof(IsChangesAll));
        OnPropertyChanged(nameof(IsChangesProblems));
        OnPropertyChanged(nameof(IsChangesLive));
        RenderChanges();
    }

    private void SortDevices(DriverColumn column)
    {
        Sort(ref _deviceSort, column);
        RenderDevices();
    }

    private void SortModules(DriverColumn column)
    {
        Sort(ref _moduleSort, column);
        RenderModules();
    }

    private static void Sort(ref DriverColumn current, DriverColumn column)
    {
        if (ReferenceEquals(column, current))
        {
            column.IsDescending = !column.IsDescending;
            return;
        }

        current.IsActive = false;
        current = column;
        column.IsActive = true;
        column.IsDescending = false;
    }

    private void ToggleGroup(DeviceGroupRow group)
    {
        if (HasSearch)
            return;

        if (!_collapsed.Remove(group.Name))
            _collapsed.Add(group.Name);
        RenderDevices();
    }

    // ---------- Selection and details ----------

    private void Select(DriverRow? row)
    {
        if (row is not null && ReferenceEquals(row, _selected))
            row = null;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = row;
        if (row is null)
        {
            Details = null;
            return;
        }

        row.IsSelected = true;
        _selectedTab = _tab;
        var details = new DriverDetails($"{_tab}|{row.Id}", new DriverDetailActions(
            () => Select(null), InstallSelectedUpdate, () => InfRequested?.Invoke(true), OpenLocation, ShowDevice, ShowModule, OpenDeviceManager, CopySelected));
        Fill(details, row);
        Details = details;
    }

    /// <summary>After a render: the selected row is rebuilt from new data, so find it again by id and refill the pane.</summary>
    private void RefreshSelection()
    {
        if (_selected is null || _details is not { } details)
            return;

        DriverRow? current = _selectedTab switch
        {
            TabDevices => _deviceRows.FirstOrDefault(r => r.Id == _selected.Id),
            TabModules => _moduleRows.FirstOrDefault(r => r.Id == _selected.Id),
            TabUpdates => _updateRows.FirstOrDefault(r => r.Id == _selected.Id),
            TabChanges => _changeRows.FirstOrDefault(r => r.Id == _selected.Id),
            _ => _crashRows.FirstOrDefault(r => r.Id == _selected.Id),
        };

        if (current is null)
        {
            // Gone (an update installed, a device removed): keep the pane's last words, drop the actions.
            if (!details.IsBusy)
                Select(null);
            return;
        }

        if (!ReferenceEquals(current, _selected))
        {
            _selected.IsSelected = false;
            _selected = current;
            current.IsSelected = true;
        }

        Fill(details, current);
    }

    private void Fill(DriverDetails details, DriverRow row)
    {
        details.ShowInstallUpdate = false;
        details.CanInstallUpdate = false;
        details.ShowUpdateFromFile = false;
        details.CanUpdateFromFile = false;
        details.CanOpenLocation = false;
        details.CanShowDevice = false;
        details.CanShowModule = false;
        details.ShowDeviceManager = false;
        details.Tools = [];
        details.NeedsElevation = string.Empty;
        details.LookTitle = string.Empty;
        details.LookText = string.Empty;
        details.About = string.Empty;

        switch (row)
        {
            case DeviceRow device:
                FillDevice(details, device);
                break;
            case ModuleRow module:
                FillModule(details, module);
                break;
            case UpdateRow update:
                FillUpdate(details, update);
                break;
            case ChangeRow change:
                FillChange(details, change);
                break;
            case CrashRow crash:
                FillCrash(details, crash);
                break;
        }
    }

    private void FillDevice(DriverDetails details, DeviceRow row)
    {
        var d = row.Device;
        details.Name = d.Name;
        details.Subtitle = d.ClassTitle;
        details.Status = row.Status;
        details.CopyText = "Copy instance id";
        details.ShowDeviceManager = true;

        if (d.IsPresent && d.Problem != 0)
        {
            details.LookTitle = DriverText.Problem(d.Problem);
            details.LookText = d.ProblemStatus != 0
                ? $"{DriverText.ProblemExplanation(d.Problem)} Windows' status: 0x{d.ProblemStatus:X8}."
                : DriverText.ProblemExplanation(d.Problem);
        }
        else if (!d.IsPresent)
        {
            details.About = "Not connected now. Windows remembers it and its driver for when it's back.";
        }

        var groups = new List<DriverFactGroup>();
        if (row.Update is { } update)
        {
            var facts = new List<DriverFact> { new("Update", update.Title) };
            if (update.Provider is { } provider)
                facts.Add(new DriverFact("Provider", provider));
            if (update.Date is { } date)
                facts.Add(new DriverFact("Date", DriverText.DriverDate(date)));
            facts.Add(new DriverFact("Kind", update.IsOptional ? "Optional: Windows won't install it on its own" : "Recommended: Windows installs it on its own in time"));
            groups.Add(new DriverFactGroup("Update available", facts));
            details.ShowInstallUpdate = true;
            details.CanInstallUpdate = Elevation.IsElevated && !_installing.Contains(update.UpdateId) && !_isInstallingAll;
        }

        var driver = new List<DriverFact>();
        if (!d.HasDriver)
        {
            driver.Add(new DriverFact("Driver", "None installed"));
        }
        else
        {
            Add(driver, "Provider", d.Provider);
            Add(driver, "Version", d.Version);
            if (d.Date is { } date)
                driver.Add(new DriverFact("Date", DriverText.DriverDate(date)));
            Add(driver, "INF", d.Inf is null ? null : d.InfSection is null ? d.Inf : $"{d.Inf} ({d.InfSection})");
            driver.Add(new DriverFact("Kind", d.IsThirdParty ? "A package added to Windows (third-party)" : "Shipped with Windows"));
        }

        DriverService? service = null;
        DriverFile? file = null;
        if (d.Service is { } serviceName && _snapshot is { } snapshot)
        {
            snapshot.Services.TryGetValue(serviceName, out service);
            driver.Add(new DriverFact("Service", service?.DisplayName is { } display && display != serviceName ? $"{serviceName} ({display})" : serviceName));
            if (service is not null)
            {
                driver.Add(new DriverFact("Starts", DriverText.StartType(service.Start)));
                if (service.Image is { } image)
                {
                    driver.Add(new DriverFact("File", image));
                    if (snapshot.Files.TryGetValue(image, out file) && file.Exists)
                    {
                        driver.Add(new DriverFact("Signature", DriverText.SignatureText(file.Signature)));
                        if (file.Signature is { State: SignatureState.Signed, Signer: { } signer })
                            driver.Add(new DriverFact("Signed by", signer));
                    }
                }
            }
        }

        groups.Add(new DriverFactGroup("Driver", driver));
        if (row.Origin is { } origin)
            groups.Add(SourceFacts(origin));
        details.Tools = ToolLinks(row.Origin);

        var device = new List<DriverFact> { new("Status", d.IsPresent ? row.Status.Text : "Not connected") };
        Add(device, "Manufacturer", d.Manufacturer);
        Add(device, "Location", d.Location);
        device.Add(new DriverFact("Instance id", d.InstanceId));
        if (d.HardwareIds.Count > 0)
            device.Add(new DriverFact("Hardware ids", string.Join("\n", d.HardwareIds.Take(4))));
        if (d.FirstInstalled is { } first)
            device.Add(new DriverFact("First installed", DriverText.Time(first)));
        if (d.LastArrival is { } arrived)
            device.Add(new DriverFact("Last connected", DriverText.Time(arrived)));
        if (!d.IsPresent && d.LastRemoval is { } removed)
            device.Add(new DriverFact("Last removed", DriverText.Time(removed)));
        groups.Add(new DriverFactGroup("Device", device));
        details.Groups = groups;

        details.ShowUpdateFromFile = true;
        details.CanUpdateFromFile = Elevation.IsElevated && d.IsPresent && (d.HardwareIds.Count > 0 || d.CompatibleIds.Count > 0);
        details.CanOpenLocation = file is { Exists: true };
        details.OpenLocationText = "Open driver file location";
        details.CanShowModule = service?.Image is { } path && ModuleAt(path) is not null;
        if (!Elevation.IsElevated)
            details.NeedsElevation = "Installing a driver needs administrator rights.";
    }

    private void FillModule(DriverDetails details, ModuleRow row)
    {
        var m = row.Module;
        var file = m.File;
        details.Name = m.Name;
        details.Subtitle = m.Service is { IsFileSystem: true } ? "File system driver" : "Kernel module";
        details.Status = row.Look.HasText ? row.Look : file.IsWindows ? new Badge("Windows", BadgeKind.Success) : new Badge("Signed", BadgeKind.Success);
        details.LookTitle = row.Look.Text;
        details.LookText = DriverText.LookExplanation(file);
        details.About = m.Service?.Description ?? string.Empty;
        details.CopyText = "Copy path";

        var module = new List<DriverFact> { new("Path", m.Path) };
        if (m.ImageBytes > 0)
            module.Add(new DriverFact("Size in memory", Format.Bytes(m.ImageBytes)));
        if (m.Service is { } service)
        {
            module.Add(new DriverFact("Service", service.DisplayName is { } display && display != service.Name ? $"{service.Name} ({display})" : service.Name));
            module.Add(new DriverFact("Starts", DriverText.StartType(service.Start)));
            Add(module, "Load group", service.Group);
        }
        else
        {
            module.Add(new DriverFact("Service", "None: loaded by Windows or by another driver"));
        }

        var users = UsersOf(m);
        if (users.Count > 0)
            module.Add(new DriverFact("Used by", string.Join("\n", users.Take(5).Select(u => u.Name)) + (users.Count > 5 ? $"\n…and {Format.Number(users.Count - 5)} more" : string.Empty)));

        var fileFacts = new List<DriverFact>();
        if (!file.Exists)
        {
            fileFacts.Add(new DriverFact("On disk", "Not found"));
        }
        else
        {
            fileFacts.Add(new DriverFact("Signature", DriverText.SignatureText(file.Signature)));
            if (file.Signature is { State: SignatureState.Signed, Signer: { } signer })
                fileFacts.Add(new DriverFact("Signed by", signer));
            if (file.Company is { } company)
                fileFacts.Add(new DriverFact("Company", file.Signature.State == SignatureState.Signed ? company : $"{company} (as the file says; not verified)"));
            Add(fileFacts, "Description", file.Description);
            Add(fileFacts, "Version", file.Version);
            if (file.Modified is { } modified)
                fileFacts.Add(new DriverFact("Modified", modified.ToString("g", CultureInfo.CurrentCulture)));
        }

        details.Groups = row.Origin is { } origin
            ? [new DriverFactGroup("Module", module), new DriverFactGroup("File", fileFacts), SourceFacts(origin)]
            : [new DriverFactGroup("Module", module), new DriverFactGroup("File", fileFacts)];
        details.Tools = ToolLinks(row.Origin);
        details.CanOpenLocation = file.Exists;
        details.OpenLocationText = "Open file location";
        details.CanShowDevice = users.Count > 0;
    }

    private void FillUpdate(DriverDetails details, UpdateRow row)
    {
        var u = row.Update;
        details.Name = u.Title;
        details.Subtitle = "Driver update from Windows Update";
        details.Status = row.Status;
        details.About = u.Description ?? string.Empty;
        details.CopyText = u.HardwareId is null ? string.Empty : "Copy hardware id";

        var update = new List<DriverFact>();
        Add(update, "Provider", u.Provider);
        Add(update, "Manufacturer", u.Manufacturer);
        Add(update, "Model", u.Model);
        Add(update, "Class", u.DriverClass);
        if (u.Date is { } date)
            update.Add(new DriverFact("Date", DriverText.DriverDate(date)));
        Add(update, "Hardware id", u.HardwareId);
        update.Add(new DriverFact("Kind", u.IsOptional ? "Optional: Windows won't install it on its own" : "Recommended: Windows installs it on its own in time"));
        update.Add(new DriverFact("Downloaded", u.IsDownloaded ? "Yes" : "Not yet"));

        var groups = new List<DriverFactGroup> { new("Update", update) };
        if (row.Device is { } d)
        {
            var now = new List<DriverFact> { new("Device", d.Name) };
            Add(now, "Provider", d.Provider);
            Add(now, "Version", d.Version);
            if (d.Date is { } current)
                now.Add(new DriverFact("Date", DriverText.DriverDate(current)));
            groups.Add(new DriverFactGroup("Installed now", now));
        }

        details.Groups = groups;
        details.ShowInstallUpdate = true;
        details.CanInstallUpdate = Elevation.IsElevated && !_installing.Contains(u.UpdateId) && !_isInstallingAll;
        details.CanShowDevice = row.Device is not null;
        if (!Elevation.IsElevated)
            details.NeedsElevation = "Installing a driver needs administrator rights.";
    }

    private void FillChange(DriverDetails details, ChangeRow row)
    {
        var c = row.Change;
        details.Name = row.Subject;
        details.Subtitle = row.TimeText;
        details.Status = row.Kind;
        details.About = c.Detail;
        details.CopyText = c.InstanceId is null ? string.Empty : "Copy instance id";

        var facts = new List<DriverFact>
        {
            new("When", DriverText.Time(c.Time)),
            new("What", row.Kind.Text),
            new("Seen", c.IsLive ? "Live, by this app" : c.Kind == ChangeKind.FailedToLoad ? "In the System log" : "In the Kernel-PnP configuration log"),
        };
        Add(facts, "Instance id", c.InstanceId);
        if (c.InstanceId is null)
            facts.Add(new DriverFact("Driver", c.Subject));
        details.Groups = [new DriverFactGroup("Change", facts)];
        details.CanShowDevice = c.InstanceId is { } id && _devicesById.ContainsKey(id);
        details.CanShowModule = c.InstanceId is null && ModuleNamed(c.Subject) is not null;
    }

    private void FillCrash(DriverDetails details, CrashRow row)
    {
        var c = row.Crash;
        details.Name = row.Kind.Text;
        details.Subtitle = row.TimeText;
        details.Status = row.Kind;
        details.About = DriverText.CrashAbout(c.Kind);
        details.CopyText = c.Code is null ? string.Empty : "Copy stop code";

        var module = c.Driver is { } driver ? ModuleNamed(driver) : null;
        if (c.Driver is { } blamed)
        {
            details.LookTitle = $"Probably {blamed}";
            var what = module is null ? null : module.Service?.DisplayName ?? module.File.Description;
            details.LookText = string.Join(" ", new[]
            {
                what is null ? null : $"{what}{(module!.File.Company is { } company ? $", from {company}" : string.Empty)}.",
                c.Blame is null ? null : $"{c.Blame}.",
                c.Kind == CrashKind.BlueScreen ? "A driver named here is the usual suspect, though faulty hardware or another driver can leave it holding the blame." : null,
            }.OfType<string>());
        }

        var facts = new List<DriverFact> { new("When", DriverText.Time(c.Time)) };
        if (c.Code is { } code)
        {
            facts.Add(new DriverFact("Stop code", DriverText.StopCode(code)));
            Add(facts, "Means", DriverText.StopExplanation(code));
        }

        if (c.Parameters.Count > 0)
            facts.Add(new DriverFact("Parameters", string.Join("\n", c.Parameters.Select(p => $"0x{p:X16}"))));
        if (c.Driver is null && c.Blame is { } blame)
            facts.Add(new DriverFact("Driver", blame));
        Add(facts, "Dump", c.Dump);
        Add(facts, "Report id", c.ReportId);
        details.Groups = [new DriverFactGroup("Crash", facts)];

        details.CanOpenLocation = c.Dump is { } dump && File.Exists(dump);
        details.OpenLocationText = "Open dump location";
        details.CanShowModule = module is not null;
        if (_crashes is { NeedsElevation: true } && c.Driver is null && c.Kind is CrashKind.BlueScreen or CrashKind.Recovered)
            details.NeedsElevation = "Reading the crash dump, to find the driver involved, needs administrator rights.";
    }

    private static DriverFactGroup SourceFacts(DriverOrigin origin)
    {
        var facts = new List<DriverFact> { new("From", origin.Title) };
        if (origin.Installed is { } installed)
            facts.Add(new DriverFact("Installed", DriverText.Time(installed)));
        if (origin.Package is { } package)
            facts.Add(new DriverFact("Package", package.Version is null ? package.Name : $"{package.Name} ({package.Version})"));
        if (origin.Source is { } source)
            facts.Add(new DriverFact(source.Contains('\\') ? "Installed from" : "For hardware id", source));
        facts.Add(new DriverFact("How we know", origin.Detail));
        facts.Add(new DriverFact("Updates", UpdatesFrom(origin)));
        return new DriverFactGroup("Source", facts);
    }

    /// <summary>Where newer versions of it come from, in words.</summary>
    private static string UpdatesFrom(DriverOrigin origin)
    {
        if (origin.Kind == OriginKind.Windows)
            return "With Windows itself: Windows Update keeps it current.";

        var from = new List<string>();
        if (origin.Kind == OriginKind.WindowsUpdate)
            from.Add("Windows Update (the Updates tab)");
        from.AddRange(origin.Tools.Select(t => t.Name));
        if (from.Count == 0)
        {
            from.Add(origin.Package is { } package
                ? $"a newer {package.Name}, from its maker; Windows Update may also offer one (the Updates tab)"
                : "its maker's website; Windows Update may also offer one (the Updates tab)");
        }

        return string.Join(", or ", from) + ".";
    }

    private IReadOnlyList<ToolLink> ToolLinks(DriverOrigin? origin) =>
        origin is null ? [] : [.. origin.Tools.Select(t => new ToolLink(t.Name, () => OpenTool(t)))];

    private void OpenTool(VendorTool tool)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(tool.Shortcut) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say($"Couldn't open {tool.Name}.", isError: true);
        }
    }

    private static void Add(List<DriverFact> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            facts.Add(new DriverFact(label, value));
    }

    private List<DeviceInfo> UsersOf(LoadedModule module) =>
        module.Service is { } service && _snapshot is { } snapshot
            ? [.. snapshot.Devices.Where(d => d.IsPresent && string.Equals(d.Service, service.Name, StringComparison.OrdinalIgnoreCase))]
            : [];

    private LoadedModule? ModuleAt(string path) =>
        _snapshot?.Modules.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>A loaded module by file name ("nvlddmkm.sys") or service name ("nvlddmkm").</summary>
    private LoadedModule? ModuleNamed(string name) =>
        _snapshot?.Modules.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Service?.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(m.Name), name, StringComparison.OrdinalIgnoreCase));

    // ---------- Navigation between tabs ----------

    private void ShowDevice()
    {
        var id = _selected switch
        {
            UpdateRow { Device: { } d } => d.InstanceId,
            ChangeRow { Change.InstanceId: { } i } => i,
            ModuleRow m => UsersOf(m.Module).FirstOrDefault()?.InstanceId,
            _ => null,
        };

        if (id is null || _deviceRows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } row)
            return;

        if (!MatchesDevice(row))
        {
            // Make sure it's listed: every device, no search, its group open.
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(HasSearch));
            SetDeviceShow(true, DevicesAll);
        }

        _collapsed.Remove(row.Device.ClassTitle);
        Open(TabDevices, row);
    }

    private void ShowModule()
    {
        var module = _selected switch
        {
            DeviceRow { Device.Service: { } s } when _snapshot?.Services.GetValueOrDefault(s)?.Image is { } image => ModuleAt(image),
            CrashRow { Crash.Driver: { } driver } => ModuleNamed(driver),
            ChangeRow { Change.InstanceId: null } change => ModuleNamed(change.Change.Subject),
            _ => null,
        };

        if (module is null || _moduleRows.FirstOrDefault(r => r.Module.Path == module.Path) is not { } row)
            return;

        if (!MatchesModule(row))
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(HasSearch));
            SetModuleShow(true, ModulesAll);
        }

        Open(TabModules, row);
    }

    private void Open(int tab, DriverRow row)
    {
        Select(null);
        SetTab(true, tab);
        Render();
        Select(row);
    }

    // ---------- Actions ----------

    private void OpenLocation()
    {
        var path = _selected switch
        {
            ModuleRow m => m.Module.Path,
            DeviceRow { Device.Service: { } s } => _snapshot?.Services.GetValueOrDefault(s)?.Image,
            CrashRow c => c.Crash.Dump,
            _ => null,
        };

        if (path is null)
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("Couldn't open the folder.", isError: true);
        }
    }

    private void OpenDeviceManager()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("Couldn't open Device Manager.", isError: true);
        }
    }

    private void CopySelected()
    {
        var text = _selected switch
        {
            DeviceRow d => d.Device.InstanceId,
            ModuleRow m => m.Module.Path,
            UpdateRow u => u.Update.HardwareId,
            ChangeRow c => c.Change.InstanceId,
            CrashRow { Crash.Code: { } code } c => c.Crash.Parameters.Count > 0
                ? $"{DriverText.StopCode(code)} ({string.Join(", ", c.Crash.Parameters.Select(p => $"0x{p:X}"))})"
                : DriverText.StopCode(code),
            _ => null,
        };

        if (text is not null)
            CopyRequested?.Invoke(text);
    }

    private void Say(string message, bool isError)
    {
        if (_details is { } details)
        {
            details.Message = message;
            details.MessageIsError = isError;
        }
        else
        {
            Notice = message;
            NoticeIsError = isError;
        }
    }

    private void InstallSelectedUpdate()
    {
        var update = _selected switch
        {
            UpdateRow u => u.Update,
            DeviceRow { Update: { } u } => u,
            _ => null,
        };

        if (update is not null)
            InstallUpdate(update, _details, done: null);
    }

    /// <summary>Downloads and installs one update, telling <paramref name="details"/> (when it's still on screen) how it goes.</summary>
    private void InstallUpdate(DriverUpdate update, DriverDetails? details, Action<bool, bool>? done)
    {
        if (!_installing.Add(update.UpdateId))
            return;

        void Progress(string text)
        {
            if (_details is { } shown && ReferenceEquals(shown, details))
            {
                shown.Message = text;
                shown.MessageIsError = false;
            }

            if (_updateRows.FirstOrDefault(r => r.Id == update.UpdateId) is { } row)
                row.Status = new Badge(text.TrimEnd('…') + "…", BadgeKind.Info);
        }

        if (details is not null)
            details.IsBusy = true;
        Progress("Starting…");
        OnPropertyChanged(nameof(CanInstallAll));
        OnPropertyChanged(nameof(CanCheckUpdates));
        RefreshSelection();

        _ = Task.Run(() =>
        {
            (string? Failure, bool Restart) result;
            try
            {
                result = WindowsUpdate.Install(update.UpdateId, text => Dispatcher.UIThread.Post(() => Progress(text)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Driver update failed: {ex}");
                result = ("Windows Update couldn't install it.", false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                _installing.Remove(update.UpdateId);
                var message = result.Failure ?? (result.Restart
                    ? $"Installed. Restart Windows to finish installing {update.Title}."
                    : $"Installed {update.Title}.");
                if (_details is { } shown && ReferenceEquals(shown, details))
                {
                    shown.IsBusy = false;
                    shown.Message = message;
                    shown.MessageIsError = result.Failure is not null;
                }

                if (_updateRows.FirstOrDefault(r => r.Id == update.UpdateId) is { } row)
                    row.Status = result.Failure is null ? new Badge("Installed", BadgeKind.Success) : new Badge("Failed", BadgeKind.Danger);

                if (result.Restart)
                {
                    Notice = "Restart Windows to finish installing the driver updates.";
                    NoticeIsError = false;
                }

                OnPropertyChanged(nameof(CanInstallAll));
                OnPropertyChanged(nameof(CanCheckUpdates));
                if (done is not null)
                {
                    done(result.Failure is null, result.Restart);
                }
                else if (result.Failure is null)
                {
                    ScanDevices();
                    CheckUpdates();
                }
                else
                {
                    RefreshSelection();
                }
            });
        });
    }

    private void AskInstallAll()
    {
        if (_updates is not { Count: > 0 } updates || !CanInstallAll)
            return;

        var optional = updates.Count(u => u.IsOptional);
        Ask(
            $"Install {Plural(updates.Count, "driver update", "driver updates")}?",
            "Windows Update downloads and installs them one at a time. Devices may stop for a moment while their driver changes, and some updates finish only after a restart.",
            optional == 0 ? null
                : $"{(optional == updates.Count ? optional == 1 ? "It's" : "They're all" : $"{Format.Number(optional)} of them {(optional == 1 ? "is" : "are")}")} optional:"
                  + " Windows wouldn't install optional drivers on its own. They're meant for specific problems; if a device works, it may not need one.",
            "Install all",
            "IconDownload",
            () => InstallAll([.. updates]));
    }

    private void InstallAll(List<DriverUpdate> updates)
    {
        IsInstallingAll = true;
        _stopInstallingAll = false;
        var installed = 0;
        var failed = 0;
        var restart = false;

        void Next(int index)
        {
            if (index >= updates.Count || _stopInstallingAll)
            {
                IsInstallingAll = false;
                Notice = string.Join(" ", new[]
                {
                    $"Installed {Plural(installed, "driver update", "driver updates")}.",
                    failed > 0 ? $"{Format.Number(failed)} failed: select {(failed == 1 ? "it" : "one")} to see why." : null,
                    index < updates.Count ? $"Stopped before the other {Format.Number(updates.Count - index)}." : null,
                    restart ? "Restart Windows to finish." : null,
                }.OfType<string>());
                NoticeIsError = failed > 0;
                ScanDevices();
                CheckUpdates();
                return;
            }

            var details = _details is { } shown && shown.Id == $"{TabUpdates}|{updates[index].UpdateId}" ? shown : null;
            InstallUpdate(updates[index], details, (ok, needsRestart) =>
            {
                if (ok)
                    installed++;
                else
                    failed++;
                restart |= needsRestart;
                Next(index + 1);
            });
        }

        Next(0);
    }

    /// <summary>The INF the view picked: for the selected device, or (toolbar) for whatever devices it fits.</summary>
    public void InstallInf(string path, bool forDevice)
    {
        if (!forDevice)
        {
            Notice = $"Installing {Path.GetFileName(path)}…";
            NoticeIsError = false;
            _ = Task.Run(() =>
            {
                (string? Failure, bool Restart) result;
                try
                {
                    result = DriverPackages.Install(path);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"INF install failed: {ex}");
                    result = ("Windows couldn't install it.", false);
                }

                Dispatcher.UIThread.Post(() =>
                {
                    Notice = result.Failure ?? (result.Restart
                        ? $"{Path.GetFileName(path)} is installed. Restart Windows to finish."
                        : $"{Path.GetFileName(path)} is in the driver store, and installed on the connected devices it's the best driver for.");
                    NoticeIsError = result.Failure is not null;
                    if (result.Failure is null)
                        ScanDevices();
                });
            });
            return;
        }

        if (_selected is not DeviceRow row || _details is not { } details)
            return;

        InstallFor(row.Device, path, details, force: false);
    }

    private void InstallFor(DeviceInfo device, string inf, DriverDetails details, bool force)
    {
        details.IsBusy = true;
        details.Message = $"Installing from {Path.GetFileName(inf)}…";
        details.MessageIsError = false;
        var ids = device.HardwareIds.Concat(device.CompatibleIds).ToList();

        _ = Task.Run(() =>
        {
            (InfOutcome Outcome, string? Failure, bool Restart) result;
            try
            {
                result = DriverPackages.InstallFor(inf, ids, force);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Device INF install failed: {ex}");
                result = (InfOutcome.Failed, "Windows couldn't install it.", false);
            }

            Dispatcher.UIThread.Post(() =>
            {
                details.IsBusy = false;
                switch (result.Outcome)
                {
                    case InfOutcome.NotBetter:
                        details.Message = string.Empty;
                        Ask(
                            $"Install it on {device.Name} anyway?",
                            $"Windows ranks the driver the device has ({device.Provider} {device.Version}) as a better match than the one in {Path.GetFileName(inf)}, so it kept it.",
                            "Installing a driver Windows ranks lower can make the device stop working. The driver it replaces stays in the driver store, so Device Manager can roll back to it.",
                            "Install anyway",
                            "IconWarning",
                            () => InstallFor(device, inf, details, force: true));
                        break;
                    case InfOutcome.Installed:
                        details.Message = result.Restart ? "Installed. Restart Windows to finish." : "Installed.";
                        details.MessageIsError = false;
                        ScanDevices();
                        break;
                    default:
                        details.Message = result.Failure ?? "Windows couldn't install it.";
                        details.MessageIsError = true;
                        break;
                }
            });
        });
    }

    private void Ask(string title, string message, string? warning, string confirmText, string icon, Action confirm)
    {
        ConfirmIcon = ThemeResource.Geometry(icon);
        Confirmation = new Confirmation(title, message, warning, confirmText,
            () =>
            {
                Confirmation = null;
                confirm();
            },
            () => Confirmation = null);
    }

    // ---------- Text ----------

    private static string Plural(int count, string one, string many) => $"{Format.Number(count)} {(count == 1 ? one : many)}";

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
