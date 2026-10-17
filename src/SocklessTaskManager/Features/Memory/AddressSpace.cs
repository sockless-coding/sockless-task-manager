using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>A file mapped into memory and how much of it is resident.</summary>
/// <param name="ResidentBytes">In RAM in one process, or, across processes, the most in any one of them.</param>
internal sealed record FileUse(string Path, ulong ResidentBytes, int Processes);

/// <summary>One process's working set split by what backs each page, as VMMap does.</summary>
internal sealed record ProcessSpace(
    ulong WorkingSetBytes,
    ulong PrivateBytes,
    ulong SharedMemoryBytes,
    ulong OtherBytes,
    IReadOnlyList<FileUse> Images,
    IReadOnlyList<FileUse> MappedFiles)
{
    public ulong ImageBytes => Sum(Images);

    public ulong MappedFileBytes => Sum(MappedFiles);

    private static ulong Sum(IReadOnlyList<FileUse> files)
    {
        ulong sum = 0;
        foreach (var file in files)
            sum += file.ResidentBytes;
        return sum;
    }
}

/// <summary>Files mapped by every process that could be read.</summary>
internal sealed record SharedFiles(IReadOnlyList<FileUse> Files, int Read, int Denied);

/// <summary>
/// Walks a process's address space with <c>VirtualQueryEx</c> and counts which of its regions'
/// pages are in the working set. Slow (milliseconds per process), so it only runs on request,
/// off the sampling thread. Needs query and read access, so elevated and protected processes
/// are out of reach unless the app itself is elevated.
/// </summary>
internal static partial class AddressSpace
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;
    private const uint MemImage = 0x1000000;
    private const int ErrorBadLength = 24;

    public const string AccessDenied =
        "Windows didn't allow reading this process. Run Sockless Task Manager as administrator to inspect it.";

    public static ProcessSpace Scan(int processId) =>
        TryScan(processId, DeviceMap.Read()) ?? throw new UnauthorizedAccessException(AccessDenied);

    public static SharedFiles ScanAll(IReadOnlyList<ProcessUsage> processes)
    {
        var devices = DeviceMap.Read();
        var files = new Dictionary<string, (ulong Max, int Count)>(StringComparer.OrdinalIgnoreCase);
        int read = 0, denied = 0;

        foreach (var process in processes)
        {
            // The kernel and the compression store map no files.
            if (process.Id == 4 || process.Name == ProcessMemory.CompressionName)
                continue;

            if (TryScan(process.Id, devices) is not { } space)
            {
                denied++;
                continue;
            }

            read++;
            foreach (var file in space.Images.Concat(space.MappedFiles))
            {
                var (max, count) = files.GetValueOrDefault(file.Path);
                files[file.Path] = (Math.Max(max, file.ResidentBytes), count + 1);
            }
        }

        return new SharedFiles(
            [.. files.Where(f => f.Value.Max > 0).Select(f => new FileUse(f.Key, f.Value.Max, f.Value.Count))],
            read,
            denied);
    }

    private static unsafe ProcessSpace? TryScan(int processId, DeviceMap devices)
    {
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)processId);
        if (process == 0)
            return null;

        try
        {
            if (ReadWorkingSet(process) is not { } pages)
                return null;

            var page = (ulong)Environment.SystemPageSize;
            ulong privateBytes = 0, sharedMemory = 0, accounted = 0;
            var images = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            var mapped = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<nint, string?>();
            var name = stackalloc char[1024];

            nint address = 0;
            while (VirtualQueryEx(process, address, out var region, (nuint)sizeof(MemoryBasicInformation)) != 0)
            {
                if (region.State == MemCommit)
                {
                    var first = (ulong)region.BaseAddress / page;
                    var resident = CountPages(pages, first, first + (ulong)region.RegionSize / page) * page;
                    accounted += resident;

                    if (region.Type == MemPrivate)
                    {
                        privateBytes += resident;
                    }
                    else if (resident > 0)
                    {
                        if (!names.TryGetValue(region.AllocationBase, out var path))
                        {
                            var length = GetMappedFileName(process, region.AllocationBase, name, 1024);
                            path = length == 0 ? null : devices.ToDosPath(new string(name, 0, (int)length));
                            names[region.AllocationBase] = path;
                        }

                        if (path is null)
                            sharedMemory += resident;
                        else
                        {
                            var into = region.Type == MemImage ? images : mapped;
                            into[path] = into.GetValueOrDefault(path) + resident;
                        }
                    }
                }

                var next = region.BaseAddress + (nint)region.RegionSize;
                if (next <= address)
                    break;
                address = next;
            }

            var total = (ulong)pages.Length * page;
            return new ProcessSpace(
                total,
                privateBytes,
                sharedMemory,
                total > accounted ? total - accounted : 0,
                [.. images.Select(f => new FileUse(f.Key, f.Value, 1))],
                [.. mapped.Select(f => new FileUse(f.Key, f.Value, 1))]);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The working set as a sorted array of virtual page numbers.</summary>
    private static unsafe ulong[]? ReadWorkingSet(nint process)
    {
        // PSAPI_WORKING_SET_INFORMATION: an entry count, then one entry per page with the
        // virtual page number in bits 12 and up.
        var entries = 64 * 1024;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new ulong[entries + 1];
            bool ok;
            fixed (ulong* p = buffer)
                ok = QueryWorkingSet(process, p, (uint)(buffer.Length * sizeof(ulong)));

            if (ok)
            {
                var count = (int)Math.Min(buffer[0], (ulong)entries);
                var pages = new ulong[count];
                for (var i = 0; i < count; i++)
                    pages[i] = buffer[i + 1] >> 12;
                Array.Sort(pages);
                return pages;
            }

            if (Marshal.GetLastPInvokeError() != ErrorBadLength)
                return null;

            // The working set can grow before the retry.
            entries = (int)Math.Min(buffer[0] + buffer[0] / 8 + 1024, int.MaxValue / 16);
        }

        return null;
    }

    /// <summary>How many pages in <paramref name="pages"/> fall in [<paramref name="first"/>, <paramref name="end"/>).</summary>
    private static ulong CountPages(ulong[] pages, ulong first, ulong end)
    {
        static int LowerBound(ulong[] pages, ulong value)
        {
            int low = 0, high = pages.Length;
            while (low < high)
            {
                var mid = (low + high) >>> 1;
                if (pages[mid] < value)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        return (ulong)(LowerBound(pages, end) - LowerBound(pages, first));
    }

    /// <summary>Maps <c>\Device\HarddiskVolume3\…</c> back to <c>C:\…</c>.</summary>
    private sealed class DeviceMap
    {
        private readonly List<(string Device, string Drive)> _drives = [];

        public static unsafe DeviceMap Read()
        {
            var map = new DeviceMap();
            var target = stackalloc char[512];
            foreach (var drive in Environment.GetLogicalDrives())
            {
                var letter = drive.TrimEnd('\\');
                var length = QueryDosDevice(letter, target, 512);
                if (length > 0)
                    map._drives.Add((new string(target).TrimEnd('\0') + '\\', letter + '\\'));
            }

            return map;
        }

        public string ToDosPath(string devicePath)
        {
            foreach (var (device, drive) in _drives)
            {
                if (devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                    return ToLongPath(drive + devicePath[device.Length..]);
            }

            return devicePath;
        }

        /// <summary>Files opened by an 8.3 name ("PROGRA~2") report it; expand so one file is one entry.</summary>
        private static unsafe string ToLongPath(string path)
        {
            if (!path.Contains('~'))
                return path;

            var buffer = stackalloc char[1024];
            var length = GetLongPathName(path, buffer, 1024);
            return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : path;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "K32QueryWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryWorkingSet(nint process, void* buffer, uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation buffer, nuint length);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetMappedFileNameW")]
    private static unsafe partial uint GetMappedFileName(nint process, nint address, char* fileName, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint QueryDosDevice(string deviceName, char* targetPath, uint max);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetLongPathName(string shortPath, char* longPath, uint max);
}
