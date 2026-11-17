using System.Globalization;
using System.Security;
using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// Everything a setup program or Windows Installer registered in the uninstall registry: what
/// Programs and Features and Settings list, for everyone (HKLM, 64- and 32-bit) and for this user
/// (HKCU). Each entry is told apart by what installed it (Windows Installer, Inno Setup, NSIS, WiX,
/// Squirrel, ClickOnce, Steam, winget), which decides how it's changed and repaired. Entries
/// Windows hides (system components, updates) are listed in their own groups.
/// </summary>
internal static class InstalledPrograms
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string WindowsFolder = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    private static readonly (RegistryHive Hive, string Path, string? Architecture)[] Lists =
    [
        (RegistryHive.LocalMachine, UninstallPath, "64-bit"),
        (RegistryHive.LocalMachine, Uninstall32Path, "32-bit"),
        (RegistryHive.CurrentUser, UninstallPath, null),
        (RegistryHive.CurrentUser, Uninstall32Path, "32-bit"),
    ];

    /// <summary>The keys the list is read from, for the watcher.</summary>
    public static IEnumerable<(RegistryHive Hive, string Path)> Keys => Lists.Select(l => (l.Hive, l.Path));

    public static IEnumerable<AppItem> Scan()
    {
        foreach (var (hive, path, architecture) in Lists)
        {
            using var list = AppRegistry.Open(hive, path);
            if (list is null)
                continue;

            foreach (var name in AppRegistry.SubKeyNames(list))
            {
                using var key = AppRegistry.OpenSubKey(list, name);
                if (key is not null && Read(hive, path, name, key, architecture) is { } item)
                    yield return item;
            }
        }
    }

    private static AppItem? Read(RegistryHive hive, string listPath, string keyName, RegistryKey key, string? architecture)
    {
        // Windows lists only entries with a name.
        if (AppRegistry.Text(key, "DisplayName") is not { } name)
            return null;

        var keyPath = $@"{listPath}\{keyName}";
        var display = AppRegistry.Display(hive, keyPath);
        var perUser = hive == RegistryHive.CurrentUser;
        var uninstallString = AppRegistry.Text(key, "UninstallString");
        var quietString = AppRegistry.Text(key, "QuietUninstallString");
        var modifyPath = AppRegistry.Text(key, "ModifyPath");
        var productCode = ProductCode(keyName) ?? ProductCode(uninstallString);
        var isMsi = AppRegistry.Number(key, "WindowsInstaller") == 1 && ProductCode(keyName) is not null
            || IsMsiexec(uninstallString) && productCode is not null;

        var kind = Classify(key, keyName, uninstallString, isMsi);
        var group = AppRegistry.Number(key, "SystemComponent") == 1 ? AppGroup.Components
            : IsUpdate(key) ? AppGroup.Updates
            : kind == Kind.WingetPortable ? AppGroup.PackageManagers
            : AppGroup.Programs;

        // ---- Uninstall, change and repair, as Programs and Features offers them ----
        AppAction? uninstall = null, modify = null, repair = null;
        string? locked = null;
        string? problem = null, explanation = null;
        var noRemove = AppRegistry.Number(key, "NoRemove") == 1;
        var machineWide = hive == RegistryHive.LocalMachine;

        if (isMsi && productCode is not null)
        {
            uninstall = new CommandAction(Msiexec, $"/x {productCode}", Elevate: false, IsMsi: true);
            if (AppRegistry.Number(key, "NoModify") != 1)
                modify = new CommandAction(Msiexec, $"/i {productCode}", Elevate: false, IsMsi: true);
            if (AppRegistry.Number(key, "NoRepair") != 1)
                repair = new CommandAction(Msiexec, $"/fomus {productCode}", Elevate: false, IsMsi: true);
        }
        else if (uninstallString is not null)
        {
            var (program, arguments) = CommandLine.Resolve(uninstallString);
            // Steam and winget handle rights themselves; elevating them would start them as administrator.
            var elevate = machineWide && !Elevation.IsElevated && kind is not (Kind.Steam or Kind.WingetPortable or Kind.ClickOnce);
            if (program.Length > 0)
            {
                uninstall = new CommandAction(program, arguments, elevate);
                if (!File.Exists(program))
                {
                    problem = "Uninstaller not found";
                    explanation = $"The uninstaller it names ({program}) isn't there, so it can't be uninstalled the usual way. Often the program's folder was deleted by hand: if so, remove the entry. Reinstalling it and uninstalling again also works.";
                }
            }

            if (kind == Kind.WixBundle && AppRegistry.Text(key, "BundleCachePath") is { } bundle && File.Exists(bundle))
            {
                modify = new CommandAction(bundle, "/modify", elevate);
                repair = new CommandAction(bundle, "/repair", elevate);
            }
            else if (modifyPath is not null && AppRegistry.Number(key, "NoModify") != 1)
            {
                var (modifyProgram, modifyArguments) = CommandLine.Resolve(modifyPath);
                if (modifyProgram.Length > 0)
                    modify = IsMsiexec(modifyPath) && ProductCode(modifyPath) is { } code
                        ? new CommandAction(Msiexec, $"/i {code}", Elevate: false, IsMsi: true)
                        : new CommandAction(modifyProgram, modifyArguments, elevate);
            }
        }

        if (noRemove)
        {
            uninstall = null;
            locked = "Its installer says it can't be uninstalled on its own: it goes when what installed it is uninstalled.";
        }
        else if (uninstall is null)
        {
            locked = "Its entry doesn't say how to uninstall it.";

            // Components often have no uninstaller of their own: what they belong to removes them.
            if (group is AppGroup.Programs or AppGroup.PackageManagers)
            {
                problem ??= "No uninstaller";
                explanation ??= "The entry has no uninstall command. Something may have installed it as part of itself; if what it belongs to is gone, remove the entry.";
            }
        }

        // ---- Where it lives ----
        var location = Folder(AppRegistry.Text(key, "InstallLocation"))
            ?? Folder(AppRegistry.Text(key, "Inno Setup: App Path"))
            ?? Folder(AppRegistry.Text(key, "PortableTargetDirectory"));
        var named = AppRegistry.Text(key, "InstallLocation") is { } declared ? Unquote(declared) : null;
        // Windows Installer's InstallLocation is often where its setup unpacked, gone once it's done; msiexec uninstalls regardless.
        if (location is null && !isMsi && named is { Length: > 3 } && Path.IsPathFullyQualified(named) && !File.Exists(named) && problem is null)
        {
            problem = "Folder not found";
            explanation = $"The folder it says it's installed in ({named}) isn't there. It may have been deleted by hand, leaving the entry behind; or it may live somewhere else now.";
        }

        location ??= FolderOf(AppRegistry.Text(key, "DisplayIcon")) ?? (uninstall is CommandAction { IsMsi: false } command && kind is not (Kind.Steam or Kind.WingetPortable or Kind.ClickOnce)
            ? FolderOf(command.Program)
            : null);

        // ---- Facts ----
        var facts = new List<AppFact>();
        if (productCode is not null && isMsi)
            facts.Add(new AppFact("Product code", productCode));
        if (AppRegistry.Text(key, "WinGetPackageIdentifier") is { } wingetId)
            facts.Add(new AppFact("winget package", wingetId));
        if (AppRegistry.Text(key, "ParentDisplayName") is { } parent)
            facts.Add(new AppFact("Part of", parent));
        if (AppRegistry.Text(key, "Comments") is { } comments)
            facts.Add(new AppFact("Comments", comments));
        if (AppRegistry.Text(key, "Contact") is { } contact)
            facts.Add(new AppFact("Contact", contact));
        if (AppRegistry.Text(key, "InstallSource") is { } source)
            facts.Add(new AppFact("Installed from", source));
        if (uninstallString is not null)
            facts.Add(new AppFact("Uninstall command", uninstallString));
        if (quietString is not null)
            facts.Add(new AppFact("Quiet uninstall", quietString));
        if (modifyPath is not null)
            facts.Add(new AppFact("Change command", modifyPath));

        var estimated = AppRegistry.Number(key, "EstimatedSize");
        return new AppItem
        {
            Id = display,
            Group = group,
            Name = name,
            InstalledWith = KindText(kind),
            Publisher = AppRegistry.Text(key, "Publisher"),
            Version = AppRegistry.Text(key, "DisplayVersion"),
            InstalledOn = InstallDate(AppRegistry.Text(key, "InstallDate")) ?? AppRegistry.LastWritten(key),
            Size = estimated is > 0 ? (long)(uint)estimated.Value * 1024 : null,
            IsPerUser = perUser,
            Architecture = architecture,
            Location = location,
            About = About(group, kind, perUser),
            Facts = facts,
            RegistryKey = display,
            Website = Web(AppRegistry.Text(key, "URLInfoAbout")) ?? Web(AppRegistry.Text(key, "URLUpdateInfo")),
            HelpLink = Web(AppRegistry.Text(key, "HelpLink")),
            Uninstall = uninstall,
            Modify = modify,
            Repair = repair,
            Entry = new EntryRemoval(hive, keyPath),
            Locked = locked,
            Problem = problem,
            ProblemExplanation = explanation,
        };
    }

    private enum Kind
    {
        WindowsInstaller,
        InnoSetup,
        Nsis,
        InstallShield,
        WixBundle,
        Squirrel,
        ClickOnce,
        Steam,
        WingetPortable,
        Setup,
    }

    private static Kind Classify(RegistryKey key, string keyName, string? uninstall, bool isMsi)
    {
        if (string.Equals(AppRegistry.Text(key, "WinGetInstallerType"), "portable", StringComparison.OrdinalIgnoreCase))
            return Kind.WingetPortable;
        if (AppRegistry.Has(key, "BundleProviderKey") || AppRegistry.Has(key, "BundleCachePath") || AppRegistry.Has(key, "BundleUpgradeCode"))
            return Kind.WixBundle;
        if (isMsi)
            return Kind.WindowsInstaller;
        if (keyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase) || AppRegistry.Has(key, "Inno Setup: App Path"))
            return Kind.InnoSetup;

        var command = uninstall ?? string.Empty;
        if (command.Contains("dfshim.dll", StringComparison.OrdinalIgnoreCase))
            return Kind.ClickOnce;
        if (command.Contains("steam://", StringComparison.OrdinalIgnoreCase))
            return Kind.Steam;
        if (command.Contains("Update.exe", StringComparison.OrdinalIgnoreCase) && command.Contains("--uninstall", StringComparison.OrdinalIgnoreCase))
            return Kind.Squirrel;
        if (command.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)
            || (AppRegistry.Text(key, "LogFile") ?? string.Empty).EndsWith(".ilg", StringComparison.OrdinalIgnoreCase))
            return Kind.InstallShield;

        // NSIS: its default uninstaller name, or the one electron-builder gives it.
        var program = Path.GetFileName(CommandLine.Resolve(command).Program);
        if (program.Equals("uninst.exe", StringComparison.OrdinalIgnoreCase)
            || (program.StartsWith("Uninstall ", StringComparison.OrdinalIgnoreCase) && program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            return Kind.Nsis;

        return Kind.Setup;
    }

    private static string KindText(Kind kind) => kind switch
    {
        Kind.WindowsInstaller => "Windows Installer",
        Kind.InnoSetup => "Inno Setup",
        Kind.Nsis => "NSIS",
        Kind.InstallShield => "InstallShield",
        Kind.WixBundle => "WiX bundle",
        Kind.Squirrel => "Squirrel",
        Kind.ClickOnce => "ClickOnce",
        Kind.Steam => "Steam",
        Kind.WingetPortable => "winget (portable)",
        _ => "Setup program",
    };

    private static string About(AppGroup group, Kind kind, bool perUser)
    {
        var who = perUser ? "for you only" : "for everyone on this computer";
        return group switch
        {
            AppGroup.Components => $"Installed {who} as part of something else, so Windows doesn't list it. It's usually removed with what it belongs to.",
            AppGroup.Updates => "An update to a program, removed with it or on its own here.",
            _ => kind switch
            {
                Kind.WindowsInstaller => $"Installed {who} by Windows Installer, which can also change and repair it.",
                Kind.WixBundle => $"A WiX setup bundle installed {who}: one or more Windows Installer packages, uninstalled, changed and repaired together.",
                Kind.ClickOnce => "A ClickOnce app, installed for you only from a web page or share, and kept up to date from there.",
                Kind.Squirrel => "Installed for you only by Squirrel, which also updates it. Its uninstaller removes its folder.",
                Kind.Steam => "A Steam game: Steam uninstalls it.",
                Kind.WingetPortable => $"A portable program winget placed {who}: no setup ran, and winget removes its files and the command it added.",
                _ => $"Installed {who} by {(kind == Kind.Setup ? "a setup program" : KindText(kind))}. Its uninstaller removes it.",
            },
        };
    }

    private static bool IsUpdate(RegistryKey key)
    {
        if (AppRegistry.Has(key, "ParentKeyName"))
            return true;
        var release = AppRegistry.Text(key, "ReleaseType") ?? string.Empty;
        return release.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
            || release.Contains("Service Pack", StringComparison.OrdinalIgnoreCase);
    }

    private static string Msiexec => Path.Join(Environment.SystemDirectory, "msiexec.exe");

    private static bool IsMsiexec(string? command) =>
        command is not null && Path.GetFileName(CommandLine.Resolve(command).Program).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Windows Installer product code ({8-4-4-4-12}) in the text, braces included.</summary>
    private static string? ProductCode(string? text)
    {
        if (text is null)
            return null;

        var open = text.IndexOf('{');
        while (open >= 0 && open + 38 <= text.Length)
        {
            var candidate = text.Substring(open, 38);
            if (candidate[37] == '}' && Guid.TryParseExact(candidate, "B", out _))
                return candidate.ToUpperInvariant();
            open = text.IndexOf('{', open + 1);
        }

        return null;
    }

    private static DateTime? InstallDate(string? text) =>
        text is { Length: 8 } && DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>A folder that exists, from a value that may be quoted or end in a separator, or name the program in it.</summary>
    private static string? Folder(string? value)
    {
        if (value is null || Unquote(value) is not { Length: > 0 } path || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            if (File.Exists(path))
                path = Path.GetDirectoryName(path) ?? path;
            return Directory.Exists(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>The folder of a file named in a DisplayIcon ("path,0") or command, when it's the app's own (not Windows' or the installer cache).</summary>
    private static string? FolderOf(string? value)
    {
        if (value is null)
            return null;

        var path = Unquote(value.Split(',')[0]);
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            return null;

        var folder = Path.GetDirectoryName(path);
        if (folder is null || folder.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase)
            || folder.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
            || folder.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase)
            || Path.GetPathRoot(folder) is { } root && folder.Length <= root.Length)
            return null;

        return Folder(folder);
    }

    private static string Unquote(string value) => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"').Trim());

    /// <summary>Only web links: a registry value is never handed to the shell as something to run.</summary>
    internal static string? Web(string? value)
    {
        if (value is null)
            return null;

        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal) && text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri
            : null;
    }
}
