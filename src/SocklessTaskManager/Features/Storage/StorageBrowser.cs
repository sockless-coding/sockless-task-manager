using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Storage;

internal enum ItemKind
{
    Volume,
    Folder,
    File,
    SmallerFiles,
    Unaccounted,
}

/// <summary>A row of the current level, and a box of its treemap.</summary>
internal sealed record StorageEntry(string Key, ItemKind Kind, string Name, long Bytes)
{
    public string Note { get; init; } = string.Empty;

    public bool CanOpen { get; init; }

    /// <summary>Where it is on disk, for Show in Explorer.</summary>
    public string? Path { get; init; }

    public FolderNode? Folder { get; init; }

    public VolumeInfo? Volume { get; init; }

    /// <summary>Fraction shown in the share column, where it isn't the share of the level (how full a drive is).</summary>
    public double? Share { get; init; }

    /// <summary>Tooltip text, built when it's shown.</summary>
    public Func<string>? Describe { get; init; }

    /// <summary>What the context menu acts on.</summary>
    public StorageTarget? Target { get; init; }
}

/// <summary>The scans of one drive: the last complete tree, and whatever is reading now.</summary>
internal sealed class VolumeScan(string root)
{
    public string Root { get; } = root;

    public FolderNode? Tree { get; set; }

    /// <summary>Used space when <see cref="Tree"/> was read: what the tree should add up to.</summary>
    public long UsedBytes { get; set; }

    public DateTime CompletedAt { get; set; }

    public TimeSpan Took { get; set; }

    public DiskScan? Running { get; set; }

    /// <summary>The folder <see cref="Running"/> reads again; null when it reads the whole drive.</summary>
    public FolderNode? RunningTarget { get; set; }

    /// <summary>Bytes <see cref="Running"/> should find, for its progress bar.</summary>
    public long RunningExpected { get; set; }

    public string? Error { get; set; }

    public bool Stopped { get; set; }
}

/// <summary>
/// The Storage page's state: the drives, and a path into a drive's scanned folder tree. A drive
/// is scanned the first time it's opened (in the background; the page can be left meanwhile),
/// and folders can be read again on request. Samples (the drives' free space) only while the
/// page is on screen; the tree is only rebuilt into rows when the path or a scan changes.
/// </summary>
public sealed class StorageBrowser : ObservableObject, IDisposable
{
    /// <summary>Boxes drawn in the treemap; the rest are merged into one, so a folder of thousands stays quick to draw.</summary>
    private const int TreemapLimit = 250;

    /// <summary>Boxes inside one box, at most.</summary>
    private const int NestedLimit = 60;

    /// <summary>Nested boxes are drawn down to this fraction of the level (1/1,500).</summary>
    private const double NestedFraction = 1500;

    /// <summary>Largest items named in a folder's tooltip.</summary>
    private const int DescribedParts = 5;

    // Shared between threads.
    private volatile bool _isShown;
    private List<VolumeInfo>? _pendingVolumes;

    // UI thread.
    private List<VolumeInfo> _volumes = [];
    private readonly Dictionary<string, VolumeScan> _scans = new(StringComparer.OrdinalIgnoreCase);
    private VolumeScan? _scan;
    private FolderNode? _folder;

    private string _title = "Storage";
    private string _description = string.Empty;
    private string _totalText = "—";
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _showProgress;
    private double _progress;
    private bool _canStop;
    private bool _canRescan;
    private string _rescanText = "Rescan";
    private bool _canShowInExplorer;
    private bool _offerElevation;
    private StorageTarget? _contextTarget;
    private Confirmation? _confirmation;
    private string _notice = string.Empty;
    private bool _noticeIsError;
    private string? _deleting;
    private IReadOnlyList<TreemapItem> _treemapItems = [];
    private bool _isEmpty;
    private string _emptyText = string.Empty;
    private string _shareHeader = "Share";
    private string _sizeHeader = "On disk";

    public StorageBrowser()
    {
        StopCommand = new RelayCommand(Stop);
        RescanCommand = new RelayCommand(Rescan);
        ShowInExplorerCommand = new RelayCommand(ShowCurrentInExplorer);
        UpCommand = new RelayCommand(Up);
        ContextCommand = new ParameterCommand(target => ContextTarget = target as StorageTarget);
        DismissNoticeCommand = new RelayCommand(() => Notice = string.Empty);
        Render();
    }

    public ObservableCollection<StorageCrumb> Crumbs { get; } = [];

    public bool HasCrumbs => _scan is not null;

    public ObservableCollection<StorageRow> Rows { get; } = [];

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Description
    {
        get => _description;
        private set => Set(ref _description, value);
    }

    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0 || _canRescan || _canStop;

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool ShowProgress
    {
        get => _showProgress;
        private set => Set(ref _showProgress, value);
    }

