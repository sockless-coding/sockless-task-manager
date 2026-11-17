using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>A newer version of an installed app, and how to install it.</summary>
/// <param name="AppId">The <see cref="AppItem.Id"/> it updates.</param>
/// <param name="Installed">The version the updater sees installed (its own reading; may differ from the app's entry).</param>
/// <param name="AppVersion">The app's version when it was checked: when that moves on, the update is done.</param>
/// <param name="Available">The version it would install.</param>
/// <param name="Updater">"winget", "Chocolatey", "Scoop".</param>
/// <param name="Package">The updater's name for it: a winget id, a Chocolatey or Scoop package.</param>
internal sealed record AppUpdate(string AppId, string? AppVersion, string Installed, string Available, string Updater, string Package, string? Source, CommandAction Action);

/// <param name="Updates">By app id.</param>
/// <param name="Failure">Why a check couldn't be made (no winget, no network); null when every updater answered.</param>
internal sealed record UpdateCheck(Dictionary<string, AppUpdate> Updates, string? Failure);

/// <summary>
/// Finds newer versions of installed apps through the updaters that know them: winget for setup
/// programs, Windows Installer packages and the Store apps its msstore source has, Chocolatey and
/// Scoop for their own. winget and Chocolatey ask their sources online, so this runs when asked
/// (first show, Refresh, after an update), never per tick; Scoop compares with its local buckets,
/// as fresh as its last "scoop update". Updates run the updater in a console window.
/// </summary>
internal static class AppUpdates
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static UpdateCheck Check(IReadOnlyList<AppItem> items)
    {
        var updates = new Dictionary<string, AppUpdate>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        // winget and Chocolatey each take seconds online: ask both at once.
        var choco = items.Any(i => i.Id.StartsWith("chocolatey|", StringComparison.Ordinal))
            ? Task.Run(() => Chocolatey(items))
            : Task.FromResult<(List<AppUpdate>, string?)>(([], null));
        var (winget, wingetFailure) = Winget(items);
        var (chocolatey, chocolateyFailure) = choco.GetAwaiter().GetResult();

        foreach (var update in Scoop(items).Concat(chocolatey).Concat(winget))
            updates.TryAdd(update.AppId, update);

        if (wingetFailure is not null)
            failures.Add(wingetFailure);
        if (chocolateyFailure is not null)
            failures.Add(chocolateyFailure);
        return new UpdateCheck(updates, failures.Count > 0 ? string.Join(" ", failures) : null);
    }

    /// <summary>Whether the update still applies: the app's version is still the one the updater saw.</summary>
    public static bool IsCurrent(AppUpdate update, AppItem item) => string.Equals(item.Version, update.AppVersion, StringComparison.OrdinalIgnoreCase);

    // ---------- winget ----------

    /// <summary>winget's app execution alias; elevated, the same user's alias is used.</summary>
    private static string WingetPath
    {
        get
        {
            var alias = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(alias) ? alias : "winget.exe";
        }
    }

    private static (List<AppUpdate>, string?) Winget(IReadOnlyList<AppItem> items)
    {
        var (output, failure) = Capture(WingetPath, "upgrade --include-unknown --accept-source-agreements --disable-interactivity");
        if (output is null)
            return ([], failure ?? "winget isn't installed: get App Installer from the Microsoft Store to check most apps for updates.");

        // winget names an app as its uninstall entry or package does: match by name, then version.
        var byName = items
            .Where(i => i.Group is not AppGroup.Features && !i.Id.StartsWith("scoop|", StringComparison.Ordinal) && !i.Id.StartsWith("chocolatey|", StringComparison.Ordinal))
            .ToLookup(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var updates = new List<AppUpdate>();
        foreach (var row in WingetTable.Parse(output))
        {
            var candidates = byName[row.Name].ToList();
            if (candidates.Count == 0)
                continue;

            var item = candidates.FirstOrDefault(c => c.Version is { } v && Normalize(v) == Normalize(row.Version))
                ?? candidates.OrderBy(c => c.Group).First();
            var unknown = row.Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
            var source = row.Source.Length > 0 ? $" --source {row.Source}" : string.Empty;
            updates.Add(new AppUpdate(item.Id, item.Version, row.Version, row.Available, "winget", row.Id, row.Source.Length > 0 ? row.Source : null,
                new CommandAction(WingetPath,
                    $"upgrade --id {row.Id} --exact{source}{(unknown ? " --include-unknown" : string.Empty)} --accept-package-agreements --accept-source-agreements",
                    Elevate: false, IsWinget: true)));
        }

        return (updates, null);
    }

    // ---------- Chocolatey ----------

    private static (List<AppUpdate>, string?) Chocolatey(IReadOnlyList<AppItem> items)
    {
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall") is { Length: > 0 } custom
            ? custom
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        var choco = Path.Join(root, "bin", "choco.exe");
        if (!File.Exists(choco))
            choco = Path.Join(root, "choco.exe");
        if (!File.Exists(choco))
            return ([], null);

        // "id|installed|available|pinned", one package per line.
        var (output, failure) = Capture(choco, "outdated --limit-output --ignore-unfound");
        if (output is null)
            return ([], failure);

        var ids = items.Where(i => i.Id.StartsWith("chocolatey|", StringComparison.Ordinal)).ToDictionary(i => i.Id[11..], i => i, StringComparer.OrdinalIgnoreCase);
        var updates = new List<AppUpdate>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 3 || !ids.TryGetValue(parts[0], out var item) || (parts.Length > 3 && parts[3].Equals("true", StringComparison.OrdinalIgnoreCase)))
                continue;

            updates.Add(new AppUpdate(item.Id, item.Version, parts[1], parts[2], "Chocolatey", parts[0], null,
                new CommandAction(choco, $"upgrade {parts[0]} -y", Elevate: !Elevation.IsElevated)));
        }

        return (updates, null);
    }

    // ---------- Scoop ----------

    /// <summary>Each app's version against its bucket's manifest, as "scoop status" does; no network.</summary>
    private static IEnumerable<AppUpdate> Scoop(IReadOnlyList<AppItem> items)
    {
        foreach (var item in items)
        {
            if (!item.Id.StartsWith("scoop|", StringComparison.Ordinal) || item.Location is not { } folder || item.Version is not { } version)
                continue;

            // <root>\apps\<name>: the bucket manifest is <root>\buckets\<bucket>\bucket\<name>.json.
            var name = Path.GetFileName(folder);
            var root = Path.GetDirectoryName(Path.GetDirectoryName(folder));
            var bucket = Property(Path.Join(folder, "current", "install.json"), "bucket");
            if (root is null || bucket is null)
                continue;

            var latest = Property(Path.Join(root, "buckets", bucket, "bucket", $"{name}.json"), "version");
            if (latest is null || AppText.CompareVersions(latest, version) <= 0)
                continue;

            var shim = Path.Join(root, "shims", "scoop.cmd");
            if (!File.Exists(shim))
                continue;

            var global = item.Id.StartsWith("scoop|global|", StringComparison.Ordinal);
            yield return new AppUpdate(item.Id, version, version, latest, "Scoop", name, bucket,
                new CommandAction(Path.Join(Environment.SystemDirectory, "cmd.exe"),
                    $"/c \"\"{shim}\" update {name}{(global ? " --global" : string.Empty)}\"", Elevate: global && !Elevation.IsElevated));
        }
    }

    private static string? Property(string path, string name)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // ---------- Running ----------

    /// <summary>Runs a command without a window and returns what it printed; null and why, if it couldn't run or finish.</summary>
    private static (string? Output, string? Failure) Capture(string program, string arguments)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return (null, null);

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                return (null, $"{Path.GetFileNameWithoutExtension(program)} took too long to answer.");
            }

            return (output.GetAwaiter().GetResult(), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Running {program} failed: {ex}");
            return (null, null);
        }
    }

    /// <summary>"1.2.0" and "1.2" are the same version, as are "v1.2" and "1.2".</summary>
    private static string Normalize(string version)
    {
        var text = version.Trim().TrimStart('v', 'V');
        while (text.EndsWith(".0", StringComparison.Ordinal) && text.Count(c => c == '.') > 1)
            text = text[..^2];
        return text;
    }
}
