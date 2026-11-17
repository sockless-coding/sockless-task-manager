using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// The Autostart page's state: every entry, grouped by where it starts from or in one list, filtered
/// by state, by "worth a look" and by a search, with Windows' own hidden unless asked for. The
/// selected entry's details turn it off and on, or remove it. Scans when first shown, on Refresh,
/// and after each change; nothing runs per tick.
/// </summary>
public sealed class AutostartList : ObservableObject
{
    private const int ShowAll = 0;
    private const int ShowOn = 1;
    private const int ShowOff = 2;
    private const int ShowLook = 3;

    /// <summary>Categories where Windows' own entries are what keeps it starting: they can't be changed here.</summary>
    private static readonly HashSet<AutostartCategory> Critical =
    [
        AutostartCategory.Drivers, AutostartCategory.Winlogon, AutostartCategory.Boot, AutostartCategory.SecurityProviders,
        AutostartCategory.Network, AutostartCategory.Printing, AutostartCategory.LoadedDlls,
    ];

    private readonly AutostartScanner _scanner = new();
    private readonly AutostartRowActions _rowActions;

    // UI thread.
    private readonly Dictionary<string, AutostartRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AutostartCategory, AutostartGroupRow> _groups = [];
    private readonly HashSet<AutostartCategory> _collapsed = [];
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private AutostartSnapshot? _latest;
    private int _show = ShowAll;
    private bool _showWindows;
    private bool _isGrouped = true;
    private string _searchText = string.Empty;
    private AutostartColumn _sortColumn;
    private string _summary = "Reading what starts on its own…";
    private string _notice = string.Empty;
    private AutostartRow? _selected;
    private AutostartDetails? _details;
    private Confirmation? _confirmation;
    private Geometry? _confirmIcon;
    private bool _confirmIsDanger;
    private bool _isEmpty;
    private bool _isLoaded;
    private bool _isScanning;
    private bool _rescan;
    private bool _wasShown;

    internal AutostartList()
    {
        _rowActions = new AutostartRowActions(Select, row => Act(row, AskToggle), row => Act(row, AskRemove),
            row => Act(row, OpenLocation), row => CopyRequested?.Invoke(row.Entry?.Item.Command ?? string.Empty));

        NameColumn = new AutostartColumn("Entry", AutostartSortKey.Name, Sort);
        StatusColumn = new AutostartColumn("Status", AutostartSortKey.Status, Sort);
        PublisherColumn = new AutostartColumn("Publisher", AutostartSortKey.Publisher, Sort);
        CommandColumn = new AutostartColumn("Runs", AutostartSortKey.Command, Sort);
        CategoryColumn = new AutostartColumn("Where", AutostartSortKey.Category, Sort);
        _sortColumn = NameColumn;
        NameColumn.IsActive = true;

        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        RefreshCommand = new RelayCommand(Refresh);
        OpenBackupsCommand = new RelayCommand(OpenBackups);
    }

    /// <summary>The view puts the text on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>Set by the view. The first time the page is shown, it scans.</summary>
    public bool IsShown
    {
        get => _wasShown;
        set
        {
            if (value && !_wasShown)
            {
                _wasShown = true;
                Refresh();
            }
        }
    }

    /// <summary>Category headings and entries, in display order.</summary>
    public ObservableCollection<object> Items { get; } = [];

    public AutostartColumn NameColumn { get; }

    public AutostartColumn StatusColumn { get; }

    public AutostartColumn PublisherColumn { get; }

    public AutostartColumn CommandColumn { get; }

