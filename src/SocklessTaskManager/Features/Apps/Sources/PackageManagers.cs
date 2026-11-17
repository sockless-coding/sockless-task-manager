using System.Text.Json;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// Apps installed by command-line package managers that keep their own record rather than the
/// uninstall registry: Scoop (per user and global) and Chocolatey. Read from their folders;
/// uninstalled by running the package manager in a console window, so its output can be seen.
/// (winget's portable apps are in the uninstall registry, and its other installs are ordinary
/// setup programs and packages.)
/// </summary>
internal static class PackageManagers
{
    public static IEnumerable<AppItem> Scan()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var scoop = Environment.GetEnvironmentVariable("SCOOP") is { Length: > 0 } custom ? custom : Path.Join(profile, "scoop");
        foreach (var item in Scoop(scoop, global: false))
            yield return item;

        var scoopGlobal = Environment.GetEnvironmentVariable("SCOOP_GLOBAL") is { Length: > 0 } customGlobal ? customGlobal : Path.Join(programData, "scoop");
        foreach (var item in Scoop(scoopGlobal, global: true))
            yield return item;

        var chocolatey = Environment.GetEnvironmentVariable("ChocolateyInstall") is { Length: > 0 } choco ? choco : Path.Join(programData, "chocolatey");
        foreach (var item in Chocolatey(chocolatey))
            yield return item;
    }

    // ---------- Scoop ----------

    private static IEnumerable<AppItem> Scoop(string root, bool global)
    {
        var apps = Path.Join(root, "apps");
        if (!Directory.Exists(apps))
            yield break;

        // Scoop's own command, from its shims folder; without it, apps can still be listed.
        var shim = Path.Join(root, "shims", "scoop.cmd");
        if (!File.Exists(shim))
            shim = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "scoop.cmd");

        foreach (var folder in Folders(apps))
        {
            var name = Path.GetFileName(folder);
            var current = Path.Join(folder, "current");
            if (!Directory.Exists(current))
                continue;

            var manifest = Json(Path.Join(current, "manifest.json"));
            var install = Json(Path.Join(current, "install.json"));
            var bucket = Property(install, "bucket");
            var facts = new List<AppFact> { new("Scoop app", name) };
            if (bucket is not null)
                facts.Add(new AppFact("Bucket", bucket));
            if (Property(manifest, "description") is { } description)
                facts.Add(new AppFact("Description", description));
            facts.Add(new AppFact("Uninstall command", global ? $"scoop uninstall {name} --global" : $"scoop uninstall {name}"));

            var isScoop = name.Equals("scoop", StringComparison.OrdinalIgnoreCase);
            yield return new AppItem
            {
                Id = $"scoop|{(global ? "global" : "user")}|{name}",
                Group = AppGroup.PackageManagers,
                Name = name,
                InstalledWith = "Scoop",
                Publisher = bucket is null ? null : $"{bucket} bucket",
                Version = Property(manifest, "version"),
                InstalledOn = Created(current),
                IsPerUser = !global,
                Architecture = Property(install, "architecture") switch
                {
                    "64bit" => "64-bit",
                    "32bit" => "32-bit",
                    "arm64" => "ARM64",
                    _ => null,
                },
                Location = folder,
                About = global
                    ? "Installed for everyone by Scoop, a command-line installer. Scoop removes it, its shims and shortcuts."
                    : "Installed for you by Scoop, a command-line installer. Scoop removes it, its shims and shortcuts.",
                Facts = facts,
                Website = InstalledPrograms.Web(Property(manifest, "homepage")),
                UninstallText = "Uninstall",
                Uninstall = isScoop || !File.Exists(shim)
                    ? null
                    : new CommandAction(Cmd, $"/c \"\"{shim}\" uninstall {name}{(global ? " --global" : string.Empty)}\"", Elevate: global && !Elevation.IsElevated),
                Locked = isScoop
                    ? "Scoop itself: uninstall it with \"scoop uninstall scoop\", which removes every app it installed."
                    : !File.Exists(shim) ? "Scoop's command isn't where it should be, so it can't be run from here." : null,
            };
        }
    }

    // ---------- Chocolatey ----------

    private static IEnumerable<AppItem> Chocolatey(string root)
    {
        var lib = Path.Join(root, "lib");
        if (!Directory.Exists(lib))
            yield break;

        var choco = Path.Join(root, "bin", "choco.exe");
        if (!File.Exists(choco))
            choco = Path.Join(root, "choco.exe");

        foreach (var folder in Folders(lib))
        {
            var id = Path.GetFileName(folder);
            var nuspecPath = Path.Join(folder, $"{id}.nuspec");
            string nuspec;
            try
            {
                if (!File.Exists(nuspecPath))
                    continue;
                nuspec = File.ReadAllText(nuspecPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var facts = new List<AppFact> { new("Chocolatey package", id) };
            if ((Element(nuspec, "summary") ?? Element(nuspec, "description")) is { } summary)
                facts.Add(new AppFact("Description", summary.Length > 400 ? summary[..400] + "…" : summary));
            facts.Add(new AppFact("Uninstall command", $"choco uninstall {id}"));

            var isChocolatey = id.Equals("chocolatey", StringComparison.OrdinalIgnoreCase);
            yield return new AppItem
            {
                Id = $"chocolatey|{id}",
                Group = AppGroup.PackageManagers,
                Name = Element(nuspec, "title") is { Length: > 0 } title ? title : id,
                InstalledWith = "Chocolatey",
                Publisher = Element(nuspec, "authors"),
                Version = Element(nuspec, "version"),
                InstalledOn = Created(folder),
                Location = folder,
                About = "Installed for everyone by Chocolatey, a command-line package manager. Chocolatey runs the package's uninstaller, so a program it installed is removed too.",
                Facts = facts,
                Website = InstalledPrograms.Web(Element(nuspec, "projectUrl")),
                Uninstall = isChocolatey || !File.Exists(choco)
                    ? null
                    : new CommandAction(choco, $"uninstall {id} -y", Elevate: !Elevation.IsElevated),
                Locked = isChocolatey
                    ? "Chocolatey itself: see Chocolatey's documentation for how to remove it."
                    : !File.Exists(choco) ? "Chocolatey's command isn't where it should be, so it can't be run from here." : null,
            };
        }
    }

    private static string Cmd => Path.Join(Environment.SystemDirectory, "cmd.exe");

    private static string[] Folders(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTime? Created(string folder)
    {
        try
        {
            return Directory.GetCreationTime(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JsonElement? Json(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>An element's text in a .nuspec (no attributes on these elements); XML entities decoded.</summary>
    private static string? Element(string xml, string name)
    {
        var open = xml.IndexOf($"<{name}>", StringComparison.Ordinal);
        if (open < 0)
            return null;

        var start = open + name.Length + 2;
        var end = xml.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        if (end < 0)
            return null;

        var text = xml[start..end].Trim();
        if (text.StartsWith("<![CDATA[", StringComparison.Ordinal) && text.EndsWith("]]>", StringComparison.Ordinal))
            text = text[9..^3].Trim();
        text = System.Net.WebUtility.HtmlDecode(text);
        return text.Length > 0 ? text : null;
    }
}
