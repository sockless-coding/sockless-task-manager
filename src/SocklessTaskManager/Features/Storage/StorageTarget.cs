using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Storage;

/// <summary>
/// What a context menu acts on: a drive, folder or file from a table row or a treemap box. The
/// smaller-files and unaccounted items have no path, so everything but nothing is disabled on them.
/// </summary>
public sealed class StorageTarget
{
    internal StorageTarget(StorageBrowser browser, ItemKind kind, string name, string? path, long bytes, FolderNode? folder, FolderNode? parent)
    {
        Kind = kind;
        Name = name;
        Path = path;
        Bytes = bytes;
        Folder = folder;
        Parent = parent;
        Protection = path is null ? null : Protect(kind, path);

        OpenCommand = new RelayCommand(() => browser.Open(this));
        ExplorerCommand = new RelayCommand(() => browser.ShowInExplorer(this));
        CopyPathCommand = new RelayCommand(() => browser.CopyPath(this));
        RescanCommand = new RelayCommand(() => browser.RescanFolder(this));
        PropertiesCommand = new RelayCommand(() => browser.ShowProperties(this));
        DeleteCommand = new RelayCommand(() => browser.AskDelete(this, permanently: false));
        DeletePermanentlyCommand = new RelayCommand(() => browser.AskDelete(this, permanently: true));
    }

    internal ItemKind Kind { get; }

    public string Name { get; }

    internal string? Path { get; }

    internal long Bytes { get; }

    /// <summary>The folder itself, for a folder.</summary>
    internal FolderNode? Folder { get; }

    /// <summary>The folder it's in, for a folder or file: where it's taken out after a delete.</summary>
    internal FolderNode? Parent { get; }

    /// <summary>Why it can't be deleted from here, or null.</summary>
    internal string? Protection { get; }

    public bool HasPath => Path is not null;

    public bool IsFolder => Kind == ItemKind.Folder;

    /// <summary>A folder opens on this page; a file opens in its app; a drive scans.</summary>
    public bool CanOpen => Kind switch
    {
        ItemKind.Folder => Folder is { IsUnreadable: false } || Folder?.Folders.Length > 0,
        ItemKind.File or ItemKind.Volume => true,
        _ => false,
    };

    public string OpenText => Kind == ItemKind.File ? "Open file" : "Open";

    /// <summary>A folder or drive opens in Explorer; a file is selected in its folder.</summary>
    public string ExplorerText => Kind == ItemKind.File ? "Show in File Explorer" : "Open in File Explorer";

    public bool CanDelete => Kind is ItemKind.Folder or ItemKind.File && Path is not null && Protection is null && Parent is not null;

    public ICommand OpenCommand { get; }

    public ICommand ExplorerCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand RescanCommand { get; }

    public ICommand PropertiesCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand DeletePermanentlyCommand { get; }

    /// <summary>
    /// Folders Windows or every app needs. Deleting one breaks the system rather than freeing
    /// space usefully; Settings (Storage, Apps) and Disk Cleanup are the way to shrink them.
    /// </summary>
    private static string? Protect(ItemKind kind, string path)
    {
        if (kind == ItemKind.Volume)
            return "A whole drive can't be deleted.";
        if (kind != ItemKind.Folder)
            return null;

        var full = path.TrimEnd('\\');
        if (full.Length <= 2)
            return "A whole drive can't be deleted.";

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] system =
        [
            windows,
            System.IO.Path.Join(windows, "System32"),
            System.IO.Path.Join(windows, "SysWOW64"),
            System.IO.Path.Join(windows, "WinSxS"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        foreach (var folder in system)
        {
            if (folder.Length > 0 && string.Equals(full, folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return "Windows and installed apps need this folder. Use Settings › Storage or Disk Cleanup to free space in it.";
        }

        var users = System.IO.Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (users is not null && (string.Equals(full, users, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(System.IO.Path.GetDirectoryName(full), users, StringComparison.OrdinalIgnoreCase)))
            return "This holds user profiles. Remove an account in Settings › Accounts instead.";

        return null;
    }

    /// <summary>A warning for the confirmation, when deleting it is allowed but risky.</summary>
    internal string? Risk()
    {
        if (Path is null)
            return null;

        static bool Under(string path, Environment.SpecialFolder root)
        {
            var folder = Environment.GetFolderPath(root);
            return folder.Length > 0 && path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        if (Under(Path, Environment.SpecialFolder.Windows))
            return "This is part of Windows. Deleting it can stop Windows or its features from working.";
        if (Under(Path, Environment.SpecialFolder.ProgramFiles) || Under(Path, Environment.SpecialFolder.ProgramFilesX86))
            return "An installed app may stop working. Uninstalling it from Settings › Apps removes it cleanly.";
        if (Under(Path, Environment.SpecialFolder.CommonApplicationData))
            return "Apps keep shared settings and data here; one may stop working without it.";
        return null;
    }
}