    /// <summary>0–1: bytes found so far against the drive's used space (or the folder's last size).</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool CanStop
    {
        get => _canStop;
        private set
        {
            if (Set(ref _canStop, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool CanRescan
    {
        get => _canRescan;
        private set
        {
            if (Set(ref _canRescan, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public string RescanText
    {
        get => _rescanText;
        private set => Set(ref _rescanText, value);
    }

    public bool CanShowInExplorer
    {
        get => _canShowInExplorer;
        private set => Set(ref _canShowInExplorer, value);
    }

    /// <summary>Some folders couldn't be read, and running as administrator would read them.</summary>
    public bool OfferElevation
    {
        get => _offerElevation;
        private set => Set(ref _offerElevation, value);
    }

    public IReadOnlyList<TreemapItem> TreemapItems
    {
        get => _treemapItems;
        private set
        {
            if (Set(ref _treemapItems, value))
            {
                OnPropertyChanged(nameof(ShowTreemap));
                OnPropertyChanged(nameof(TableRow));
                OnPropertyChanged(nameof(TableRowSpan));
            }
        }
    }

    public bool ShowTreemap => _treemapItems.Count > 0;

    /// <summary>The table sits below the treemap, or takes its place when there is none.</summary>
    public int TableRow => ShowTreemap ? 1 : 0;

    public int TableRowSpan => ShowTreemap ? 1 : 2;

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => Set(ref _emptyText, value);
    }

    public string ShareHeader
    {
        get => _shareHeader;
        private set => Set(ref _shareHeader, value);
    }

    public string SizeHeader
    {
        get => _sizeHeader;
        private set => Set(ref _sizeHeader, value);
    }

    public ICommand StopCommand { get; }

    public ICommand RescanCommand { get; }

    public ICommand ShowInExplorerCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    /// <summary>Back up one level (Backspace, Alt+Left).</summary>
    public ICommand UpCommand { get; }

    /// <summary>The treemap's right-click: the box the context menu will act on.</summary>
    public ICommand ContextCommand { get; }

    public StorageTarget? ContextTarget
    {
        get => _contextTarget;
        private set => Set(ref _contextTarget, value);
    }

    /// <summary>A delete waiting for a yes.</summary>
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

    /// <summary>The outcome of the last delete, until dismissed or the page moves on.</summary>
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

    public ICommand DismissNoticeCommand { get; }

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    internal Action<string>? CopyText { get; set; }

    /// <summary>Set by the view: the window's handle, so the shell's dialogs open over it.</summary>
    internal Func<nint>? OwnerWindow { get; set; }

    /// <summary>Esc: closes the confirmation, if one is open.</summary>
    internal bool Dismiss()
    {
        if (Confirmation is null)
            return false;

        Confirmation = null;
        return true;
    }

    /// <summary>Set by the view as it enters and leaves the screen.</summary>
    internal bool IsShown
    {
        get => _isShown;
        set
        {
            if (_isShown == value)
                return;

            _isShown = value;
            if (value)
            {
                _ = Task.Run(() =>
                {
                    Sample();
                    Dispatcher.UIThread.Post(Apply);
                });
            }
        }
    }

    /// <summary>Sampling thread: free space changes as files are written, so the drive list stays current.</summary>
    internal void Sample()
    {
        if (_isShown)
            Volatile.Write(ref _pendingVolumes, Volumes.Read());
    }

    /// <summary>UI thread.</summary>
    internal void Apply()
    {
        if (Interlocked.Exchange(ref _pendingVolumes, null) is { } volumes)
        {
            _volumes = volumes;
            if (_scan is null)
                RenderVolumes();
        }

        if (_scan?.Running is not null)
            RenderStatus();
    }

    private void Open(StorageEntry entry)
    {
        if (!entry.CanOpen)
            return;

        if (entry.Volume is { } volume)
        {
            if (!_scans.TryGetValue(volume.Root, out var scan))
                _scans[volume.Root] = scan = new VolumeScan(volume.Root);
            GoTo(scan, scan.Tree);
        }
        else if (entry.Folder is { } folder && _scan is { } current)
        {
            GoTo(current, folder);
        }
    }

    /// <summary>Shows <paramref name="folder"/> of <paramref name="scan"/>, reading what it needs first.</summary>
    private void GoTo(VolumeScan? scan, FolderNode? folder)
    {
        Notice = string.Empty;
        _scan = scan;
        _folder = folder;

        if (scan is { Running: null })
        {
            if (scan.Tree is null && scan.Error is null && !scan.Stopped)
                StartScan(scan, null);
            else if (folder is { IsSummary: true })
                StartScan(scan, folder);
        }

        Render();
    }

    private void Up()
    {
        if (_scan is null)
            return;

        if (_folder?.Parent is { } parent)
            GoTo(_scan, parent);
        else
            GoTo(null, null);
    }

    private void Stop() => _scan?.Running?.Cancel();

    private void Rescan()
    {
        if (_scan is not { Running: null } scan)
            return;

        scan.Error = null;
        scan.Stopped = false;
        StartScan(scan, _folder is { Parent: not null } ? _folder : null);
        Render();
    }

    private void StartScan(VolumeScan scan, FolderNode? target)
    {
        var used = Volume(scan)?.UsedBytes ?? scan.UsedBytes;

        // Folders under about 1/20,000 of the drive's used space keep only their totals: a few
        // thousand folders stay in memory instead of hundreds of thousands. A summary folder is
        // read again in full when it's opened.
        var summaryBelow = target is { IsSummary: true } ? 0 : Math.Max(4L << 20, used / 20_000);
        var disk = new DiskScan(target?.Path ?? scan.Root, summaryBelow);

        scan.Running = disk;
        scan.RunningTarget = target;
        scan.RunningExpected = target?.Bytes ?? used;
        scan.Error = null;
        scan.Stopped = false;
        disk.Start();
        disk.Completion.ContinueWith(
            task => Dispatcher.UIThread.Post(() => Finished(scan, disk, target, used, task)),
            TaskScheduler.Default);
    }

    private void Finished(VolumeScan scan, DiskScan disk, FolderNode? target, long used, Task<FolderNode> task)
    {
        if (!ReferenceEquals(scan.Running, disk))
            return;

        scan.Running = null;
        scan.RunningTarget = null;

        if (task.IsCanceled)
        {
            scan.Stopped = true;
        }
        else if (task.Exception is { } failure)
        {
            scan.Error = failure.InnerException?.Message ?? failure.Message;
        }
        else
        {
            var fresh = task.Result;
            scan.CompletedAt = DateTime.Now;
            scan.Took = scan.CompletedAt - disk.StartedAt;
            if (target is null)
            {
                scan.Tree = fresh;
                scan.UsedBytes = used;
                if (ReferenceEquals(_scan, scan))
                    _folder = Relocate(_folder, fresh);
            }
            else
            {
                Splice(target, fresh);
            }
        }

        if (ReferenceEquals(_scan, scan) || _scan is null)
            Render();
    }

    /// <summary>The folder at the same path in a new tree, or as close to it as still exists.</summary>
    private static FolderNode Relocate(FolderNode? old, FolderNode tree)
    {
        var node = tree;
        if (old is null)
            return node;

        foreach (var step in old.Lineage().Skip(1))
        {
            if (node.Child(step.Name) is not { } next)
                break;
            node = next;
        }

        return node;
    }

    /// <summary>Puts a re-read folder's contents in place of the old ones and corrects the totals above it.</summary>
    private static void Splice(FolderNode target, FolderNode fresh)
    {
        var bytes = fresh.Bytes - target.Bytes;
        var files = fresh.FileCount - target.FileCount;
        var folders = fresh.FolderCount - target.FolderCount;
        var unreadable = fresh.UnreadableCount - target.UnreadableCount;

        foreach (var child in fresh.Folders)
            child.Parent = target;

        target.Folders = fresh.Folders;
        target.Files = fresh.Files;
        target.OtherFileBytes = fresh.OtherFileBytes;
        target.OtherFileCount = fresh.OtherFileCount;
        target.IsSummary = false;
        target.IsUnreadable = fresh.IsUnreadable;
        target.Bytes = fresh.Bytes;
        target.FileCount = fresh.FileCount;
        target.FolderCount = fresh.FolderCount;
        target.UnreadableCount = fresh.UnreadableCount;

        for (var node = target.Parent; node is not null; node = node.Parent)
        {
            node.Bytes += bytes;
            node.FileCount += files;
            node.FolderCount += folders;
            node.UnreadableCount += unreadable;
        }
    }

    private VolumeInfo? Volume(VolumeScan scan) =>
        _volumes.Find(v => string.Equals(v.Root, scan.Root, StringComparison.OrdinalIgnoreCase));

    private void Render()
    {
        RebuildCrumbs();
        if (_scan is not { } scan)
        {
            RenderVolumes();
            RenderStatus();
            return;
        }

        var volume = Volume(scan);
        ShareHeader = "Share";
        SizeHeader = "On disk";

        if (scan.Tree is null || _folder is null)
        {
            Title = volume?.Name ?? scan.Root;
            Description = volume is null
                ? string.Empty
                : $"{Format.Bytes(volume.UsedBytes)} used of {Format.Bytes(volume.TotalBytes)} ({volume.FileSystem}), {Format.Bytes(volume.FreeBytes)} free.";
            TotalText = volume is null ? "—" : Format.Bytes(volume.UsedBytes);
            SetRows([], 0);
            TreemapItems = [];
            IsEmpty = true;
            EmptyText = scan.Running is not null
                ? "Reading every folder on the drive. A large drive takes a minute or two; you can leave this page meanwhile."
                : scan.Error is not null
                    ? "The scan didn't finish. Scan again to try once more."
                    : "Not scanned. Scan to see which folders and files take the space.";
            RenderStatus();
            return;
        }

        var folder = _folder;
        var isRoot = folder.Parent is null;
        var total = isRoot ? Math.Max(scan.UsedBytes, folder.Bytes) : folder.Bytes;
        var level = isRoot ? scan.Root.TrimEnd('\\') : folder.Name;
        var entries = Entries(scan, folder, total, level);
        var basis = Math.Max(total, entries.Sum(e => e.Bytes));

        Title = isRoot ? volume?.Name ?? scan.Root : folder.Name;
        Description = isRoot
            ? $"Everything on {scan.Root.TrimEnd('\\')} by space on disk, as scanned. Select a folder, here or in the map, to see what's in it."
            : $"{folder.Path} · {Counts(folder)}";
        TotalText = Format.Bytes(total);

        SetRows(entries, basis);
        TreemapItems = Map(entries, basis, level);
        IsEmpty = entries.Count == 0;
        EmptyText = folder.IsSummary
            ? scan.Running is not null ? "Reading this folder…" : "Rescan to list what's in this folder."
            : folder.IsUnreadable ? "This folder couldn't be read." : "This folder is empty.";
        RenderStatus();
    }

    /// <param name="basis">The level's total, for shares in tooltips.</param>
    /// <param name="level">The level's short name, for "35% of C:".</param>
    private List<StorageEntry> Entries(VolumeScan scan, FolderNode folder, double basis, string level)
    {
        var entries = new List<StorageEntry>(folder.Folders.Length + folder.Files.Length + 2);
        foreach (var child in folder.Folders)
            entries.Add(FolderEntry(child, basis, level));

        var path = folder.Path;
        foreach (var file in folder.Files)
        {
            var filePath = Path.Join(path, file.Name);
            entries.Add(new StorageEntry($"f:{file.Name}", ItemKind.File, file.Name, file.Bytes)
            {
                Note = Path.GetExtension(file.Name) is { Length: > 1 } extension ? $"{extension.ToLowerInvariant()} file" : "File",
                Path = filePath,
                Describe = () => Describe(ItemKind.File, file.Name, filePath, file.Bytes, null, basis, level),
                Target = new StorageTarget(this, ItemKind.File, file.Name, filePath, file.Bytes, null, folder),
            });
        }

        if (folder.OtherFileCount > 0)
        {
            var name = $"{Format.Number(folder.OtherFileCount)} smaller files";
            var bytes = folder.OtherFileBytes;
            entries.Add(new StorageEntry("files", ItemKind.SmallerFiles, name, bytes)
            {
                Note = "Files here that are each smaller than those listed",
                Describe = () => Describe(ItemKind.SmallerFiles, name, null, bytes, null, basis, level),
                Target = new StorageTarget(this, ItemKind.SmallerFiles, name, null, bytes, null, folder),
            });
        }

        if (folder.Parent is null && scan.UsedBytes > folder.Bytes)
        {
            var bytes = scan.UsedBytes - folder.Bytes;
            entries.Add(new StorageEntry("unaccounted", ItemKind.Unaccounted, "System and unreadable", bytes)
            {
                Note = Elevation.IsElevated
                    ? "File system metadata, restore points and other space Windows doesn't list as files"
                    : "Folders that need administrator rights, restore points and file system metadata",
                Describe = () => Describe(ItemKind.Unaccounted, "System and unreadable", null, bytes, null, basis, level),
                Target = new StorageTarget(this, ItemKind.Unaccounted, "System and unreadable", null, bytes, null, null),
            });
        }

        entries.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return entries;
    }

    private StorageEntry FolderEntry(FolderNode folder, double basis, string level) =>
        new($"d:{folder.Name}", ItemKind.Folder, folder.Name, folder.Bytes)
        {
            Note = folder.IsUnreadable ? "Couldn't be read" : Counts(folder) + LargestNote(folder),
            CanOpen = CanOpen(folder),
            Path = folder.Path,
            Folder = folder,
            Describe = () => Describe(ItemKind.Folder, folder.Name, folder.Path, folder.Bytes, folder, basis, level),
            Target = new StorageTarget(this, ItemKind.Folder, folder.Name, folder.Path, folder.Bytes, folder, folder.Parent),
        };

    private static bool CanOpen(FolderNode folder) => !folder.IsUnreadable || folder.Folders.Length > 0;

    private static string Counts(FolderNode folder)
    {
        var text = $"{Format.Number(folder.FileCount)} {(folder.FileCount == 1 ? "file" : "files")}, "
                   + $"{Format.Number(folder.FolderCount)} {(folder.FolderCount == 1 ? "folder" : "folders")}";
        return folder.UnreadableCount > 0 ? $"{text}, {Format.Number(folder.UnreadableCount)} unreadable" : text;
    }

    /// <summary>" · largest: AppData (98.2 GB)", when the folder holds more than one thing.</summary>
    private static string LargestNote(FolderNode folder)
    {
        if (folder.Folders.Length + folder.Files.Length + (folder.OtherFileCount > 0 ? 1 : 0) < 2)
            return string.Empty;

        var largest = Contents(folder)[0];
        return largest.Kind == ItemKind.SmallerFiles ? string.Empty : $" · largest: {largest.Name} ({Format.Bytes(largest.Bytes)})";
    }

    /// <summary>Something inside a folder: a subfolder, a file, or its smaller files together.</summary>
    private readonly record struct Part(ItemKind Kind, string Name, long Bytes, FolderNode? Folder);

    /// <summary>What a folder holds, largest first.</summary>
    private static List<Part> Contents(FolderNode folder)
    {
        var parts = new List<Part>(folder.Folders.Length + folder.Files.Length + 1);
        foreach (var child in folder.Folders)
            parts.Add(new Part(ItemKind.Folder, child.Name, child.Bytes, child));
        foreach (var file in folder.Files)
            parts.Add(new Part(ItemKind.File, file.Name, file.Bytes, null));
        if (folder.OtherFileCount > 0)
            parts.Add(new Part(ItemKind.SmallerFiles, $"{Format.Number(folder.OtherFileCount)} smaller files", folder.OtherFileBytes, null));
        parts.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return parts;
    }

    /// <summary>
    /// Tooltip text for a row or a box: where it is, its size against the level on screen, and
    /// for a folder, its counts and the largest things in it, so it can be sized up without opening it.
    /// </summary>
    private static string Describe(ItemKind kind, string name, string? path, long bytes, FolderNode? folder, double basis, string level)
    {
        var share = basis > 0 ? bytes / basis : 0;
        var lines = new List<string>
        {
            path ?? name,
            $"{Format.Bytes(bytes)} · {(share is > 0 and < 0.005 ? "<1%" : Format.Percent(share * 100))} of {level}",
        };

        switch (kind)
        {
            case ItemKind.SmallerFiles:
                lines.Add("Files each smaller than those listed by name");
                break;
            case ItemKind.Unaccounted:
                lines.Add("Used space the scan couldn't put a name to");
                break;
        }

        if (folder is null)
            return string.Join('\n', lines);

        lines.Add(folder.IsUnreadable ? "Couldn't be read" : Counts(folder));
        if (folder.IsSummary)
        {
            lines.Add("A small folder: open it to list what's in it.");
        }
        else if (Contents(folder) is { Count: > 0 } parts)
        {
            lines.Add(string.Empty);
            lines.Add("Largest inside:");
            foreach (var part in parts.Take(DescribedParts))
                lines.Add($"  {part.Name} · {Format.Bytes(part.Bytes)}");
            if (parts.Count > DescribedParts)
                lines.Add($"  and {Format.Number(parts.Count - DescribedParts)} more");
        }

        return string.Join('\n', lines);
    }

    private IReadOnlyList<TreemapItem> Map(List<StorageEntry> entries, double basis, string level)
    {
        if (entries.Count == 0 || entries[0].Bytes <= 0)
            return [];

        // Nested boxes smaller than this wouldn't be visible anyway; leaving them out keeps the map quick to draw.
        var cutoff = (long)(basis / NestedFraction);
        var items = new List<TreemapItem>(Math.Min(entries.Count, TreemapLimit + 1));
        long restBytes = 0;
        var restCount = 0;
        foreach (var entry in entries)
        {
            if (entry.Bytes <= 0)
                continue;

            if (items.Count >= TreemapLimit)
            {
                restBytes += entry.Bytes;
                restCount++;
                continue;
            }

            var key = SeriesKey(entry.Kind);
            items.Add(new TreemapItem(
                entry.Name,
                Format.Bytes(entry.Bytes),
                entry.Bytes,
                ThemeResource.Brush(key + "Tint"),
                ThemeResource.Brush(key),
                entry.CanOpen ? new RelayCommand(() => Open(entry)) : null,
                entry.Folder is { } folder ? Nested(folder, cutoff, 1, basis, level) : null,
                entry.Describe,
                entry.Target));
        }

        if (restCount > 0)
        {
            var key = SeriesKey(ItemKind.SmallerFiles);
            items.Add(new TreemapItem(
                $"{Format.Number(restCount)} more",
                Format.Bytes(restBytes),
                restBytes,
                ThemeResource.Brush(key + "Tint"),
                ThemeResource.Brush(key)));
        }

        // Merged items can outweigh the last few listed; the layout wants largest first.
        items.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        return items;
    }

    /// <summary>The boxes inside a folder's box, down to the treemap's depth.</summary>
    private List<TreemapItem>? Nested(FolderNode folder, long cutoff, int depth, double basis, string level)
    {
        if (depth >= Treemap.MaxDepth || folder.IsSummary)
            return null;

        var scan = _scan;
        List<TreemapItem>? items = null;
        foreach (var part in Contents(folder))
        {
            if (part.Bytes < cutoff || part.Bytes <= 0 || items?.Count >= NestedLimit)
                break;

            var key = SeriesKey(part.Kind);
            var child = part.Folder;
            var path = part.Kind == ItemKind.SmallerFiles ? null : child?.Path ?? Path.Join(folder.Path, part.Name);
            (items ??= []).Add(new TreemapItem(
                part.Name,
                Format.Bytes(part.Bytes),
                part.Bytes,
                ThemeResource.Brush(key + "Tint"),
                ThemeResource.Brush(key),
                child is not null && CanOpen(child) ? new RelayCommand(() => GoTo(scan, child)) : null,
                child is not null ? Nested(child, cutoff, depth + 1, basis, level) : null,
                () => Describe(part.Kind, part.Name, path, part.Bytes, child, basis, level),
                new StorageTarget(this, part.Kind, part.Name, path, part.Bytes, child, folder)));
        }

        return items;
    }

    private void RenderVolumes()
    {
        Title = "Storage";
        Description = "Drives on this PC by space used. Open one to scan it and find which folders and files take the space.";
        long used = 0;
        foreach (var volume in _volumes)
            used += volume.UsedBytes;
        TotalText = _volumes.Count == 0 ? "—" : Format.Bytes(used);
        ShareHeader = "Full";
        SizeHeader = "Used";
        TreemapItems = [];

        var entries = _volumes.Select(v => new StorageEntry(v.Root, ItemKind.Volume, v.Name, v.UsedBytes)
        {
            Note = VolumeNote(v),
            CanOpen = true,
            Path = v.Root,
            Volume = v,
            Share = v.TotalBytes > 0 ? (double)v.UsedBytes / v.TotalBytes : 0,
            Target = new StorageTarget(this, ItemKind.Volume, v.Name, v.Root, v.UsedBytes, null, null),
        }).ToList();

        SetRows(entries, 0);
        IsEmpty = entries.Count == 0;
        EmptyText = "No drives to show.";
    }

    private string VolumeNote(VolumeInfo volume)
    {
        var note = $"{volume.FileSystem} · {Format.Bytes(volume.FreeBytes)} free of {Format.Bytes(volume.TotalBytes)}";
        if (!_scans.TryGetValue(volume.Root, out var scan))
            return note;
        if (scan.Running is not null)
            return $"{note} · Scanning…";
        return scan.Tree is not null ? $"{note} · Scanned at {scan.CompletedAt.ToString("t", CultureInfo.CurrentCulture)}" : note;
    }

    private void RenderStatus()
    {
        if (_scan is not { } scan)
        {
            Status = string.Empty;
            IsBusy = false;
            ShowProgress = false;
            CanStop = false;
            CanRescan = false;
            CanShowInExplorer = false;
            OfferElevation = false;
            return;
        }

        CanShowInExplorer = true;
        var drive = scan.Root.TrimEnd('\\');
        if (scan.Running is { } disk)
        {
            var doing = scan.RunningTarget is { } target
                ? $"Reading {target.Name}…"
                : scan.Tree is null ? $"Scanning {drive}…" : $"Scanning {drive} again…";
            var bytes = disk.BytesSoFar;
            Status = $"{doing} {Format.Number(disk.FilesSoFar)} files in {Format.Number(disk.FoldersSoFar)} folders, {Format.Bytes(bytes)} so far.";
            IsBusy = true;
            ShowProgress = scan.RunningExpected > 0;
            Progress = scan.RunningExpected > 0 ? Math.Clamp((double)bytes / scan.RunningExpected, 0, 1) : 0;
            CanStop = true;
            CanRescan = false;
            OfferElevation = false;
            return;
        }

        if (_deleting is { } deleting)
        {
            Status = deleting;
            IsBusy = true;
            ShowProgress = false;
            CanStop = false;
            CanRescan = false;
            OfferElevation = false;
            return;
        }

        IsBusy = false;
        ShowProgress = false;
        CanStop = false;
        CanRescan = true;
        RescanText = scan.Tree is null ? "Scan" : _folder is { Parent: not null } ? "Rescan folder" : "Rescan drive";

        var unreadable = (_folder ?? scan.Tree)?.UnreadableCount ?? 0;
        OfferElevation = unreadable > 0 && !Elevation.IsElevated;

        if (scan.Error is { } error)
        {
            Status = $"The scan failed: {error}";
        }
        else if (scan.Tree is null)
        {
            Status = scan.Stopped ? "Scan stopped before it finished." : string.Empty;
        }
        else
        {
            var text = $"Scanned at {scan.CompletedAt.ToString("T", CultureInfo.CurrentCulture)} in {Duration(scan.Took)}.";
            if (scan.Stopped)
                text += " The last rescan was stopped, so this is the earlier result.";
            if (unreadable > 0)
            {
                text += unreadable == 1 ? " 1 folder couldn't be read" : $" {Format.Number(unreadable)} folders couldn't be read";
                text += Elevation.IsElevated ? "." : "; run as administrator to include them.";
            }

            Status = text;
        }
    }

    private static string Duration(TimeSpan span) =>
        span.TotalSeconds < 60
            ? $"{Math.Max(1, (int)Math.Round(span.TotalSeconds)).ToString(CultureInfo.CurrentCulture)} s"
            : $"{((int)span.TotalMinutes).ToString(CultureInfo.CurrentCulture)} min {span.Seconds.ToString(CultureInfo.CurrentCulture)} s";

    /// <summary>Updates rows in place when the keys are unchanged (the drive list every tick); otherwise rebuilds.</summary>
    private void SetRows(List<StorageEntry> entries, double basis)
    {
        double ShareOf(StorageEntry e) => e.Share ?? (basis > 0 ? e.Bytes / basis : 0);

        var same = Rows.Count == entries.Count;
        for (var i = 0; same && i < entries.Count; i++)
            same = Rows[i].Key == entries[i].Key;

        if (same)
        {
            for (var i = 0; i < entries.Count; i++)
                Rows[i].Update(entries[i], ShareOf(entries[i]));
            return;
        }

        Rows.Clear();
        foreach (var entry in entries)
            Rows.Add(new StorageRow(entry, ShareOf(entry), Open));
    }

    private void RebuildCrumbs()
    {
        Crumbs.Clear();
        Crumbs.Add(new StorageCrumb("Drives", _scan is null, false, () => GoTo(null, null)));
        if (_scan is { } scan)
        {
            var drive = scan.Root.TrimEnd('\\');
            if (_folder is null)
            {
                Crumbs.Add(new StorageCrumb(drive, true, true, () => GoTo(scan, scan.Tree)));
            }
            else
            {
                foreach (var node in _folder.Lineage())
                {
                    var target = node;
                    Crumbs.Add(new StorageCrumb(node.Parent is null ? drive : node.Name, ReferenceEquals(node, _folder), true, () => GoTo(scan, target)));
                }
            }
        }

        OnPropertyChanged(nameof(HasCrumbs));
    }

    private void ShowCurrentInExplorer()
    {
        if (_scan is { } scan)
            Explorer($"\"{_folder?.Path ?? scan.Root}\"");
    }

    /// <summary>Open from a menu: a drive's scan, a folder on this page, a file in its app.</summary>
    internal void Open(StorageTarget target)
    {
        if (!target.CanOpen)
            return;

        switch (target.Kind)
        {
            case ItemKind.Volume when _volumes.Find(v => v.Root == target.Path) is { } volume:
                Open(new StorageEntry(volume.Root, ItemKind.Volume, volume.Name, volume.UsedBytes) { CanOpen = true, Volume = volume });
                break;
            case ItemKind.Folder when target.Folder is { } folder && _scan is { } scan:
                GoTo(scan, folder);
                break;
            case ItemKind.File when target.Path is { } path:
                try
                {
                    using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    ShowNotice($"Couldn't open {target.Name}: {ex.Message}", isError: true);
                }

                break;
        }
    }

    /// <summary>A folder or drive opens in Explorer; a file is selected in its folder.</summary>
    internal void ShowInExplorer(StorageTarget target)
    {
        if (target.Path is { } path)
            Explorer(target.Kind == ItemKind.File ? $"/select,\"{path}\"" : $"\"{path}\"");
    }

    internal void CopyPath(StorageTarget target)
    {
        if (target.Path is { } path)
            CopyText?.Invoke(path);
    }

    internal void RescanFolder(StorageTarget target)
    {
        if (target.Folder is not { } folder || _scan is not { Running: null } scan || _deleting is not null)
            return;

        Notice = string.Empty;
        scan.Error = null;
        scan.Stopped = false;
        StartScan(scan, folder.Parent is null ? null : folder);
        Render();
    }

    internal void ShowProperties(StorageTarget target)
    {
        if (target.Path is { } path && !StorageNative.ShowProperties(path, OwnerWindow?.Invoke() ?? 0))
            ShowNotice($"Windows couldn't show the properties of {target.Name}.", isError: true);
    }

    /// <summary>Asks before deleting (components.md → Dialogs): names what goes, how much, and whether it can come back.</summary>
    internal void AskDelete(StorageTarget target, bool permanently)
    {
        if (target.Path is not { } path || target.Kind is not (ItemKind.Folder or ItemKind.File))
            return;

        if (target.Protection is { } reason)
        {
            ShowNotice($"{target.Name} can't be deleted here. {reason}", isError: true);
            return;
        }

        if (!target.CanDelete)
            return;

        if (_deleting is not null || _scan?.Running is not null)
        {
            ShowNotice("Wait for the scan or delete in progress to finish first.", isError: true);
            return;
        }

        var what = target.Folder is { } folder
            ? $"{target.Name} and everything in it ({Counts(folder)}, {Format.Bytes(target.Bytes)})"
            : $"{target.Name} ({Format.Bytes(target.Bytes)})";
        Confirmation = new Confirmation(
            permanently ? $"Delete {target.Name} permanently?" : $"Delete {target.Name}?",
            permanently
                ? $"{what} will be deleted for good. It doesn't go to the Recycle Bin and can't be restored.\n\n{path}"
                : $"{what} moves to the Recycle Bin, where you can restore it until the bin is emptied. The space is freed when it is.\n\n{path}",
            target.Risk(),
            permanently ? "Delete permanently" : "Move to Recycle Bin",
            () =>
            {
                Confirmation = null;
                Delete(target, permanently);
            },
            () => Confirmation = null);
    }

    private async void Delete(StorageTarget target, bool permanently)
    {
        var path = target.Path!;
        var scan = _scan;
        _deleting = $"Deleting {target.Name}…";
        Notice = string.Empty;
        RenderStatus();

        int result;
        bool cancelled;
        try
        {
            (result, cancelled) = await StorageNative.DeleteAsync(path, permanently, OwnerWindow?.Invoke() ?? 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Delete failed: {ex}");
            (result, cancelled) = (-1, false);
        }

        _deleting = null;

        if (!Path.Exists(path))
        {
            Remove(scan, target);
            ShowNotice(permanently
                ? $"Deleted {target.Name}, freeing {Format.Bytes(target.Bytes)}."
                : $"Moved {target.Name} ({Format.Bytes(target.Bytes)}) to the Recycle Bin. Empty the bin to free the space.", isError: false);
            Render();
            return;
        }

        // Some or all of it is still there: files in use, access denied, or stopped at the shell's prompt.
        ShowNotice(cancelled
            ? $"Deleting {target.Name} was stopped; what's left of it is still there."
            : $"Windows couldn't delete all of {target.Name} (error 0x{result:x}). Files that are in use or protected stay where they were.", isError: !cancelled);

        // Read a partly deleted folder again, so the page shows what's left.
        if (target.Folder is { } folder && scan is { Running: null } && ReferenceEquals(scan, _scan))
            StartScan(scan, folder.Parent is null ? null : folder);
        Render();
    }

    /// <summary>Takes a deleted folder or file out of the tree and its size out of every total above it.</summary>
    private void Remove(VolumeScan? scan, StorageTarget target)
    {
        if (target.Parent is not { } parent)
            return;

        long bytes = target.Bytes, files = 1, folders = 0, unreadable = 0;
        if (target.Folder is { } folder)
        {
            parent.Folders = [.. parent.Folders.Where(f => !ReferenceEquals(f, folder))];
            bytes = folder.Bytes;
            files = folder.FileCount;
            folders = folder.FolderCount + 1;
            unreadable = folder.UnreadableCount;

            // The page was inside what's gone: show the folder it was in.
            if (_folder is not null && _folder.Lineage().Contains(folder))
                _folder = parent;
        }
        else
        {
            var index = Array.FindIndex(parent.Files, f => f.Name == target.Name);
            if (index >= 0)
                parent.Files = [.. parent.Files.Where((_, i) => i != index)];
        }

        for (var node = parent; node is not null; node = node.Parent)
        {
            node.Bytes -= bytes;
            node.FileCount -= files;
            node.FolderCount -= folders;
            node.UnreadableCount -= unreadable;
        }

        // Keeps "System and unreadable" from growing by what was deleted (a recycled item is
        // counted again once the drive is rescanned, under $Recycle.Bin).
        if (scan is not null)
            scan.UsedBytes = Math.Max(0, scan.UsedBytes - bytes);
    }

    private void ShowNotice(string text, bool isError)
    {
        NoticeIsError = isError;
        Notice = text;
    }

    private static void Explorer(string arguments)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", arguments));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Couldn't open Explorer: {ex.Message}");
        }
    }

    internal static string SeriesKey(ItemKind kind) => kind switch
    {
        ItemKind.File => "SlSeries4",
        ItemKind.SmallerFiles => "SlSeries3",
        ItemKind.Unaccounted => "SlSeriesIdle",
        _ => "SlSeries1",
    };

    public void Dispose()
    {
        foreach (var scan in _scans.Values)
            scan.Running?.Cancel();
    }
}

/// <summary>One row of the table: a drive, folder, file, or the smaller files of a folder.</summary>
public sealed class StorageRow : ObservableObject
{
    private StorageEntry _entry;
    private double _share;

    internal StorageRow(StorageEntry entry, double share, Action<StorageEntry> open)
    {
        _entry = entry;
        _share = share;
        Series = ThemeResource.Brush(StorageBrowser.SeriesKey(entry.Kind));
        OpenCommand = new RelayCommand(() => open(_entry));
    }

    public string Key => _entry.Key;

    public string Name => _entry.Name;

    public string Note => _entry.Note;

    public bool HasNote => _entry.Note.Length > 0;

    public string SizeText => Format.Bytes(_entry.Bytes);

    /// <summary>0–1.</summary>
    public double Share => _share;

    public string ShareText => _share is > 0 and < 0.005 ? "<1%" : Format.Percent(_share * 100);

    /// <summary>Swatch and share bar, matching the item's treemap colour.</summary>
    public IBrush Series { get; }

    public bool CanOpen => _entry.CanOpen;

    public bool CanReveal => _entry.Path is not null;

    /// <summary>What the row's context menu, Show in Explorer button and Delete keys act on.</summary>
    public StorageTarget? Target => _entry.Target;

    public string? ToolTip => _entry.Describe?.Invoke() ?? _entry.Path;

    public string AccessibleName => $"{Name}, {SizeText}";

    public ICommand OpenCommand { get; }

    internal void Update(StorageEntry entry, double share)
    {
        var old = _entry;
        _entry = entry;
        if (!ReferenceEquals(old.Target, entry.Target))
            OnPropertyChanged(nameof(Target));
        if (old.Note != entry.Note)
            OnPropertyChanged(nameof(Note));
        if (old.Bytes != entry.Bytes)
        {
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(AccessibleName));
        }

        if (_share != share)
        {
            _share = share;
            OnPropertyChanged(nameof(Share));
            OnPropertyChanged(nameof(ShareText));
        }
    }
}

/// <summary>A breadcrumb: every level above the current one is a link back to it.</summary>
public sealed class StorageCrumb
{
    internal StorageCrumb(string name, bool isCurrent, bool showSeparator, Action navigate)
    {
        Name = name;
        IsCurrent = isCurrent;
        ShowSeparator = showSeparator;
        NavigateCommand = new RelayCommand(navigate);
    }

    public string Name { get; }

    public bool IsCurrent { get; }

    public bool IsLink => !IsCurrent;

    public bool ShowSeparator { get; }

    public ICommand NavigateCommand { get; }

    public override string ToString() => Name;
}

/// <summary>A command that passes its parameter on (RelayCommand drops it).</summary>
internal sealed class ParameterCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