    public AutostartColumn CategoryColumn { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenBackupsCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    /// <summary>Without elevation, machine-wide entries can't be changed and some can't even be read.</summary>
    public bool ShowElevationHint => !Elevation.IsElevated;

    public bool IsShowAll
    {
        get => _show == ShowAll;
        set => SetShow(value, ShowAll);
    }

    public bool IsShowOn
    {
        get => _show == ShowOn;
        set => SetShow(value, ShowOn);
    }

    public bool IsShowOff
    {
        get => _show == ShowOff;
        set => SetShow(value, ShowOff);
    }

    public bool IsShowLook
    {
        get => _show == ShowLook;
        set => SetShow(value, ShowLook);
    }

    /// <summary>Windows' own entries too: hundreds of them, rarely what you came for.</summary>
    public bool ShowWindows
    {
        get => _showWindows;
        set
        {
            if (Set(ref _showWindows, value))
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

    /// <summary>The outcome of an action whose entry has since left the list (removed).</summary>
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

    public string EmptyTitle => HasSearch ? "Nothing matches" : _show == ShowLook ? "Nothing to check" : "Nothing to show";

    public string EmptyHint => HasSearch
        ? "Try another search, or show every entry."
        : _show == ShowLook
            ? "Every file is signed and where it should be."
            : "Try showing every entry, or Windows' own.";

    public AutostartDetails? Details
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

    /// <summary>Removing is destructive (rust); turning off isn't.</summary>
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

    // ---------- Scanning ----------

    public void Refresh()
    {
        if (_isScanning)
        {
            _rescan = true;
            return;
        }

        IsScanning = true;
        if (_isLoaded)
            Summary = "Reading what starts on its own…";

        _ = Task.Run(() =>
        {
            try
            {
                _scanner.Scan(snapshot => Dispatcher.UIThread.Post(() => Apply(snapshot)));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Autostart scan failed: {ex}");
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
                        _rescan = false;
                        Refresh();
                    }
                });
            }
        });
    }

    private void Apply(AutostartSnapshot snapshot)
    {
        _latest = snapshot;
        if (!_isLoaded)
        {
            _isLoaded = true;
            OnPropertyChanged(nameof(IsLoading));
        }

        Render();
    }

    private void SetShow(bool isChecked, int show)
    {
        if (!isChecked || _show == show)
            return;

        _show = show;
        OnPropertyChanged(nameof(IsShowAll));
        OnPropertyChanged(nameof(IsShowOn));
        OnPropertyChanged(nameof(IsShowOff));
        OnPropertyChanged(nameof(IsShowLook));
        Render();
    }

    private void Sort(AutostartColumn column)
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

        var ids = new HashSet<string>(snapshot.Entries.Count, StringComparer.OrdinalIgnoreCase);
        var shown = new List<AutostartRow>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            ids.Add(entry.Item.Id);
            if (!_rows.TryGetValue(entry.Item.Id, out var row))
                _rows[entry.Item.Id] = row = new AutostartRow(entry.Item.Id, _rowActions);

            row.Entry = entry;
            Fill(row, entry);
            if (Matches(entry))
                shown.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
            _rows.Remove(gone);

        shown.Sort(Compare);
        var wanted = new List<object>(shown.Count + 20);
        if (_isGrouped)
        {
            var searching = HasSearch;
            var present = new HashSet<AutostartCategory>();
            foreach (var group in shown.GroupBy(r => r.Entry!.Item.Category).OrderBy(g => g.Key))
            {
                present.Add(group.Key);
                if (!_groups.TryGetValue(group.Key, out var header))
                    _groups[group.Key] = header = new AutostartGroupRow(group.Key, !_collapsed.Contains(group.Key), Toggle);

                var count = group.Count();
                var off = group.Count(r => !r.Entry!.Item.IsEnabled);
                var look = group.Count(r => r.HasLook);
                header.CountText = string.Join(" · ", new[]
                {
                    Plural(count, "entry", "entries"),
                    off > 0 ? $"{Format.Number(off)} off" : null,
                    look > 0 ? $"{Format.Number(look)} to check" : null,
                }.OfType<string>());
                header.HasLook = look > 0;
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
                Select(null);
            else if (_details is { } details && current.Entry is { } entry)
                FillDetails(details, entry);
        }
    }

