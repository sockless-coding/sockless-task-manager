using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// What starts when someone signs in: the Run keys, the Startup folders and packaged apps' startup
/// tasks, which together are what Task Manager's Startup apps lists, plus the RunOnce and policy
/// lists it leaves out.
/// </summary>
internal static class SignInSources
{
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string AppModel = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel";

    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string PolicyRunPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";

    private static readonly (RegistryHive Hive, string Path, string? Approved, string About)[] RunKeys =
    [
        (RegistryHive.CurrentUser, RunPath, "Run", "Starts when you sign in."),
        (RegistryHive.LocalMachine, RunPath, "Run", "Starts when anyone signs in."),
        (RegistryHive.CurrentUser, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Starts when you sign in (the 32-bit programs' list)."),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Starts when anyone signs in (the 32-bit programs' list)."),
        (RegistryHive.CurrentUser, RunOncePath, null, "Runs once at your next sign-in, then Windows removes it."),
        (RegistryHive.LocalMachine, RunOncePath, null, "Runs once at the next sign-in, then Windows removes it."),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", null, "Runs once at the next sign-in, then Windows removes it (the 32-bit programs' list)."),
        (RegistryHive.CurrentUser, PolicyRunPath, null, "Starts when you sign in. Set as a policy, so Settings and Task Manager don't list it."),
        (RegistryHive.LocalMachine, PolicyRunPath, null, "Starts when anyone signs in. Set as a policy, so Settings and Task Manager don't list it."),
        (RegistryHive.LocalMachine, @"Software\Microsoft\Windows NT\CurrentVersion\Terminal Server\Install\Software\Microsoft\Windows\CurrentVersion\Run", null,
            "Starts when anyone signs in while the computer is in Remote Desktop install mode."),
    ];

    public static IEnumerable<AutostartItem> Scan()
    {
        foreach (var run in RunKeys)
        {
            foreach (var item in RunKey(run.Hive, run.Path, run.Approved, run.About))
                yield return item;
        }

        foreach (var item in StartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser))
            yield return item;
        foreach (var item in StartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine))
            yield return item;

        foreach (var item in PackagedTasks())
            yield return item;
    }

    private static IEnumerable<AutostartItem> RunKey(RegistryHive hive, string path, string? approved, string about)
    {
        var location = AutostartRegistry.Display(hive, path);
        var admin = hive == RegistryHive.LocalMachine;

        using (var key = AutostartRegistry.Open(hive, path))
        {
            if (key is not null)
            {
                foreach (var name in AutostartRegistry.ValueNames(key))
                {
                    if (AutostartRegistry.Text(key, name) is not { Length: > 0 } command)
                        continue;

                    yield return new AutostartItem
                    {
                        Id = $"{location}|{name}",
                        Category = AutostartCategory.SignIn,
                        Name = name.Length > 0 ? name : "(Default)",
                        Location = location,
                        Command = command,
                        Image = Image(command),
                        IsEnabled = approved is null || !IsSwitchedOff(hive, approved, name),
                        IsPerUser = hive == RegistryHive.CurrentUser,
                        About = about,
                        Toggle = approved is not null
                            ? new ApprovedSwitch(hive, approved, name) { NeedsAdmin = admin }
                            : new MovedValueTarget(hive, path, name) { NeedsAdmin = admin },
                        Removal = new RegistryValueTarget(hive, path, name) { NeedsAdmin = admin },
                        RegistryKey = location,
                    };
                }
            }
        }

        // Turned off by moving them aside (lists without a StartupApproved switch).
        var aside = $@"{path}\{AutostartRegistry.DisabledKey}";
        using var moved = AutostartRegistry.Open(hive, aside);
        if (moved is null)
            yield break;

        foreach (var name in AutostartRegistry.ValueNames(moved))
        {
            if (AutostartRegistry.Text(moved, name) is not { Length: > 0 } command)
                continue;

            yield return new AutostartItem
            {
                Id = $"{location}|{name}",
                Category = AutostartCategory.SignIn,
                Name = name.Length > 0 ? name : "(Default)",
                Location = location,
                Command = command,
                Image = Image(command),
                IsEnabled = false,
                IsPerUser = hive == RegistryHive.CurrentUser,
                About = about,
                Toggle = new MovedValueTarget(hive, path, name) { NeedsAdmin = admin },
                Removal = new RegistryValueTarget(hive, aside, name) { NeedsAdmin = admin },
                RegistryKey = AutostartRegistry.Display(hive, aside),
            };
        }
    }

