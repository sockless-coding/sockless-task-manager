using System.Globalization;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>
/// The breakdown hierarchy. A path of entry keys (["inuse", "kernel", "paged"]) resolves to the
/// level it shows, built fresh from the latest sample each tick:
/// <code>
/// Physical memory
///   In use      Processes › process group › process › programs and DLLs | mapped files
///               Compressed memory
///               Shared files and other › files mapped by processes
///               Kernel and drivers › non-paged | paged pool › tag › drivers with that tag
///                                  › driver code › loaded drivers
///               System file cache
///   Modified    for the page file, for files, not to be written
///   Standby     by priority
///   Free        zeroed, free, bad
/// </code>
/// </summary>
internal static class BreakdownTree
{
    public static BreakdownNeeds Needs(string[] path) => path switch
    {
        ["inuse", "kernel", "drivers", ..] => BreakdownNeeds.InUse | BreakdownNeeds.Modules,
        ["inuse", "kernel", "paged" or "nonpaged", ..] => BreakdownNeeds.InUse | BreakdownNeeds.Tags,
        ["inuse", ..] => BreakdownNeeds.InUse,
        _ => BreakdownNeeds.Lists,
    };

    /// <summary>The level at <paramref name="path"/>, or null if what it pointed at is gone (a process exited).</summary>
    public static Level? Resolve(string[] path, BreakdownSample s, Scans scans) => path switch
    {
        [] => Root(s),
        ["inuse"] => InUse(s),
        ["inuse", "processes"] => Processes(s),
        ["inuse", "processes", var group] when group.StartsWith("g:", StringComparison.Ordinal) => Group(s, group[2..]),
        ["inuse", "processes", .., var pid] when ProcessId(pid) is { } id => Process(s, id, scans),
        ["inuse", "processes", .., var pid, var files] when ProcessId(pid) is { } id => ProcessFiles(s, id, files, scans),
        ["inuse", "shared"] => SharedFiles(s, scans),
        ["inuse", "kernel"] => Kernel(s),
        ["inuse", "kernel", "drivers"] => Drivers(s),
        ["inuse", "kernel", "paged" or "nonpaged"] => Pool(s, path[2] == "paged"),
        ["inuse", "kernel", "paged" or "nonpaged", var tag] => TagDrivers(s, tag, scans),
        ["modified"] => Modified(s),
        ["standby"] => Standby(s),
        ["free"] => Free(s),
        _ => null,
    };

    private static Level Root(BreakdownSample s)
    {
        var lists = s.Lists;
        return new Level(
            "Physical memory",
            $"All {Format.Bytes(s.TotalBytes)} of RAM Windows can use, by what it's holding right now. Select a row to see what it's made of.",
            s.TotalBytes,
            [
                new("inuse", "In use", s.InUseBytes)
                {
                    Note = "Processes, drivers and the kernel",
                    SeriesKey = "SlSeries1",
                    CanOpen = true,
                },
                new("modified", "Modified", lists.AllModifiedBytes)
                {
                    Note = "Changed data waiting to be written to disk",
                    SeriesKey = "SlSeries3",
                    CanOpen = true,
                },
                new("standby", "Standby", lists.StandbyBytes)
                {
                    Note = "Cached files and data, handed out when needed",
                    SeriesKey = "SlSeries4",
                    CanOpen = true,
                },
                new("free", "Free", lists.UnusedBytes + lists.BadBytes)
                {
                    Note = "Not holding anything",
                    SeriesKey = "SlSeriesIdle",
                    CanOpen = true,
                },
            ])
        {
            Sorted = false,
            ShowComposition = true,
        };
    }

    /// <summary>"In use" split into what can be attributed; the rest is shared or unattributed.</summary>
    private readonly record struct InUseParts(ulong Processes, int ProcessCount, ulong Compressed, ulong Kernel, ulong FileCache, ulong Shared);