    private bool Matches(AutostartEntry entry)
    {
        if (!_showWindows && AutostartText.IsWindows(entry) && AutostartText.Look(entry).Length == 0)
            return false;

        switch (_show)
        {
            case ShowOn when !entry.Item.IsEnabled:
            case ShowOff when entry.Item.IsEnabled:
            case ShowLook when AutostartText.Look(entry).Length == 0:
                return false;
        }

        if (_searchText.Length == 0)
            return true;

        var search = _searchText.Trim();
        var item = entry.Item;
        return Contains(item.Name, search)
            || Contains(item.Command, search)
            || Contains(item.Location, search)
            || Contains(AutostartText.CategoryTitle(item.Category), search)
            || Contains(AutostartText.Publisher(entry), search)
            || (entry.File is { } file && (Contains(file.Path, search) || Contains(file.Description ?? string.Empty, search)));
    }

    private static bool Contains(string text, string search) => text.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private int Compare(AutostartRow a, AutostartRow b)
    {
        var result = _sortColumn.Key switch
        {
            AutostartSortKey.Status => b.IsEnabled.CompareTo(a.IsEnabled),
            AutostartSortKey.Publisher => string.Compare(a.PublisherText, b.PublisherText, StringComparison.CurrentCultureIgnoreCase),
            AutostartSortKey.Command => string.Compare(a.CommandText, b.CommandText, StringComparison.CurrentCultureIgnoreCase),
            AutostartSortKey.Category => a.Entry!.Item.Category.CompareTo(b.Entry!.Item.Category),
            _ => 0,
        };

        if (result == 0)
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        if (result == 0)
            result = string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        return _sortColumn.IsDescending ? -result : result;
    }

    private void Toggle(AutostartGroupRow group)
    {
        if (HasSearch)
            return;

        if (!_collapsed.Remove(group.Category))
            _collapsed.Add(group.Category);
        Render();
    }

    private void Fill(AutostartRow row, AutostartEntry entry)
    {
        var item = entry.Item;
        row.Name = item.Name;
        row.IsEnabled = item.IsEnabled;
        row.StatusText = item.IsEnabled ? "On" : "Off";
        row.PublisherText = AutostartText.Publisher(entry);
        row.LookText = AutostartText.Look(entry);
        row.CommandText = item.Command.Length > 0 ? item.Command : "—";
        row.CategoryText = AutostartText.CategoryTitle(item.Category);
        row.ToolTip = item.Command.Length > 0 ? $"{item.Location}\n\n{item.Command}" : item.Location;
        row.HasFile = entry.File is { Exists: true };

        var busy = _busy.Contains(item.Id);
        var locked = LockReason(entry) is not null;
        row.CanToggle = !busy && !locked && item.Toggle is not null;
        row.CanRemove = !busy && !locked && item.Removal is not null;
    }

    private string SummaryOf(AutostartSnapshot snapshot)
    {
        var entries = snapshot.Entries;
        var windows = entries.Count(e => AutostartText.IsWindows(e) && AutostartText.Look(e).Length == 0);
        var off = entries.Count(e => !e.Item.IsEnabled);
        var look = entries.Count(e => AutostartText.Look(e).Length > 0);
        var parts = new List<string> { Plural(entries.Count, "entry", "entries") };
        if (off > 0)
            parts.Add($"{Format.Number(off)} off");
        parts.Add(look > 0 ? $"{Format.Number(look)} to check" : "nothing to check");
        if (!_showWindows && windows > 0)
            parts.Add($"{Format.Number(windows)} of Windows' own hidden");
        if (snapshot.IsChecking || _isScanning)
            parts.Add(snapshot.IsChecking ? "checking signatures…" : "reading…");
        return string.Join(" · ", parts);
    }

    /// <summary>Why it can't be changed here, or null.</summary>
    private static string? LockReason(AutostartEntry entry)
    {
        if (Critical.Contains(entry.Item.Category) && AutostartText.IsWindows(entry))
            return "Part of Windows: changing it can keep Windows from starting, or anyone from signing in.";
        return entry.Item.Toggle is null && entry.Item.Removal is null ? entry.Item.Locked ?? "This can't be changed from here." : null;
    }

    // ---------- Details ----------