    private static IEnumerable<AutostartItem> StartupFolder(string folder, RegistryHive hive)
    {
        if (folder.Length == 0 || !Directory.Exists(folder))
            yield break;

        string[] files;
        try
        {
            files = Directory.GetFiles(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var admin = hive == RegistryHive.LocalMachine;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            string command = file;
            string? image = file;
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var link = ShellLink.Read(file);
                image = link?.Target;
                command = link is { Target: { } target } ? Quote(target, link.Value.Arguments) : file;
            }

            yield return new AutostartItem
            {
                Id = $"{folder}|{name}",
                Category = AutostartCategory.SignIn,
                Name = Path.GetFileNameWithoutExtension(name),
                Location = folder,
                Command = command,
                Image = image,
                IsEnabled = !IsSwitchedOff(hive, "StartupFolder", name),
                IsPerUser = !admin,
                About = admin
                    ? "In the Startup folder for all users: opens when anyone signs in."
                    : "In your Startup folder: opens when you sign in.",
                Facts = [new AutostartFact("File", file)],
                Toggle = new ApprovedSwitch(hive, "StartupFolder", name) { NeedsAdmin = admin },
                Removal = new FileTarget(file) { NeedsAdmin = admin },
            };
        }
    }

    /// <summary>
    /// Packaged (Store, MSIX) apps declare startup tasks in their manifest; whether each runs is its
    /// State under the app's SystemAppData key: 2 on, 4 on by policy, 0 and 1 off, 3 off by policy.
    /// </summary>
    private static IEnumerable<AutostartItem> PackagedTasks()
    {
        using var apps = AutostartRegistry.Open(RegistryHive.CurrentUser, $@"{AppModel}\SystemAppData");
        if (apps is null)
            yield break;

        var packages = InstalledPackages();
        foreach (var family in AutostartRegistry.SubKeyNames(apps))
        {
            if (!packages.TryGetValue(family, out var installed))
                continue;

            using var app = apps.OpenSubKey(family);
            if (app is null)
                continue;

            foreach (var taskId in AutostartRegistry.SubKeyNames(app))
            {
                using var task = app.OpenSubKey(taskId);
                if (task is null || AutostartRegistry.Number(task, "State") is not { } state)
                    continue;

                if (FindTask(installed, taskId) is not { Package: var package, Executable: var executable, Publisher: var publisher })
                    continue;

                var keyPath = $@"{AppModel}\SystemAppData\{family}\{taskId}";
                var byPolicy = state is 3 or 4;
                yield return new AutostartItem
                {
                    Id = $"package|{family}|{taskId}",
                    Category = AutostartCategory.SignIn,
                    Name = package.DisplayName,
                    Location = $"Packaged app {family}",
                    Command = executable ?? string.Empty,
                    Image = executable,
                    IsEnabled = state is 2 or 4,
                    IsPerUser = true,
                    About = "A packaged (Store or MSIX) app's startup task: starts when you sign in, if it's on. Uninstalling the app removes it.",
                    Facts = [new AutostartFact("Startup task", taskId), new AutostartFact("Package", package.FullName)],
                    Toggle = byPolicy ? null : new PackagedTaskSwitch(keyPath),
                    Locked = byPolicy ? "A policy sets whether this starts." : "Uninstall the app to remove its startup task.",
                    PackageSigner = publisher,
                    RegistryKey = AutostartRegistry.Display(RegistryHive.CurrentUser, keyPath),
                };
            }
        }
    }

    private sealed record Package(string FullName, string Root, string DisplayName);

    private sealed record PackagedTask(Package Package, string? Executable, string? Publisher);

