using System.Collections.Concurrent;

namespace Sockless.TaskManager.Features.Storage;

/// <summary>
/// Reads a folder tree on a few below-normal-priority threads, depth first from a shared stack.
/// Each folder is listed once (<see cref="DirectoryListing"/>); its size is summed when the last
/// of its subfolders is done, and a folder smaller than the summary threshold drops its contents
/// right then, so the tree held in memory stays small even for a disk of millions of files.
/// Junctions and other reparse-point folders aren't followed. Hard-linked files (common under
/// Windows) count in every folder that links them, as Explorer counts them.
/// </summary>
internal sealed class DiskScan
{
    /// <summary>Files kept by name per folder; the rest are counted as smaller files.</summary>
    private const int KeptFiles = 50;
    private const int BufferBytes = 64 * 1024;

    private readonly FolderNode _root;
    private readonly long _summaryBelow;
    private readonly int _threads = Math.Clamp(Environment.ProcessorCount, 2, 8);
    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentStack<(FolderNode Node, string Path)> _work = new();
    private readonly SemaphoreSlim _ready = new(0);
    private readonly TaskCompletionSource<FolderNode> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;
    private volatile bool _finished;
    private long _files;
    private long _folders;
    private long _bytes;

    /// <param name="path">The folder to read: a volume root ("C:\") or a folder in it.</param>
    /// <param name="summaryBelow">Folders smaller than this keep only their totals. Zero keeps everything.</param>
    public DiskScan(string path, long summaryBelow)
    {
        Path = path;
        _summaryBelow = summaryBelow;
        _root = new FolderNode(path.Length <= 3 ? path : System.IO.Path.GetFileName(path.TrimEnd('\\')), null);
    }

    public string Path { get; }

    public DateTime StartedAt { get; private set; }

    /// <summary>The scanned tree, once every folder has been read. Cancelled by <see cref="Cancel"/>.</summary>
    public Task<FolderNode> Completion => _done.Task;

    public long FilesSoFar => Interlocked.Read(ref _files);

    public long FoldersSoFar => Interlocked.Read(ref _folders);

    public long BytesSoFar => Interlocked.Read(ref _bytes);

    public void Start()
    {
        StorageNative.EnableBackupPrivilege();
        StartedAt = DateTime.Now;
        _running = _threads;
        Push(_root, Path);
        for (var i = 0; i < _threads; i++)
        {
            new Thread(Work)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "Storage scan",
            }.Start();
        }
    }

    public void Cancel()
    {
        if (_finished)
            return;

        _cancel.Cancel();
        _ready.Release(_threads);
    }

    private void Push(FolderNode node, string path)
    {
        _work.Push((node, path));
        _ready.Release();
    }

    private void Work()
    {
        var buffer = new byte[BufferBytes];
        var largest = new PriorityQueue<FileLeaf, long>(KeptFiles + 1);
        var folders = new List<FolderNode>();
        try
        {
            while (true)
            {
                _ready.Wait();
                if (_finished || _cancel.IsCancellationRequested)
                    break;

                // Every push releases the semaphore once, so an item is waiting.
                if (_work.TryPop(out var item))
                    Read(item.Node, item.Path, buffer, largest, folders);
            }
        }
        catch (Exception ex)
        {
            _done.TrySetException(ex);
            _cancel.Cancel();
            _ready.Release(_threads);
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0 && !_finished)
                _done.TrySetCanceled();
        }
    }

    private void Read(FolderNode node, string path, byte[] buffer, PriorityQueue<FileLeaf, long> largest, List<FolderNode> folders)
    {
        folders.Clear();
        largest.Clear();
        long ownBytes = 0, ownFiles = 0;

        if (DirectoryListing.TryOpen(path, buffer, out var listing))
        {
            using (listing)
            {
                while (listing.MoveNext())
                {
                    if (listing.IsDirectory)
                    {
                        if (!listing.IsReparsePoint)
                            folders.Add(new FolderNode(new string(listing.Name), node));
                        continue;
                    }

                    var bytes = listing.AllocatedBytes;
                    ownBytes += bytes;
                    ownFiles++;

                    // Name strings are only made for files that make the cut so far.
                    if (largest.Count < KeptFiles)
                        largest.Enqueue(new FileLeaf(new string(listing.Name), bytes), bytes);
                    else if (largest.TryPeek(out _, out var smallest) && bytes > smallest)
                        largest.EnqueueDequeue(new FileLeaf(new string(listing.Name), bytes), bytes);
                }
            }
        }
        else
        {
            node.IsUnreadable = true;
        }

        var kept = new FileLeaf[largest.Count];
        long keptBytes = 0;
        for (var i = kept.Length - 1; i >= 0; i--)
        {
            kept[i] = largest.Dequeue();
            keptBytes += kept[i].Bytes;
        }

        node.Files = kept;
        node.OwnFileBytes = ownBytes;
        node.OwnFileCount = ownFiles;
        node.OtherFileBytes = ownBytes - keptBytes;
        node.OtherFileCount = ownFiles - kept.Length;
        node.Folders = [.. folders];

        Interlocked.Add(ref _files, ownFiles);
        Interlocked.Add(ref _bytes, ownBytes);
        Interlocked.Add(ref _folders, folders.Count);

        // Count this read as one outstanding part, so children finishing first can't complete it early.
        node.Pending = node.Folders.Length + 1;
        foreach (var child in node.Folders)
            Push(child, System.IO.Path.Join(path, child.Name));

        Finish(node);
    }

    /// <summary>One part of <paramref name="node"/> is done; when all are, total it and tell its parent.</summary>
    private void Finish(FolderNode node)
    {
        while (Interlocked.Decrement(ref node.Pending) == 0)
        {
            Total(node);
            if (ReferenceEquals(node, _root))
            {
                _finished = true;
                _done.TrySetResult(_root);
                _ready.Release(_threads);
                return;
            }

            node = node.Parent!;
        }
    }

    private void Total(FolderNode node)
    {
        long bytes = node.OwnFileBytes, files = node.OwnFileCount, folders = node.Folders.Length;
        long unreadable = node.IsUnreadable ? 1 : 0;
        foreach (var child in node.Folders)
        {
            bytes += child.Bytes;
            files += child.FileCount;
            folders += child.FolderCount;
            unreadable += child.UnreadableCount;
        }

        node.Bytes = bytes;
        node.FileCount = files;
        node.FolderCount = folders;
        node.UnreadableCount = unreadable;

        if (!ReferenceEquals(node, _root) && bytes < _summaryBelow && (node.Folders.Length > 0 || node.Files.Length > 0))
        {
            node.Folders = [];
            node.Files = [];
            node.OtherFileBytes = 0;
            node.OtherFileCount = 0;
            node.IsSummary = true;
        }
    }
}