    private void Select(AutostartRow? row)
    {
        if (row is not null && ReferenceEquals(row, _selected))
            row = null;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = row;
        if (row?.Entry is not { } entry)
        {
            _selected = null;
            Details = null;
            return;
        }

        row.IsSelected = true;
        Notice = string.Empty;
        var details = new AutostartDetails(row.Id, new AutostartDetailActions(
            () => Select(null), AskToggle, AskRemove, OpenLocation, OpenRegistry,
            () => CopyRequested?.Invoke(_selected?.Entry?.Item.Command ?? string.Empty), OpenBackups));
        FillDetails(details, entry);
        Details = details;
    }

    private void Act(AutostartRow row, Action action)
    {
        if (!ReferenceEquals(row, _selected))
            Select(row);
        if (ReferenceEquals(row, _selected))
            action();
    }

    private void FillDetails(AutostartDetails details, AutostartEntry entry)
    {
        var item = entry.Item;
        details.Name = item.Name;
        details.About = item.About;
        details.IsEnabled = item.IsEnabled;
        details.StatusText = item.IsEnabled ? "On" : "Off";
        details.LookText = AutostartText.Look(entry);
        details.LookExplanation = AutostartText.LookExplanation(entry);
        details.HasFile = entry.File is { Exists: true };
        details.HasRegistry = item.RegistryKey is not null;

        var busy = _busy.Contains(item.Id);
        var locked = LockReason(entry);
        // Locked outright, or (services, packaged apps) only the removal is done elsewhere.
        details.Locked = locked ?? (item.Removal is null ? item.Locked ?? string.Empty : string.Empty);
        details.CanToggle = !busy && locked is null && item.Toggle is not null;
        details.CanRemove = !busy && locked is null && item.Removal is not null;
        details.NeedsElevation = !Elevation.IsElevated && locked is null
            && ((item.Toggle?.NeedsAdmin ?? false) || (item.Removal?.NeedsAdmin ?? false));

        var facts = new List<AutostartFact>
        {
            new("Starts from", AutostartText.CategoryTitle(item.Category)),
            new("Location", item.Location),
        };
        if (item.Command.Length > 0)
            facts.Add(new AutostartFact("Runs", item.Command));
        facts.Add(new AutostartFact("For", item.IsPerUser ? "You only" : "Everyone on this computer"));
        facts.AddRange(item.Facts);
        details.Facts = facts;

        var fileFacts = new List<AutostartFact>();
        if (entry.File is { } file)
        {
            fileFacts.Add(new AutostartFact("Path", file.Path));
            if (!file.Exists)
            {
                fileFacts.Add(new AutostartFact("On disk", "Not found"));
            }
            else
            {
                fileFacts.Add(new AutostartFact("Signature", AutostartText.SignatureText(file.Signature)));
                if (file.Signature is { State: SignatureState.Signed, Signer: { } signer })
                    fileFacts.Add(new AutostartFact("Signed by", signer));
                if (file.Company is { } company)
                    fileFacts.Add(new AutostartFact("Company", file.Signature.State == SignatureState.Signed ? company : $"{company} (as the file says; not verified)"));
                if (file.Description is { } description)
                    fileFacts.Add(new AutostartFact("Description", description));
                if (file.Product is { } product && !string.Equals(product, file.Description, StringComparison.OrdinalIgnoreCase))
                    fileFacts.Add(new AutostartFact("Product", product));
                if (file.Version is { } version)
                    fileFacts.Add(new AutostartFact("Version", version));
                if (file.Modified is { } modified)
                    fileFacts.Add(new AutostartFact("Modified", modified.ToString("g", CultureInfo.CurrentCulture)));
            }
        }

        details.FileFacts = fileFacts;
    }