    /// <summary>The user's installed packages by family name (Name_PublisherId), from the AppModel repository.</summary>
    private static Dictionary<string, List<Package>> InstalledPackages()
    {
        var families = new Dictionary<string, List<Package>>(StringComparer.OrdinalIgnoreCase);
        using var packages = AutostartRegistry.Open(RegistryHive.CurrentUser, $@"{AppModel}\Repository\Packages");
        if (packages is null)
            return families;

        foreach (var fullName in AutostartRegistry.SubKeyNames(packages))
        {
            var parts = fullName.Split('_');
            if (parts.Length < 5)
                continue;

            using var key = packages.OpenSubKey(fullName);
            if (key is null || AutostartRegistry.Text(key, "PackageRootFolder") is not { Length: > 0 } root)
                continue;

            var display = AutostartRegistry.Text(key, "DisplayName") is { Length: > 0 } text ? AutostartRegistry.Indirect(Resource(text, fullName, parts[0])) : parts[0];
            if (display.StartsWith('@') || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                display = parts[0];

            var family = $"{parts[0]}_{parts[^1]}";
            if (!families.TryGetValue(family, out var list))
                families[family] = list = [];
            list.Add(new Package(fullName, root, display));
        }

        return families;
    }

    /// <summary>
    /// A manifest's "ms-resource:app_name" as the indirect string Windows resolves:
    /// "@{Full_Name?ms-resource://Name/Resources/app_name}". Other text as is.
    /// </summary>
    private static string Resource(string text, string fullName, string name)
    {
        if (!text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return text;

        var key = text[12..];
        var uri = key.StartsWith("//", StringComparison.Ordinal) ? $"ms-resource:{key}"
            : key.StartsWith('/') ? $"ms-resource://{name}{key}"
            : key.Contains('/') ? $"ms-resource://{name}/{key}"
            : $"ms-resource://{name}/Resources/{key}";
        return $"@{{{fullName}?{uri}}}";
    }

    /// <summary>The package whose manifest declares the task, and the program it starts.</summary>
    private static PackagedTask? FindTask(List<Package> candidates, string taskId)
    {
        foreach (var package in candidates)
        {
            string manifest;
            try
            {
                var path = Path.Join(package.Root, "AppxManifest.xml");
                if (!File.Exists(path))
                    continue;
                manifest = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var at = manifest.IndexOf($"TaskId=\"{taskId}\"", StringComparison.Ordinal);
            if (at < 0)
                continue;

            // The task's own Executable, or else the Application element it's declared in.
            var element = manifest.LastIndexOf('<', at);
            var close = manifest.IndexOf('>', at);
            var executable = element >= 0 && close > element ? Attribute(manifest[element..close], "Executable") : null;
            if (executable is null)
            {
                var application = manifest.LastIndexOf("<Application ", at, StringComparison.Ordinal);
                if (application >= 0)
                {
                    var end = manifest.IndexOf('>', application);
                    executable = end > application ? Attribute(manifest[application..end], "Executable") : null;
                }
            }

            var publisher = TaskSources.Text(manifest, "PublisherDisplayName");
            return new PackagedTask(package, executable is null ? null : Path.GetFullPath(Path.Join(package.Root, executable)),
                publisher is { Length: > 0 } && !publisher.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? publisher : null);
        }

        return null;
    }

    private static string? Attribute(string element, string name)
    {
        var at = element.IndexOf($" {name}=\"", StringComparison.Ordinal);
        if (at < 0)
            return null;

        var start = at + name.Length + 3;
        var end = element.IndexOf('"', start);
        return end > start ? element[start..end] : null;
    }

    /// <summary>Off in Task Manager and Settings: the first byte of its StartupApproved value is odd.</summary>
    private static bool IsSwitchedOff(RegistryHive hive, string list, string name)
    {
        using var key = AutostartRegistry.Open(hive, $@"{ApprovedRoot}\{list}");
        return key is not null && AutostartRegistry.Binary(key, name) is { Length: > 0 } data && (data[0] & 1) == 1;
    }

    /// <summary>The file a command line runs; for rundll32, the DLL it loads.</summary>
    internal static string? Image(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '/' or '-')
            return null;

        var (program, arguments) = CommandLine.Resolve(command);
        if (program.Length == 0)
            return null;

        // A DLL host runs what it's given: the DLL is what to check. Switches (/s, /n, /i:…, /d) come first.
        var host = Path.GetFileName(program);
        if ((host.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || host.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase))
            && SkipSwitches(arguments) is { Length: > 0 } rest)
        {
            var (dll, _) = CommandLine.Resolve(rest.Split(',')[0]);
            if (dll.Length > 0)
                return dll;
        }

        return program;
    }

    private static string SkipSwitches(string arguments)
    {
        var rest = arguments.TrimStart();
        while (rest.Length > 0 && rest[0] is '/' or '-')
        {
            var space = rest.IndexOf(' ');
            rest = space < 0 ? string.Empty : rest[(space + 1)..].TrimStart();
        }

        return rest;
    }

    private static string Quote(string target, string arguments)
    {
        var program = target.Contains(' ') ? $"\"{target}\"" : target;
        return arguments.Length > 0 ? $"{program} {arguments}" : program;
    }
}
