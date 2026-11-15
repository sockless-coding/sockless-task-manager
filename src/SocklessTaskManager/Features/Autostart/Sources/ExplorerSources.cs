using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Code that File Explorer and the browser load into themselves: shell extensions registered by
/// class id, browser helper objects, and COM classes the user's own registry redefines.
/// </summary>
internal static class ExplorerSources
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private static readonly RegistryHive[] Hives = [RegistryHive.CurrentUser, RegistryHive.LocalMachine];

    /// <summary>Handler lists: subkeys named for the handler, whose default value is its class id.</summary>
    private static readonly (string Path, string Kind)[] HandlerLists =
    [
        (@"Software\Classes\*\ShellEx\ContextMenuHandlers", "Context menu for files"),
        (@"Software\Classes\AllFileSystemObjects\ShellEx\ContextMenuHandlers", "Context menu for files and folders"),
        (@"Software\Classes\Directory\ShellEx\ContextMenuHandlers", "Context menu for folders"),
        (@"Software\Classes\Directory\Background\ShellEx\ContextMenuHandlers", "Context menu for a folder's background"),
        (@"Software\Classes\Folder\ShellEx\ContextMenuHandlers", "Context menu for folders"),
        (@"Software\Classes\Drive\ShellEx\ContextMenuHandlers", "Context menu for drives"),
        (@"Software\Classes\*\ShellEx\PropertySheetHandlers", "Properties page for files"),
        (@"Software\Classes\Directory\ShellEx\DragDropHandlers", "Drag and drop onto folders"),
        (@"Software\Classes\Directory\ShellEx\CopyHookHandlers", "Copy hook for folders"),
        ($@"{Explorer}\ShellIconOverlayIdentifiers", "Icon overlay"),
    ];

    /// <summary>Value lists: each value names a class id (in its name or its data).</summary>
    private static readonly (string Path, string Kind, bool IdInName)[] ValueLists =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad", "Shell service object, loaded when Explorer starts", false),
        ($@"{Explorer}\SharedTaskScheduler", "Shared task, loaded when Explorer starts", true),
        ($@"{Explorer}\ShellExecuteHooks", "Hook on everything Explorer opens", true),
    ];

    public static IEnumerable<AutostartItem> Scan()
    {
        foreach (var hive in Hives)
        {
            foreach (var (path, kind) in HandlerLists)
            {
                foreach (var item in Handlers(hive, path, kind))
                    yield return item;
            }

            foreach (var (path, kind, idInName) in ValueLists)
            {
                foreach (var item in ClassValues(hive, path, kind, idInName))
                    yield return item;
            }

            foreach (var item in BrowserHelpers(hive))
                yield return item;
        }

        foreach (var item in ClassOverrides())
            yield return item;
    }

    private static IEnumerable<AutostartItem> Handlers(RegistryHive hive, string path, string kind)
    {
        foreach (var (name, enabled) in Keys(hive, path))
        {
            var keyPath = enabled ? $@"{path}\{name}" : $@"{path}\{AutostartRegistry.DisabledKey}\{name}";
            using var key = AutostartRegistry.Open(hive, keyPath);
            if (key is null)
                continue;

            // The class id is the default value, or (older handlers) the key's own name.
            var clsid = AutostartRegistry.Text(key, null) is { Length: > 0 } value && value.StartsWith('{') ? value : name.StartsWith('{') ? name : null;
            if (clsid is null)
                continue;

            yield return Class(AutostartCategory.Explorer, hive, path, name, clsid, enabled, kind,
                "A shell extension: File Explorer loads it into itself (and into programs that show files) when it needs it.",
                new MovedKeyTarget(hive, path, name) { NeedsAdmin = hive == RegistryHive.LocalMachine },
                new RegistryKeyTarget(hive, keyPath) { NeedsAdmin = hive == RegistryHive.LocalMachine });
        }
    }

    private static IEnumerable<AutostartItem> ClassValues(RegistryHive hive, string path, string kind, bool idInName)
    {
        foreach (var (name, value, enabled) in Values(hive, path))
        {
            var clsid = idInName ? name : value;
            if (!clsid.StartsWith('{'))
                continue;

            var keyPath = enabled ? path : $@"{path}\{AutostartRegistry.DisabledKey}";
            yield return Class(AutostartCategory.Explorer, hive, path, idInName ? (value.Length > 0 ? value : name) : name, clsid, enabled, kind,
                "Loaded by File Explorer when it starts, so it runs in every signed-in session.",
                new MovedValueTarget(hive, path, name) { NeedsAdmin = hive == RegistryHive.LocalMachine },
                new RegistryValueTarget(hive, keyPath, name) { NeedsAdmin = hive == RegistryHive.LocalMachine });
        }
    }

    private static IEnumerable<AutostartItem> BrowserHelpers(RegistryHive hive)
    {
        foreach (var path in (string[])[$@"{Explorer}\Browser Helper Objects", $@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects"])
        {
            var kind = path.Contains("WOW6432Node", StringComparison.Ordinal) ? "Browser helper object, 32-bit" : "Browser helper object";
            foreach (var (name, enabled) in Keys(hive, path))
            {
                if (!name.StartsWith('{'))
                    continue;

                var keyPath = enabled ? $@"{path}\{name}" : $@"{path}\{AutostartRegistry.DisabledKey}\{name}";
                yield return Class(AutostartCategory.Browser, hive, path, name, name, enabled, kind,
                    "A browser helper object: Internet Explorer, and Edge in Internet Explorer mode, load it into every window.",
                    new MovedKeyTarget(hive, path, name) { NeedsAdmin = hive == RegistryHive.LocalMachine },
                    new RegistryKeyTarget(hive, keyPath) { NeedsAdmin = hive == RegistryHive.LocalMachine });
            }
        }
    }

    /// <summary>
    /// Classes registered both machine-wide and in the user's own registry: the user's copy wins, so
    /// whatever it names loads wherever Windows or a program creates that class for this user.
    /// </summary>
    private static IEnumerable<AutostartItem> ClassOverrides()
    {
        const string classes = @"Software\Classes\CLSID";
        using var user = AutostartRegistry.Open(RegistryHive.CurrentUser, classes);
        if (user is null)
            yield break;

        foreach (var clsid in AutostartRegistry.SubKeyNames(user))
        {
            foreach (var server in (string[])["InprocServer32", "LocalServer32"])
            {
                using var mine = AutostartRegistry.Open(RegistryHive.CurrentUser, $@"{classes}\{clsid}\{server}");
                if (mine is null || AutostartRegistry.Text(mine, null) is not { Length: > 0 } path)
                    continue;

                using var machine = AutostartRegistry.Open(RegistryHive.LocalMachine, $@"{classes}\{clsid}\{server}");
                if (machine is null || AutostartRegistry.Text(machine, null) is not { Length: > 0 } machinePath)
                    continue;

                var image = CommandLine.Normalize(path);
                if (string.Equals(image, CommandLine.Normalize(machinePath), StringComparison.OrdinalIgnoreCase))
                    continue;

                var location = AutostartRegistry.Display(RegistryHive.CurrentUser, $@"{classes}\{clsid}");
                yield return new AutostartItem
                {
                    Id = $"override|{clsid}|{server}",
                    Category = AutostartCategory.Explorer,
                    Name = AutostartRegistry.ClassName(clsid) is { } named ? $"{named} {clsid}" : clsid,
                    Location = location,
                    Command = path,
                    Image = server == "LocalServer32" ? SignInSources.Image(path) : image,
                    IsPerUser = true,
                    About = "A COM class your own registry redefines: wherever Windows or a program creates it for you, this runs instead of the machine-wide one.",
                    Facts = [new AutostartFact("Machine-wide", machinePath)],
                    Removal = new RegistryKeyTarget(RegistryHive.CurrentUser, $@"{classes}\{clsid}"),
                    RegistryKey = location,
                };
            }
        }
    }

    private static AutostartItem Class(AutostartCategory category, RegistryHive hive, string path, string name, string clsid, bool enabled,
        string kind, string about, AutostartTarget toggle, AutostartTarget removal)
    {
        var server = AutostartRegistry.ClassServer(clsid);
        var location = AutostartRegistry.Display(hive, path);
        var className = AutostartRegistry.ClassName(clsid);
        return new AutostartItem
        {
            Id = $"{location}|{name}",
            Category = category,
            Name = $"{(className ?? name).Trim()} ({kind.ToLowerInvariant()})",
            Location = location,
            Command = server ?? string.Empty,
            Image = server is null ? null : CommandLine.Normalize(server),
            IsEnabled = enabled,
            IsPerUser = hive == RegistryHive.CurrentUser,
            About = about,
            Facts = server is null
                ? [new AutostartFact("Kind", kind), new AutostartFact("Class", clsid), new AutostartFact("File", "The class isn't registered, so nothing loads")]
                : [new AutostartFact("Kind", kind), new AutostartFact("Class", clsid)],
            Toggle = toggle,
            Removal = removal,
            RegistryKey = location,
        };
    }

    /// <summary>Subkeys of a list, then those moved aside (off).</summary>
    internal static IEnumerable<(string Name, bool Enabled)> Keys(RegistryHive hive, string path)
    {
        using (var key = AutostartRegistry.Open(hive, path))
        {
            if (key is not null)
            {
                foreach (var name in AutostartRegistry.SubKeyNames(key))
                    yield return (name, true);
            }
        }

        using var aside = AutostartRegistry.Open(hive, $@"{path}\{AutostartRegistry.DisabledKey}");
        if (aside is null)
            yield break;

        foreach (var name in AutostartRegistry.SubKeyNames(aside))
            yield return (name, false);
    }

    /// <summary>Text values of a list, then those moved aside (off).</summary>
    internal static IEnumerable<(string Name, string Value, bool Enabled)> Values(RegistryHive hive, string path)
    {
        using (var key = AutostartRegistry.Open(hive, path))
        {
            if (key is not null)
            {
                foreach (var name in AutostartRegistry.ValueNames(key))
                {
                    if (AutostartRegistry.Text(key, name) is { } value)
                        yield return (name, value, true);
                }
            }
        }

        using var aside = AutostartRegistry.Open(hive, $@"{path}\{AutostartRegistry.DisabledKey}");
        if (aside is null)
            yield break;

        foreach (var name in AutostartRegistry.ValueNames(aside))
        {
            if (AutostartRegistry.Text(aside, name) is { } value)
                yield return (name, value, false);
        }
    }
}
