using System.Runtime.InteropServices;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// The selected app's folder: what runs from it now (worth closing before uninstalling) and how
/// much it takes on disk. Read for one app at a time, when its details open; never per tick.
/// </summary>
internal static partial class AppFolder
{
    private const uint QueryLimitedInformation = 0x1000;

    /// <summary>Stop counting after this many files: a folder that big has its size said as "at least".</summary>
    private const int MaxFiles = 500_000;

    private static readonly string[] SharedFolders =
    [
        .. new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
        }.Select(Environment.GetFolderPath).Where(p => p.Length > 0).Select(Path.TrimEndingDirectorySeparator),
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
    ];

    /// <summary>
    /// A folder of its own: not a drive's root, nor one many apps share (Program Files, AppData,
    /// the Windows folder), whose size or processes would be everyone's.
    /// </summary>
    public static bool IsOwn(string folder)
    {
        var path = Path.TrimEndingDirectorySeparator(folder);
        if (Path.GetPathRoot(path) is not { } root || path.Length <= Path.TrimEndingDirectorySeparator(root).Length)
            return false;
        return !SharedFolders.Any(shared => string.Equals(shared, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Processes whose program is in the folder (or under it), as (id, name). Others' are missed without elevation.</summary>
    public static unsafe List<(int Id, string Name)> Running(string folder)
    {
        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        var buffer = NtSystem.Buffer(1024 * 1024);
        var running = new List<(int, string)>();
        Span<char> path = stackalloc char[1024];
        foreach (var process in SystemProcesses.Read(ref buffer))
        {
            if (process.Id <= 4)
                continue;

            var handle = OpenProcess(QueryLimitedInformation, 0, (uint)process.Id);
            if (handle == 0)
                continue;

            try
            {
                var size = (uint)path.Length;
                fixed (char* p = path)
                {
                    if (QueryFullProcessImageNameW(handle, 0, p, &size) != 0
                        && path[..(int)size].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        running.Add((process.Id, process.Name));
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return running;
    }

    /// <summary>Bytes in the folder and under it (junctions and links not followed); null if it can't be read.</summary>
    /// <param name="complete">False when it stopped early: too many files, or some couldn't be read.</param>
    public static long? Measure(string folder, CancellationToken cancel, out bool complete)
    {
        complete = true;
        long total = 0;
        var files = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                total += file.Length;
                if (++files % 2048 == 0)
                    cancel.ThrowIfCancellationRequested();
                if (files >= MaxFiles)
                {
                    complete = false;
                    break;
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            complete = false;
            return files > 0 ? total : null;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int QueryFullProcessImageNameW(nint process, uint flags, char* name, uint* size);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