    private static InUseParts? Split(BreakdownSample s)
    {
        if (s.Kernel is not { } k || s.Processes is not { } processes)
            return null;

        ulong privateSum = 0, compressed = 0;
        var count = 0;
        foreach (var p in processes)
        {
            if (p.Name == ProcessMemory.CompressionName)
            {
                compressed += p.WorkingSet;
                continue;
            }

            privateSum += p.PrivateWorkingSet;
            count++;
        }

        var kernel = k.PagedPoolBytes + k.NonPagedPoolBytes + k.DriverCodeBytes + k.KernelCodeBytes;
        var attributed = privateSum + compressed + kernel + k.FileCacheBytes;
        var inUse = s.InUseBytes;
        return new InUseParts(privateSum, count, compressed, kernel, k.FileCacheBytes, inUse > attributed ? inUse - attributed : 0);
    }

    private static Level InUse(BreakdownSample s)
    {
        if (Split(s) is not { } parts)
            return Pending("In use", s.InUseBytes);

        return new Level(
            "In use",
            "Memory held by processes, drivers and the kernel. It can't be handed to anything else until its owner frees it or Windows moves it to disk.",
            s.InUseBytes,
            [
                new("processes", "Processes", parts.Processes)
                {
                    Note = $"Private memory of {Format.Number(parts.ProcessCount)} processes",
                    CanOpen = true,
                },
                new("compressed", "Compressed memory", parts.Compressed)
                {
                    Note = "Pages squeezed into the compression store to save RAM",
                },
                new("shared", "Shared files and other", parts.Shared)
                {
                    Note = "Shared DLLs and files, driver-locked memory, page tables",
                    CanOpen = true,
                },
                new("kernel", "Kernel and drivers", parts.Kernel)
                {
                    Note = "Kernel pools, driver code and kernel code",
                    CanOpen = true,
                },
                new("filecache", "System file cache", parts.FileCache)
                {
                    Note = "File data the system is actively reading and writing",
                },
            ]);
    }

    private static Level Processes(BreakdownSample s)
    {
        if (Split(s) is not { } parts || s.Processes is not { } processes)
            return Pending("Processes", 0);

        var entries = new List<Entry>();
        foreach (var group in processes
                     .Where(p => p.Name != ProcessMemory.CompressionName)
                     .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                entries.Add(ProcessEntry(members[0], members[0].Name));
                continue;
            }

            entries.Add(new Entry($"g:{group.Key.ToLowerInvariant()}", members[0].Name, Sum(members, p => p.PrivateWorkingSet))
            {
                Note = $"{Format.Number(members.Count)} processes · {Format.Bytes(Sum(members, p => p.Commit))} committed",
                CanOpen = true,
            });
        }

