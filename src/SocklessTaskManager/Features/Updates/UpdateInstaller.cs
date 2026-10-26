using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Sockless.TaskManager.Features.Updates;

/// <summary>
/// Replaces this copy with a release. Windows won't overwrite a running exe or a loaded DLL but will
/// rename them, so each file in the way becomes <c>.old</c>, the new one takes its place, and the next
/// start removes the leftovers.
/// </summary>
internal static class UpdateInstaller
{
    private const string ExeName = "SocklessTaskManager.exe";
    private const string OldSuffix = ".old";

    private static readonly string AppDirectory = AppContext.BaseDirectory;

    private static readonly string WorkDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", "update");

    // Captured before any rename: once the exe is renamed, ProcessPath may name the .old file.
    private static readonly string ExePath = Environment.ProcessPath ?? Path.Combine(AppDirectory, ExeName);

    /// <summary>False in a folder this user can't write to, such as Program Files without elevation.</summary>
    public static bool CanReplace()
    {
        var probe = Path.Combine(AppDirectory, $".update-{Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes what the last update left: the replaced files, once the old copy has exited, and the download.</summary>
    public static void RemoveLeftovers()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(AppDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use: the next start tries again.
                }
            }

            if (Directory.Exists(WorkDirectory))
                Directory.Delete(WorkDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Downloads the release, checks it against its SHA-256 and unpacks it. Returns the folder of files.</summary>
    public static async Task<string> DownloadAsync(string tag, CancellationToken cancel)
    {
        if (Directory.Exists(WorkDirectory))
            Directory.Delete(WorkDirectory, recursive: true);
        Directory.CreateDirectory(WorkDirectory);

        var name = ReleaseFeed.AssetName(tag);
        var zip = Path.Combine(WorkDirectory, name);
        await ReleaseFeed.DownloadAsync(tag, name, zip, cancel);
        await ReleaseFeed.DownloadAsync(tag, name + ".sha256", zip + ".sha256", cancel);

        // "<hex>  <file name>", as sha256sum writes it.
        var expected = (await File.ReadAllTextAsync(zip + ".sha256", cancel)).Split(' ', 2)[0].Trim();
        byte[] actual;
        await using (var stream = File.OpenRead(zip))
            actual = await SHA256.HashDataAsync(stream, cancel);
        if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The download doesn't match its checksum.");

        var staged = Path.Combine(WorkDirectory, "files");
        ZipFile.ExtractToDirectory(zip, staged);
        if (!File.Exists(Path.Combine(staged, ExeName)))
            throw new InvalidDataException($"The download has no {ExeName}.");

        return staged;
    }

    /// <summary>Moves the unpacked files over this copy. If any move fails, every file is put back.</summary>
    public static void Replace(string staged)
    {
        var moved = new List<(string Target, bool Existed)>();
        try
        {
            foreach (var source in Directory.GetFiles(staged, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staged, source);

                // Someone who renamed the exe keeps their name.
                var target = relative.Equals(ExeName, StringComparison.OrdinalIgnoreCase)
                    ? ExePath
                    : Path.Combine(AppDirectory, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var existed = File.Exists(target);
                if (existed)
                    File.Move(target, target + OldSuffix, overwrite: true);

                moved.Add((target, existed));
                File.Move(source, target);
            }
        }
        catch
        {
            Restore(moved);
            throw;
        }
    }

    /// <summary>Starts the new copy. The caller then closes this one.</summary>
    public static bool Restart()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false });
            return true;
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"Restart after update failed: {ex}");
            return false;
        }
    }

    private static void Restore(List<(string Target, bool Existed)> moved)
    {
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var (target, existed) = moved[i];
            try
            {
                // The last entry may have been renamed without the new file arriving.
                if (File.Exists(target))
                    File.Delete(target);
                if (existed)
                    File.Move(target + OldSuffix, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Couldn't restore {target}: {ex}");
            }
        }
    }
}
