using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// The Apps page's state: every installed app, grouped by how it was installed or in one list,
/// filtered to recent installs, to those worth a look, or by a search, with frameworks, components
/// and updates hidden unless asked for. The selected app's details uninstall, change or repair it
/// the way it was installed. Scans when first shown, on Refresh, after a change, and when the
/// uninstall registry or this user's packages change while the page is on screen; nothing runs per tick.
/// </summary>
public sealed class AppList : ObservableObject, IDisposable
{
    private const int ShowAll = 0;
    private const int ShowRecent = 1;
    private const int ShowProblems = 2;

    private static readonly TimeSpan RecentSpan = TimeSpan.FromDays(30);

    /// <summary>Installers write in bursts: wait for them to settle before reading again.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>How long an uninstall is followed after its program exits, for uninstallers that hand over to a copy of themselves.</summary>
    private static readonly TimeSpan FollowFor = TimeSpan.FromMinutes(30);

    private readonly Navigator _navigator;
    private readonly AppScanner _scanner = new();
    private readonly AppRowActions _rowActions;
    private readonly DispatcherTimer _settle;

    // UI thread.
    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AppGroup, AppGroupRow> _groups = [];
    private readonly HashSet<AppGroup> _collapsed = [];
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Name, DateTime Until)> _following = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _diskSizes = new(StringComparer.OrdinalIgnoreCase);
    private AppWatcher? _watcher;
    private CancellationTokenSource? _measuring;
    private AppSnapshot? _latest;
    private int _show = ShowAll;
    private bool _showComponents;
    private bool _isGrouped = true;
    private string _searchText = string.Empty;
    private AppColumn _sortColumn;
    private string _summary = "Reading installed apps…";
    private string _notice = string.Empty;
    private AppRow? _selected;
    private AppDetails? _details;
    private Confirmation? _confirmation;
    private Geometry? _confirmIcon;
    private bool _confirmIsDanger;
    private bool _isEmpty;
    private bool _isLoaded;
    private bool _isScanning;
    private bool _rescan;
    private bool _rescanFeatures;
    private bool _wasShown;
    private bool _isOnScreen;
    private bool _stale;

    internal AppList(Navigator navigator)
    {
        _navigator = navigator;
        _rowActions = new AppRowActions(Select, row => Act(row, () => AskRun(Kind.Uninstall)), row => Act(row, () => AskRun(Kind.Modify)),
            row => Act(row, () => AskRun(Kind.Repair)), row => Act(row, OpenLocation), row => CopyRequested?.Invoke(row.Name));

        NameColumn = new AppColumn("Name", AppSortKey.Name, Sort);
        PublisherColumn = new AppColumn("Publisher", AppSortKey.Publisher, Sort);
        VersionColumn = new AppColumn("Version", AppSortKey.Version, Sort);
        InstalledColumn = new AppColumn("Installed", AppSortKey.Installed, Sort);
        SizeColumn = new AppColumn("Size", AppSortKey.Size, Sort);
        InstalledWithColumn = new AppColumn("Installed with", AppSortKey.InstalledWith, Sort);
        _sortColumn = NameColumn;
        NameColumn.IsActive = true;

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        RefreshCommand = new RelayCommand(() => Refresh(readFeatures: true));
        OpenBackupsCommand = new RelayCommand(OpenBackups);

        _settle = new DispatcherTimer { Interval = SettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Refresh(readFeatures: false);
        };
    }

    private enum Kind
    {
        Uninstall,
        Modify,
        Repair,
    }

    /// <summary>The view puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>
    /// Set by the view as the page comes and goes. The first time it's shown, it scans and starts
    /// following the registry; after that, it reads again if something changed while away.
    /// </summary>
    public bool IsOnScreen
    {
        get => _isOnScreen;
        set
        {
            if (_isOnScreen == value)
                return;

            _isOnScreen = value;
            if (!value)
                return;

            if (!_wasShown)
            {
                _wasShown = true;
                _watcher = new AppWatcher([.. InstalledPrograms.Keys, PackagedApps.Key], () => Dispatcher.UIThread.Post(OnRegistryChanged));
                Refresh(readFeatures: true);
            }
            else if (_stale)
            {
                _stale = false;
                Refresh(readFeatures: false);
            }
        }
    }

    /// <summary>Group headings and apps, in display order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    public AppColumn NameColumn { get; }

    public AppColumn PublisherColumn { get; }

    public AppColumn VersionColumn { get; }

    public AppColumn InstalledColumn { get; }

    public AppColumn SizeColumn { get; }

    public AppColumn InstalledWithColumn { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenBackupsCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    /// <summary>Without elevation, optional features can't be read, and removing apps for everyone asks for rights each time.</summary>
    public bool ShowElevationHint => !Elevation.IsElevated;

    public bool IsShowAll
    {
        get => _show == ShowAll;
        set => SetShow(value, ShowAll);
    }

    public bool IsShowRecent
    {
        get => _show == ShowRecent;
        set => SetShow(value, ShowRecent);
    }

    public bool IsShowProblems
    {
        get => _show == ShowProblems;
        set => SetShow(value, ShowProblems);
    }

    /// <summary>Frameworks, runtimes, components and updates too: they come and go with what they belong to.</summary>
    public bool ShowComponents
    {
        get => _showComponents;
        set
        {
            if (Set(ref _showComponents, value))
                Render();
        }
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

    /// <summary>The outcome of an action whose app has since left the list (uninstalled).</summary>
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

    /// <summary>Nothing listed yet: the first scan is under way.</summary>
    public bool IsLoading => !_isLoaded;

    public string EmptyTitle => HasSearch ? "Nothing matches" : _show == ShowProblems ? "Nothing to check" : _show == ShowRecent ? "Nothing new" : "Nothing to show";

    public string EmptyHint => HasSearch
        ? "Try another search, or show components as well."
        : _show == ShowProblems
            ? "Every app's uninstaller and folder are where they should be."
            : _show == ShowRecent
                ? "Nothing was installed in the last 30 days."
                : "Try showing components as well.";

    public AppDetails? Details
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

    /// <summary>Uninstalling is destructive (rust); changing and repairing aren't.</summary>
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

    public void Dispose()
    {
        _watcher?.Dispose();
        _measuring?.Cancel();
        _settle.Stop();
    }

    // ---------- Scanning ----------

    /// <param name="readFeatures">Read optional features again (slow): on Refresh, or after changing one.</param>
    public void Refresh(bool readFeatures)
    {
        if (_isScanning)
        {
            _rescan = true;
            _rescanFeatures |= readFeatures;
            return;
        }

        IsScanning = true;
        if (_isLoaded)
            Summary = "Reading installed apps…";

        _ = Task.Run(() =>
        {
            try
            {
                _scanner.Scan(snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot)), readFeatures);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Apps scan failed: {ex}");
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    IsScanning = false;
                    if (_latest is not null)
                        Summary = SummaryOf(_latest);
                    if (_rescan)
                    {
                        var features = _rescanFeatures;
                        _rescan = false;
                        _rescanFeatures = false;
                        Refresh(features);
                    }
                });
            }
        });
    }

    /// <summary>Something was installed or uninstalled: read again once it settles, now if on screen, else when next shown.</summary>
    private void OnRegistryChanged()
    {
        if (!_isOnScreen)
        {
            _stale = true;
            return;
        }

        _settle.Stop();
        _settle.Start();
    }

    private void Apply(AppSnapshot snapshot)
    {
        _latest = snapshot;
        if (!_isLoaded)
        {
            _isLoaded = true;
            OnPropertyChanged(nameof(IsLoading));
        }

        Render();
        if (!snapshot.IsReading)
            CheckFollowed(snapshot);
    }

    /// <summary>Uninstalls whose app has now left the list are said to be done; those past their time are let go.</summary>
    private void CheckFollowed(AppSnapshot snapshot)
    {
        if (_following.Count == 0)
            return;

        var present = new HashSet<string>(snapshot.Items.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var (id, (name, until)) in _following.ToList())
        {
            if (!present.Contains(id))
            {
                _following.Remove(id);
                Notice = $"{name} is uninstalled.";
            }
            else if (now > until)
            {
                _following.Remove(id);
            }
        }
    }

    private void SetShow(bool isChecked, int show)
    {
        if (!isChecked || _show == show)
            return;

        _show = show;
        OnPropertyChanged(nameof(IsShowAll));
        OnPropertyChanged(nameof(IsShowRecent));
        OnPropertyChanged(nameof(IsShowProblems));
        Render();
    }

    private void Sort(AppColumn column)
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
            // Newest and largest first is what's wanted from those columns.
            column.IsDescending = column.Key is AppSortKey.Installed or AppSortKey.Size;
        }

        Render();
    }

    // ---------- Rendering ----------

    private void Render()
    {
        if (_latest is not { } snapshot)
            return;

        var ids = new HashSet<string>(snapshot.Items.Count, StringComparer.OrdinalIgnoreCase);
        var shown = new List<AppRow>(snapshot.Items.Count);
        foreach (var item in snapshot.Items)
        {
            if (!ids.Add(item.Id))
                continue;
            if (!_rows.TryGetValue(item.Id, out var row))
                _rows[item.Id] = row = new AppRow(item.Id, _rowActions);

            row.Item = item;
            Fill(row, item);
            if (Matches(item))
                shown.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
            _rows.Remove(gone);

        shown.Sort(Compare);
        var wanted = new List<object>(shown.Count + 8);
        if (_isGrouped)
        {
            var searching = HasSearch;
            var present = new HashSet<AppGroup>();
            foreach (var group in shown.GroupBy(r => r.Item!.Group).OrderBy(g => g.Key))
            {
                present.Add(group.Key);
                if (!_groups.TryGetValue(group.Key, out var header))
                    _groups[group.Key] = header = new AppGroupRow(group.Key, !_collapsed.Contains(group.Key), Toggle);

                var count = group.Count();
                var problems = group.Count(r => r.HasProblem);
                header.CountText = problems > 0
                    ? $"{Plural(count, "app", "apps")} · {Format.Number(problems)} to check"
                    : Plural(count, "app", "apps");
                header.HasProblem = problems > 0;
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

        Reconcile(Items, wanted);
        IsEmpty = shown.Count == 0;
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        Summary = SummaryOf(snapshot);

        if (_selected is not null)
        {
            if (!_rows.TryGetValue(_selected.Id, out var current))
            {
                // Gone (uninstalled) once every source has been read; until then it may just not be read yet.
                if (!snapshot.IsReading)
                    Select(null);
            }
            else if (_details is { } details && current.Item is { } item)
            {
                FillDetails(details, item);
            }
        }
    }

    private bool Matches(AppItem item)
    {
        if (!_showComponents && AppText.IsComponent(item) && !HasSearch)
            return false;

        switch (_show)
        {
            case ShowRecent when item.InstalledOn is not { } date || DateTime.Now - date > RecentSpan:
            case ShowProblems when item.Problem is null:
                return false;
        }

        if (_searchText.Length == 0)
            return true;

        var search = _searchText.Trim();
        return Contains(item.Name, search)
            || Contains(item.Publisher, search)
            || Contains(item.InstalledWith, search)
            || Contains(item.Version, search)
            || Contains(item.Location, search)
            || item.Facts.Any(f => Contains(f.Value, search));
    }

    private static bool Contains(string? text, string search) => text?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private int Compare(AppRow a, AppRow b)
    {
        var x = a.Item!;
        var y = b.Item!;
        var result = _sortColumn.Key switch
        {
            AppSortKey.Publisher => CompareText(x.Publisher, y.Publisher),
            AppSortKey.Version => AppText.CompareVersions(x.Version, y.Version),
            AppSortKey.Installed => Nullable.Compare(x.InstalledOn, y.InstalledOn),
            AppSortKey.Size => Nullable.Compare(x.Size, y.Size),
            AppSortKey.InstalledWith => string.Compare(x.InstalledWith, y.InstalledWith, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };

        if (result == 0)
            result = string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase);
        if (result == 0)
            result = string.Compare(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);
        return _sortColumn.IsDescending ? -result : result;
    }

    /// <summary>Text with blanks last, whichever way it's sorted.</summary>
    private int CompareText(string? a, string? b)
    {
        if (a is null || b is null)
        {
            var blank = a is null ? (b is null ? 0 : 1) : -1;
            return _sortColumn.IsDescending ? -blank : blank;
        }

        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Toggle(AppGroupRow group)
    {
        if (HasSearch)
            return;

        if (!_collapsed.Remove(group.Group))
            _collapsed.Add(group.Group);
        Render();
    }

    private void Fill(AppRow row, AppItem item)
    {
        row.Name = item.Name;
        row.PublisherText = item.Publisher ?? "—";
        row.VersionText = item.Version ?? "—";
        row.InstalledText = AppText.Date(item.InstalledOn);
        row.SizeText = AppText.Size(item.Size);
        row.InstalledWithText = item.InstalledWith;
        row.ProblemText = item.Problem ?? string.Empty;
        row.ToolTip = item.Location is { } location ? $"{item.Name}\n{location}" : item.Name;
        row.UninstallText = $"{item.UninstallText}…";
        row.HasLocation = item.Location is not null;

        var idle = !_busy.Contains(item.Id);
        row.CanUninstall = idle && item.Uninstall is not null;
        row.CanModify = idle && item.Modify is not null;
        row.CanRepair = idle && item.Repair is not null;
    }

    private string SummaryOf(AppSnapshot snapshot)
    {
        var items = snapshot.Items;
        var components = items.Count(AppText.IsComponent);
        var problems = items.Count(i => i.Problem is not null && (_showComponents || !AppText.IsComponent(i)));
        var parts = new List<string> { Plural(items.Count - components, "app", "apps") };
        parts.Add(problems > 0 ? $"{Format.Number(problems)} to check" : "nothing to check");
        if (components > 0)
            parts.Add(_showComponents ? $"{Format.Number(components)} components and updates" : $"{Format.Number(components)} components and updates hidden");
        if (snapshot.IsReading || _isScanning)
            parts.Add("reading…");
        return string.Join(" · ", parts);
    }

    // ---------- Details ----------

    private void Select(AppRow? row)
    {
        if (row is not null && ReferenceEquals(row, _selected))
            row = null;

        if (_selected is not null)
            _selected.IsSelected = false;

        _measuring?.Cancel();
        _measuring = null;
        _selected = row;
        if (row?.Item is not { } item)
        {
            _selected = null;
            Details = null;
            return;
        }

        row.IsSelected = true;
        Notice = string.Empty;
        var details = new AppDetails(row.Id, new AppDetailActions(
            () => Select(null),
            () => AskRun(Kind.Uninstall),
            () => AskRun(Kind.Modify),
            () => AskRun(Kind.Repair),
            AskRemoveEntry,
            OpenLocation,
            ShowProcesses,
            () => OpenWeb(_selected?.Item?.Website),
            () => OpenWeb(_selected?.Item?.HelpLink),
            OpenRegistry,
            () => CopyRequested?.Invoke((_selected?.Item?.Uninstall as CommandAction)?.CommandLine ?? string.Empty),
            OpenBackups));
        FillDetails(details, item);
        Details = details;
        ReadFolder(details, item);
    }

    private void Act(AppRow row, Action action)
    {
        if (!ReferenceEquals(row, _selected))
            Select(row);
        if (ReferenceEquals(row, _selected))
            action();
    }

    private void FillDetails(AppDetails details, AppItem item)
    {
        details.Name = item.Name;
        details.Subtitle = string.Join(" · ", new[] { item.Publisher, item.Version is { } v ? $"version {v}" : null }.OfType<string>());
        details.About = item.About;
        details.ProblemText = item.Problem ?? string.Empty;
        details.ProblemExplanation = item.ProblemExplanation ?? string.Empty;
        details.UninstallText = item.UninstallText;
        details.HasLocation = item.Location is not null;
        details.HasWebsite = item.Website is not null;
        details.HasHelp = item.HelpLink is not null && item.HelpLink != item.Website;
        details.HasRegistry = item.RegistryKey is not null;
        details.HasCommand = item.Uninstall is CommandAction;

        var idle = !_busy.Contains(item.Id);
        details.CanUninstall = idle && item.Uninstall is not null;
        details.CanModify = idle && item.Modify is not null;
        details.CanRepair = idle && item.Repair is not null;
        details.CanRemoveEntry = idle && item.Entry is not null && item.Problem is not null;
        details.NeedsElevation = details.CanRemoveEntry && item.Entry!.NeedsAdmin && !Elevation.IsElevated;
        details.Locked = item.Uninstall is null ? item.Locked ?? "This can't be uninstalled from here." : string.Empty;

        var facts = new List<AppFact> { new("Installed with", item.InstalledWith), new("For", AppText.Scope(item)) };
        if (item.InstalledOn is { } installed)
            facts.Add(new AppFact("Installed", AppText.Date(installed)));
        if (item.Size is { } size)
            facts.Add(new AppFact("Size", item.Group == AppGroup.Features ? Format.Bytes(size) : $"{Format.Bytes(size)} (the installer's estimate)"));
        if (item.Location is { } location && _diskSizes.TryGetValue(location, out var disk))
            facts.Add(new AppFact("On disk", disk));
        if (item.Architecture is { } architecture)
            facts.Add(new AppFact("Architecture", architecture));
        if (item.Location is { } folder)
            facts.Add(new AppFact("Location", folder));
        if (item.Website is { } website)
            facts.Add(new AppFact("Website", website));
        facts.AddRange(item.Facts);
        if (item.RegistryKey is { } key && item.Group != AppGroup.Packaged)
            facts.Add(new AppFact("Registry", key));
        details.Facts = facts;
    }

    /// <summary>What runs from the app's folder, and its size on disk, read in the background for the selected app.</summary>
    private void ReadFolder(AppDetails details, AppItem item)
    {
        if (item.Location is not { } location || !AppFolder.IsOwn(location))
            return;

        var cancel = new CancellationTokenSource();
        _measuring = cancel;
        var measure = !_diskSizes.ContainsKey(location);
        _ = Task.Run(() =>
        {
            List<(int Id, string Name)> running;
            try
            {
                running = AppFolder.Running(location);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Reading an app's processes failed: {ex}");
                running = [];
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_details, details))
                    details.RunningText = RunningText(running);
            });

            if (!measure)
                return;

            string text;
            try
            {
                var bytes = AppFolder.Measure(location, cancel.Token, out var complete);
                text = bytes is { } value ? complete ? Format.Bytes(value) : $"at least {Format.Bytes(value)}" : "Can't be read";
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _diskSizes[location] = text;
                if (ReferenceEquals(_details, details) && _selected?.Item is { } current)
                    FillDetails(details, current);
            });
        }, cancel.Token);
    }

    private static string RunningText(List<(int Id, string Name)> running)
    {
        if (running.Count == 0)
            return string.Empty;

        var names = running.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        var list = string.Join(", ", names) + (running.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > names.Count ? "…" : string.Empty);
        return $"Running now: {Plural(running.Count, "process", "processes")} ({list})";
    }

    private void OpenLocation()
    {
        if (_selected?.Item?.Location is not { } location)
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{location}\""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("Couldn't open its folder.", isError: true);
        }
    }

    private void ShowProcesses()
    {
        if (_selected?.Item?.Location is { } location)
            _navigator.ShowProcesses(new ProcessQuery(Folder: location));
    }

    /// <summary>Only http and https links reach the shell (the sources check, and so does this).</summary>
    private void OpenWeb(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("Couldn't open the link.", isError: true);
        }
    }

    private void OpenRegistry()
    {
        if (_selected?.Item?.RegistryKey is { } key && Backups.OpenInRegistryEditor(key) is { } failure)
            Say(failure, isError: true);
    }

    private void OpenBackups()
    {
        try
        {
            Directory.CreateDirectory(AppActions.BackupFolder);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppActions.BackupFolder}\""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Say("Couldn't open the backups folder.", isError: true);
        }
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
        }
    }

    // ---------- Actions ----------

    private void AskRun(Kind kind)
    {
        if (_selected?.Item is not { } item)
            return;

        var action = kind switch
        {
            Kind.Modify => item.Modify,
            Kind.Repair => item.Repair,
            _ => item.Uninstall,
        };
        if (action is null)
            return;

        var running = _details?.IsRunning == true
            ? $"{item.Name} is running. Close it first, or its uninstaller may fail, or ask for a restart to finish."
            : null;

        if (kind != Kind.Uninstall)
        {
            var verb = kind == Kind.Modify ? "Change" : "Repair";
            Ask($"{verb} {item.Name}?",
                kind == Kind.Modify
                    ? "Its installer opens in change mode: choose what to add or remove there."
                    : "Its installer puts back missing or damaged files, registry entries and shortcuts. Your settings are kept.",
                null, verb, kind == Kind.Modify ? "IconPencil" : "IconWrench", danger: false,
                () => Start(item, action, kind == Kind.Modify ? $"Changing {item.Name}… finish in its installer's window." : $"Repairing {item.Name}…",
                    kind == Kind.Modify ? $"{item.Name}'s installer has closed." : $"{item.Name} is repaired."));
            return;
        }

        var (message, warning) = action switch
        {
            PackageRemoval => ("Windows removes the app and its data for your account. Other accounts that have it keep it.", running),
            CapabilityRemoval => ("Windows removes it. This can take a few minutes, and some need a restart to finish. It can be added again from Settings → Optional features.",
                (string?)"Apps that use it stop working."),
            FeatureRemoval => ("Windows turns it off, which can take a few minutes and may need a restart. Its files are kept, so it can be turned on again.",
                (string?)"Programs and other features that need it stop working."),
            CommandAction { Program: var program } when program.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) || program.EndsWith("choco.exe", StringComparison.OrdinalIgnoreCase) =>
                ($"{item.InstalledWith} uninstalls it in a console window. The list updates when it's done.", running),
            CommandAction { IsMsi: true } => ("Windows Installer uninstalls it, and may ask you to confirm. The list updates when it's done.", running),
            _ => ("Its uninstaller opens: follow its steps there. The list updates when it's done.", running),
        };

        Ask($"{item.UninstallText} {item.Name}?", message, warning, item.UninstallText, kind == Kind.Uninstall && item.UninstallText == "Turn off" ? "IconPower" : "IconTrash",
            danger: true, () =>
            {
                if (action is CommandAction)
                    _following[item.Id] = (item.Name, DateTime.UtcNow + FollowFor);
                Start(item, action, action is CommandAction ? $"Uninstalling {item.Name}… finish in its uninstaller's window." : $"{item.UninstallText}: {item.Name}…",
                    action is CommandAction ? null : $"{item.Name} is {(item.UninstallText == "Turn off" ? "turned off" : "removed")}.");
            });
    }

    /// <param name="success">What to say when it worked; null to wait and see whether the app leaves the list.</param>
    private void Start(AppItem item, AppAction action, string progress, string? success)
    {
        var features = action is CapabilityRemoval or FeatureRemoval;
        Run(item.Id, progress, () => AppActions.Run(action), outcome =>
        {
            if (!outcome.Succeeded)
            {
                _following.Remove(item.Id);
                return outcome.Failure!;
            }

            var restart = outcome.NeedsRestart ? " Restart Windows to finish." : string.Empty;
            return success is not null
                ? success + restart
                : $"The uninstaller has closed.{restart} If {item.Name} stays listed, its window may still be open: the list updates when it finishes.";
        }, features);
    }

    private void AskRemoveEntry()
    {
        if (_selected?.Item is not { Entry: { } entry } item)
            return;

        Ask($"Remove the entry for {item.Name}?",
            "Its entry is deleted from the uninstall registry, so it's no longer listed here or in Settings. Nothing else changes: files it left stay where they are. A .reg file of the entry is saved in the backups folder: open it to put the entry back.",
            item.Problem is null ? "Its uninstaller is still there: uninstalling it would remove it properly." : null,
            "Remove entry", "IconTrash", danger: true,
            () =>
            {
                string? backup = null;
                Run(item.Id, $"Removing the entry for {item.Name}…", () =>
                {
                    var outcome = AppActions.RemoveEntry(entry, item.Name);
                    backup = outcome.Backup;
                    return outcome;
                }, outcome =>
                {
                    if (!outcome.Succeeded)
                        return outcome.Failure!;
                    Notice = backup is null ? $"The entry for {item.Name} is removed." : $"The entry for {item.Name} is removed. A backup is in {backup}";
                    return "The entry is removed.";
                }, features: false);
            });
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

    /// <summary>Off the UI thread (uninstallers run as long as they run), then a rescan to show the result.</summary>
    /// <param name="describe">The outcome in words, on the UI thread.</param>
    private void Run(string id, string progress, Func<AppOutcome> action, Func<AppOutcome, string> describe, bool features)
    {
        _busy.Add(id);
        Notice = string.Empty;
        if (_details is { } details && details.Id == id)
        {
            details.Message = progress;
            details.MessageIsError = false;
            details.IsBusy = true;
        }

        Render();

        _ = Task.Run(() =>
        {
            AppOutcome outcome;
            try
            {
                outcome = action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Apps action failed: {ex}");
                outcome = new AppOutcome("Windows couldn't do that.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy.Remove(id);
                var text = describe(outcome);
                if (_details is { } shown && shown.Id == id)
                {
                    shown.IsBusy = false;
                    shown.Message = text;
                    shown.MessageIsError = !outcome.Succeeded;
                    if (_selected?.Item is { } item)
                        ReadFolder(shown, item);
                }
                else if (!outcome.Succeeded)
                {
                    Notice = text;
                }

                Render();
                Refresh(features);
            });
        });
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
