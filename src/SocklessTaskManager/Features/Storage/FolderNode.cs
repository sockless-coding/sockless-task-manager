namespace Sockless.TaskManager.Features.Storage;

/// <summary>A file kept by name: one of the largest in its folder.</summary>
internal readonly record struct FileLeaf(string Name, long Bytes);

/// <summary>
/// A folder from a scan. Sizes are space on disk (allocation sizes) of everything beneath it.
/// Written by the scan's worker threads until the scan completes; after that only the UI
/// thread touches it (to splice in a folder that was read again).
/// </summary>
internal sealed class FolderNode(string name, FolderNode? parent)
{
    public string Name { get; } = name;

    /// <summary>Set again when a re-read folder's children are moved into the tree.</summary>
    public FolderNode? Parent { get; internal set; } = parent;

    /// <summary>Space on disk of everything beneath it.</summary>
    public long Bytes;

    /// <summary>Files beneath it, at any depth.</summary>
    public long FileCount;

    /// <summary>Folders beneath it, at any depth.</summary>
    public long FolderCount;

    /// <summary>Folders beneath it (or this one) that couldn't be listed.</summary>
    public long UnreadableCount;

    /// <summary>This folder couldn't be listed (access denied, or it went away).</summary>
    public bool IsUnreadable;

    /// <summary>
    /// Small enough that its contents were dropped once counted, to keep the tree small. Opening
    /// it reads it again, which is quick because it is small.
    /// </summary>
    public bool IsSummary;

    public FolderNode[] Folders = [];

    /// <summary>The largest files directly in this folder, largest first.</summary>
    public FileLeaf[] Files = [];

    /// <summary>Files directly in this folder that aren't kept by name.</summary>
    public long OtherFileBytes;

    public long OtherFileCount;

    // Scan bookkeeping.
    internal long OwnFileBytes;
    internal long OwnFileCount;
    internal int Pending;

    public string Path => Parent is null ? Name : System.IO.Path.Join(Parent.Path, Name);

    /// <summary>The parent chain, from the scan root down to this folder.</summary>
    public List<FolderNode> Lineage()
    {
        var chain = new List<FolderNode>();
        for (var node = this; node is not null; node = node.Parent)
            chain.Add(node);
        chain.Reverse();
        return chain;
    }

    /// <summary>The direct child named <paramref name="name"/>, if it is still there.</summary>
    public FolderNode? Child(string name)
    {
        foreach (var folder in Folders)
        {
            if (string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase))
                return folder;
        }

        return null;
    }
}