    private void OpenLocation()
    {
        if (_selected?.Entry?.File is not { Exists: true } file)
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.Path}\""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("Couldn't open the file's folder.", isError: true);
        }
    }

    private void OpenRegistry()
    {
        if (_selected?.Entry?.Item.RegistryKey is { } key && Backups.OpenInRegistryEditor(key) is { } failure)
            Say(failure, isError: true);
    }

    private void OpenBackups()
    {
        try
        {
            Directory.CreateDirectory(AutostartChanges.BackupFolder);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AutostartChanges.BackupFolder}\""));
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

    /// <summary>Turning on never asks. Turning off asks for services, drivers and Windows' own entries, which other things may need.</summary>
    private void AskToggle()
    {
        if (_selected?.Entry is not { Item.Toggle: { } target } entry)
            return;

        var item = entry.Item;
        if (item.IsEnabled && (item.Category is AutostartCategory.Services or AutostartCategory.Drivers || AutostartText.IsWindows(entry)))
        {
            var what = item.Category == AutostartCategory.Drivers ? "driver" : item.Category == AutostartCategory.Services ? "service" : "entry";
            Ask(
                $"Turn off {item.Name}?",
                item.Category is AutostartCategory.Services or AutostartCategory.Drivers
                    ? $"Its startup type becomes Disabled, so it won't start at the next restart, and nothing can start it until it's turned on again here (which restores the startup type it had). If it runs now, it keeps running until then."
                    : "It won't start until it's turned on again here.",
                AutostartText.IsWindows(entry)
                    ? $"This {what} is part of Windows. Other parts of Windows may need it, and stop working without it."
                    : null,
                "Turn off",
                "IconPower",
                danger: false,
                () => SetEnabled(entry, target, false));
            return;
        }

        SetEnabled(entry, target, !item.IsEnabled);
    }

    private void SetEnabled(AutostartEntry entry, AutostartTarget target, bool enabled)
    {
        var name = entry.Item.Name;
        Run(entry.Item.Id, enabled ? $"Turning on {name}…" : $"Turning off {name}…",
            () => AutostartChanges.SetEnabled(target, enabled),
            enabled ? $"{name} is on." : $"{name} is off. Turn it on again to undo.");
    }

    private void AskRemove()
    {
        if (_selected?.Entry is not { Item.Removal: { } target } entry)
            return;

        var item = entry.Item;
        var (how, keeps) = target switch
        {
            FileTarget => ("The file moves to the backups folder", "Move it back to undo."),
            TaskTarget => ("Task Scheduler deletes the task", "Its definition is saved in the backups folder as XML, which Task Scheduler can import."),
            WmiTarget => ("WMI deletes the subscription", "Nothing is saved: note the query and command first if you may want them back."),
            _ => ("The registry entry is deleted", "A .reg file of it is saved in the backups folder: open it to put it back."),
        };
        var file = entry.File is { Exists: true } present && target is not FileTarget
            ? $" The program's files stay on disk ({Path.GetFileName(present.Path)})."
            : string.Empty;
        Ask(
            $"Remove {item.Name}?",
            $"{how}, so it won't start again.{file} {keeps}",
            AutostartText.IsWindows(entry) ? "This is part of Windows. Removing it can break Windows features." : null,
            "Remove",
            "IconTrash",
            danger: true,
            () =>
            {
                string? backup = null;
                Run(item.Id, $"Removing {item.Name}…", () => AutostartChanges.Remove(target, item.Name, out backup, item.Toggle), $"{item.Name} is removed.", ok =>
                {
                    if (!ok)
                        return;

                    // Gone from the list after the rescan: say so above the table, where it stays readable.
                    Notice = backup is null ? $"{item.Name} is removed." : $"{item.Name} is removed. A backup is in {backup}";
                });
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

    /// <summary>Off the UI thread (Task Scheduler, WMI and the SCM can take a moment), then a rescan to show the result.</summary>
    private void Run(string id, string progress, Func<string?> action, string success, Action<bool>? done = null)
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
            string? failure;
            try
            {
                failure = action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Autostart action failed: {ex}");
                failure = "Windows couldn't do that.";
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy.Remove(id);
                if (_details is { } shown && shown.Id == id)
                {
                    shown.IsBusy = false;
                    shown.Message = failure ?? success;
                    shown.MessageIsError = failure is not null;
                }

                done?.Invoke(failure is null);
                Render();
                if (failure is null)
                    Refresh();
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