        return new Level(
            "Processes",
            "Private working sets: the RAM each process uses on its own. DLLs and files it shares with others are under Shared files and other. Committed is private memory in RAM or in the page file.",
            parts.Processes,
            entries);
    }

    private static Level? Group(BreakdownSample s, string name)
    {
        if (s.Processes is not { } processes)
            return Pending(name, 0);

        var members = processes.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (members.Count == 0)
            return null;

        return new Level(
            members[0].Name,
            $"{Format.Number(members.Count)} processes running {members[0].Name}. Select one to see what its memory is made of.",
            Sum(members, p => p.PrivateWorkingSet),
            [.. members.Select(p => ProcessEntry(p, $"PID {p.Id.ToString(CultureInfo.CurrentCulture)}"))]);
    }

    private static Entry ProcessEntry(ProcessUsage p, string name) =>
        new($"p:{p.Id.ToString(CultureInfo.InvariantCulture)}", name, p.PrivateWorkingSet)
        {
            Note = name.StartsWith("PID ", StringComparison.Ordinal)
                ? $"{Format.Bytes(p.Commit)} committed"
                : $"PID {p.Id.ToString(CultureInfo.CurrentCulture)} · {Format.Bytes(p.Commit)} committed",
            CanOpen = true,
        };

    private static Level? Process(BreakdownSample s, int id, Scans scans)
    {
        if (s.Processes is not { } processes)
            return Pending("Process", 0);
        if (processes.FindIndex(p => p.Id == id) is var index && index < 0)
            return null;

        var process = processes[index];
        var key = $"space:{id.ToString(CultureInfo.InvariantCulture)}";
        var scan = scans.Get(key, () => AddressSpace.Scan(id));
        var level = new Level(
            process.Name,
            $"PID {id.ToString(CultureInfo.CurrentCulture)}. Everything this process has in RAM, including DLLs and files other processes may share. "
            + $"Its private part, {Format.Bytes(process.PrivateWorkingSet)}, is what it adds under Processes.",
            process.WorkingSet,
            []) { ScanKey = key };

        if (scan.Result is not { } space)
            return WithScanStatus(level, scan, "Reading the address space…");

        return WithScanStatus(level with
        {
            Bytes = space.WorkingSetBytes,
            Entries =
            [
                new("private", "Private data", space.PrivateBytes)
                {
                    Note = "Heaps, stacks and other memory only this process uses",
                },
                new("images", "Programs and DLLs", space.ImageBytes)
                {
                    Note = $"{Format.Number(space.Images.Count)} EXE and DLL files",
                    CanOpen = space.Images.Count > 0,
                },
                new("mapped", "Mapped files", space.MappedFileBytes)
                {
                    Note = $"{Format.Number(space.MappedFiles.Count)} data files mapped into memory",
                    CanOpen = space.MappedFiles.Count > 0,
                },
                new("sharedmemory", "Shared memory", space.SharedMemoryBytes)
                {
                    Note = "Sections shared with other processes",
                },
                new("other", "Other", space.OtherBytes)
                {
                    Note = "Pages outside the address space map",
                    Optional = true,
                },
            ],
        }, scan, string.Empty);
    }

    private static Level? ProcessFiles(BreakdownSample s, int id, string kind, Scans scans)
    {
        if (kind is not ("images" or "mapped"))
            return null;
        if (s.Processes is not { } processes)
            return Pending("Files", 0);
        if (processes.FindIndex(p => p.Id == id) is var index && index < 0)
            return null;

        var name = processes[index].Name;
        var key = $"space:{id.ToString(CultureInfo.InvariantCulture)}";
        var scan = scans.Get(key, () => AddressSpace.Scan(id));
        var images = kind == "images";
        var level = new Level(
            images ? "Programs and DLLs" : "Mapped files",
            images
                ? $"EXE and DLL files loaded by {name}, by how much of each is in RAM. Most of these pages are shared with every other process that loads the same file."
                : $"Data files {name} has mapped into memory, by how much of each is in RAM.",
            0,
            []) { ScanKey = key };

        if (scan.Result is not { } space)
            return WithScanStatus(level, scan, "Reading the address space…");

        var files = images ? space.Images : space.MappedFiles;
        return WithScanStatus(level with
        {
            Bytes = Sum(files, f => f.ResidentBytes),
            Entries = [.. files.Select(f => FileEntry(f, showProcesses: false))],
        }, scan, string.Empty);
    }

    private static Level SharedFiles(BreakdownSample s, Scans scans)
    {
        if (Split(s) is not { } parts || s.Processes is not { } processes)
            return Pending("Shared files and other", 0);

        var scan = scans.Get("shared", () => AddressSpace.ScanAll(processes));
        var level = new Level(
            "Shared files and other",
            "What In use holds beyond private process memory, compression, the kernel and the file cache: DLLs and files shared between processes, "
            + "plus memory drivers have locked (graphics, virtual machines), page tables, kernel stacks and other memory Windows doesn't attribute. "
            + "Each file shows the most RAM it holds in any one process.",
            parts.Shared,
            []) { ScanKey = "shared" };

        if (scan.Result is not { } shared)
            return WithScanStatus(level, scan, "Reading every process's address space…");

        var status = shared.Denied > 0
            ? $" {Format.Number(shared.Denied)} processes couldn't be read; run as administrator to include them."
            : string.Empty;
        return WithScanStatus(level with
        {
            Entries = [.. shared.Files.Select(f => FileEntry(f, showProcesses: true))],
        }, scan, status);
    }

    private static Entry FileEntry(FileUse file, bool showProcesses)
    {
        var folder = Path.GetDirectoryName(file.Path) ?? string.Empty;
        return new Entry(file.Path, Path.GetFileName(file.Path), file.ResidentBytes)
        {
            Note = !showProcesses ? folder
                : file.Processes == 1 ? $"1 process · {folder}"
                : $"{Format.Number(file.Processes)} processes · {folder}",
            ToolTip = file.Path,
        };
    }

    private static Level Kernel(BreakdownSample s)
    {
        if (Split(s) is not { } parts || s.Kernel is not { } k)
            return Pending("Kernel and drivers", 0);

        return new Level(
            "Kernel and drivers",
            "Memory the kernel and drivers hold for themselves. Pools are traced to drivers by tag: open one to see which tags use the most.",
            parts.Kernel,
            [
                new("nonpaged", "Non-paged pool", k.NonPagedPoolBytes)
                {
                    Note = "Kernel memory that never leaves RAM",
                    CanOpen = true,
                },
                new("paged", "Paged pool", k.PagedPoolBytes)
                {
                    Note = "Kernel memory that can be moved to the page file",
                    CanOpen = true,
                },
                new("drivers", "Driver code", k.DriverCodeBytes)
                {
                    Note = "Code and data of loaded drivers",
                    CanOpen = true,
                },
                new("code", "Kernel code", k.KernelCodeBytes)
                {
                    Note = "The kernel's own pageable code",
                    Optional = true,
                },
            ]);
    }

    private static Level Pool(BreakdownSample s, bool paged)
    {
        var title = paged ? "Paged pool" : "Non-paged pool";
        if (s.Kernel is not { } k || s.Tags is not { } tags)
            return Pending(title, 0);

        var entries = new List<Entry>();
        foreach (var tag in tags)
        {
            var bytes = paged ? tag.PagedBytes : tag.NonPagedBytes;
            if (bytes == 0)
                continue;

            var allocations = paged ? tag.PagedAllocations : tag.NonPagedAllocations;
            entries.Add(new Entry($"t:{tag.Value.ToString("x8", CultureInfo.InvariantCulture)}", tag.Tag, bytes)
            {
                Note = allocations == 1 ? "1 allocation" : $"{Format.Number(allocations)} allocations",
                Mono = true,
                CanOpen = true,
            });
        }

        return new Level(
            title,
            paged
                ? "Paged pool by tag. Sizes are what each tag has allocated, including parts moved to the page file, so they can add up to more than what's in RAM. Open a tag to find the drivers that use it."
                : "Non-paged pool by tag. Drivers label each allocation with a four-character tag; open one to find the drivers that use it.",
            paged ? k.PagedPoolBytes : k.NonPagedPoolBytes,
            entries)
        {
            SizeHeader = paged ? "Allocated" : "In RAM",
        };
    }

    private static Level? TagDrivers(BreakdownSample s, string key, Scans scans)
    {
        if (!key.StartsWith("t:", StringComparison.Ordinal)
            || !uint.TryParse(key.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return null;
        if (s.Tags is not { } tags)
            return Pending("Tag", 0);

        // A tag whose allocations were all freed drops out of the list; keep showing it at zero.
        var index = tags.FindIndex(t => t.Value == value);
        var tag = index >= 0 ? tags[index] : new PoolTag(PoolTags.Decode(value), value, 0, 0, 0, 0);
        var name = tag.Tag;
        var scan = scans.Get($"tag:{key[2..]}", () => DriverTagSearch.Find(value));
        var level = new Level(
            $"Tag {name}",
            $"Loaded drivers whose files contain the tag {name}. A tag is only four characters, so a common one can also match drivers that don't use it.",
            tag.PagedBytes + tag.NonPagedBytes,
            [])
        {
            ScanKey = $"tag:{key[2..]}",
            ShowShare = false,
            SizeHeader = "Image size",
        };

        if (scan.Result is not { } drivers)
            return WithScanStatus(level, scan, "Searching driver files…");

        return WithScanStatus(level with
        {
            Entries = [.. drivers.Select(DriverEntry)],
        }, scan, drivers.Count == 0
            ? " No loaded driver contains this tag. It may be built at run time or come from a driver that has since unloaded."
            : string.Empty);
    }

    private static Level Drivers(BreakdownSample s)
    {
        if (s.Kernel is not { } k || s.Modules is not { } modules)
            return Pending("Driver code", 0);

        return new Level(
            "Driver code",
            $"Drivers have {Format.Bytes(k.DriverCodeBytes)} of code and data in RAM. Windows doesn't report that per driver, "
            + "so each loaded driver is listed by its image size; parts of an image may be paged out or never loaded.",
            k.DriverCodeBytes,
            [.. modules.DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase).Select(DriverEntry)])
        {
            ShowShare = false,
            SizeHeader = "Image size",
        };
    }

    private static Entry DriverEntry(KernelModule module) =>
        new(module.Path, module.Name, module.ImageBytes)
        {
            Note = Path.GetDirectoryName(module.Path) ?? string.Empty,
            ToolTip = module.Path,
        };

    private static Level Modified(BreakdownSample s)
    {
        var lists = s.Lists;
        var forPageFile = Math.Min(lists.ModifiedForPageFileBytes, lists.ModifiedBytes);
        return new Level(
            "Modified",
            "Pages that were changed and must be written to disk before the RAM can be reused. Windows writes them out in the background; once written they move to standby.",
            lists.AllModifiedBytes,
            [
                new("pagefile", "For the page file", forPageFile)
                {
                    Note = "Process memory trimmed from working sets",
                },
                new("files", "For files", lists.ModifiedBytes - forPageFile)
                {
                    Note = "Changes to mapped files, waiting to be saved",
                },
                new("nowrite", "Not to be written", lists.ModifiedNoWriteBytes)
                {
                    Note = "Changed pages Windows holds back from disk",
                    Optional = true,
                },
            ]);
    }

    private static Level Standby(BreakdownSample s)
    {
        var lists = s.Lists;
        var entries = new List<Entry>();
        for (var priority = 7; priority >= 0; priority--)
        {
            entries.Add(new Entry($"priority{priority}", $"Priority {priority}", lists.StandbyByPriority[priority])
            {
                Note = priority switch
                {
                    7 => "Reused last",
                    5 => "Where most cached data starts",
                    0 => "Reused first",
                    _ => string.Empty,
                },
            });
        }

        return new Level(
            "Standby",
            "Cached file and program data that isn't in active use. It counts as available: Windows hands it out the moment something needs memory, lowest priority first. Until then, reading it again costs nothing.",
            lists.StandbyBytes,
            entries)
        {
            Sorted = false,
        };
    }

    private static Level Free(BreakdownSample s)
    {
        var lists = s.Lists;
        return new Level(
            "Free",
            "RAM that holds nothing at all. Windows keeps little of it on purpose: memory nobody needs is put to work as standby cache.",
            lists.UnusedBytes + lists.BadBytes,
            [
                new("zeroed", "Zeroed", lists.ZeroedBytes)
                {
                    Note = "Cleared and ready to hand out",
                },
                new("free", "Free", lists.FreeBytes)
                {
                    Note = "Released, waiting to be cleared",
                },
                new("bad", "Bad", lists.BadBytes)
                {
                    Note = "Pages that failed a memory check",
                    Optional = true,
                },
            ])
        {
            Sorted = false,
        };
    }

    private static Level Pending(string title, double bytes) =>
        new(title, string.Empty, bytes, []) { Status = "Reading…", IsBusy = true };

    private static Level WithScanStatus(Level level, Scan scan, string message)
    {
        if (!scan.IsDone)
            return level with { Status = message, IsBusy = true };
        if (scan.Error is { } error)
            return level with { Status = error };

        return level with { Status = $"Snapshot taken at {scan.CompletedAt.ToString("T", CultureInfo.CurrentCulture)}.{message}" };
    }

    private static int? ProcessId(string key) =>
        key.StartsWith("p:", StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    private static ulong Sum<T>(IEnumerable<T> items, Func<T, ulong> bytes)
    {
        ulong sum = 0;
        foreach (var item in items)
            sum += bytes(item);
        return sum;
    }
}
