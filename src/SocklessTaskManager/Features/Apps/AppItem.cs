using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>How an app came to be installed, which decides how it's removed. The order is the page's group order.</summary>
internal enum AppGroup
{
    /// <summary>A setup program or Windows Installer package, listed in the uninstall registry.</summary>
    Programs,

    /// <summary>An MSIX package: from the Microsoft Store or a package file.</summary>
    Packaged,

    /// <summary>winget (portable), Scoop or Chocolatey: removed through the package manager.</summary>
    PackageManagers,

    /// <summary>Optional features and Windows features, added to Windows itself (DISM).</summary>
    Features,

    /// <summary>Frameworks, runtimes and parts of other apps: hidden unless asked for.</summary>
    Components,

    /// <summary>Updates to programs, listed under them in the uninstall registry: hidden unless asked for.</summary>
    Updates,
}

/// <summary>A label and value in the details pane.</summary>
public sealed record AppFact(string Label, string Value);

/// <summary>
/// One installed app, however it was installed: an uninstall entry, a package, a Scoop or
/// Chocolatey package, an optional feature. Read on the scanning thread, immutable after.
/// </summary>
internal sealed record AppItem
{
    /// <summary>Stable across scans: where it's registered and its key or name there.</summary>
    public required string Id { get; init; }

    public required AppGroup Group { get; init; }

    public required string Name { get; init; }

    /// <summary>What installed it, in a few words: "Windows Installer", "Inno Setup", "Microsoft Store", "Scoop".</summary>
    public required string InstalledWith { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    /// <summary>Local date it was installed (or last changed, when that's all there is).</summary>
    public DateTime? InstalledOn { get; init; }

    /// <summary>Bytes, as the installer estimated it. Null when it didn't say.</summary>
    public long? Size { get; init; }

    /// <summary>For this user only, rather than everyone on the computer.</summary>
    public bool IsPerUser { get; init; }

    /// <summary>"64-bit", "32-bit", "ARM64", "Neutral"; null when unknown.</summary>
    public string? Architecture { get; init; }

    /// <summary>The folder it's installed in, when known and present.</summary>
    public string? Location { get; init; }

    /// <summary>What this kind of app is and how it goes away, in a sentence or two.</summary>
    public string About { get; init; } = string.Empty;

    /// <summary>Facts particular to the kind: a product code, a package's family name, a bucket.</summary>
    public IReadOnlyList<AppFact> Facts { get; init; } = [];

    /// <summary>Its uninstall entry or package key, to open in Registry Editor.</summary>
    public string? RegistryKey { get; init; }

    /// <summary>The publisher's page for it (http or https only).</summary>
    public string? Website { get; init; }

    /// <summary>Where to get help (http or https only).</summary>
    public string? HelpLink { get; init; }

    /// <summary>How to uninstall it; null when it can't be from here (see <see cref="Locked"/>).</summary>
    public AppAction? Uninstall { get; init; }

    /// <summary>The installer's own change (modify) mode.</summary>
    public AppAction? Modify { get; init; }

    /// <summary>The installer's own repair mode.</summary>
    public AppAction? Repair { get; init; }

    /// <summary>The uninstall entry itself, removable when what it names is gone.</summary>
    public EntryRemoval? Entry { get; init; }

    /// <summary>What the uninstall button says: "Uninstall", "Remove", "Turn off".</summary>
    public string UninstallText { get; init; } = "Uninstall";

    /// <summary>Why it can't be uninstalled here, when it can't.</summary>
    public string? Locked { get; init; }

    /// <summary>"Uninstaller not found", "Folder not found", "Needs repair"; null when nothing stands out.</summary>
    public string? Problem { get; init; }

    /// <summary>What the problem means, in a sentence or two.</summary>
    public string? ProblemExplanation { get; init; }
}

/// <summary>A way to uninstall, change or repair an app. Each kind runs in its own way (<see cref="AppActions"/>).</summary>
internal abstract record AppAction;

/// <summary>
/// A program to start and wait for: an uninstaller, msiexec, a package manager. Its window takes it
/// from there; many uninstallers hand over to a copy of themselves and exit early, so the list
/// also follows the registry.
/// </summary>
/// <param name="Elevate">Ask for administrator rights at start (machine-wide setup programs that may not ask themselves).</param>
/// <param name="IsMsi">msiexec: its exit code says what happened.</param>
internal sealed record CommandAction(string Program, string Arguments, bool Elevate, bool IsMsi = false) : AppAction
{
    public string CommandLine => Arguments.Length > 0 ? $"{Quote(Program)} {Arguments}" : Quote(Program);

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}

/// <summary>An MSIX package removed for this user through the package manager.</summary>
internal sealed record PackageRemoval(string FullName) : AppAction;

/// <summary>An optional feature (a capability) removed through DISM.</summary>
internal sealed record CapabilityRemoval(string Name) : AppAction;

/// <summary>A Windows feature turned off through DISM.</summary>
internal sealed record FeatureRemoval(string Name) : AppAction;

/// <summary>An uninstall entry deleted from the registry, after saving it as a .reg file.</summary>
internal sealed record EntryRemoval(RegistryHive Hive, string Key)
{
    public bool NeedsAdmin => Hive == RegistryHive.LocalMachine;
}

/// <summary>What an action came to.</summary>
/// <param name="Failure">Null for success, else what went wrong in words.</param>
/// <param name="NeedsRestart">Windows needs a restart to finish.</param>
/// <param name="Backup">Where a removal saved what it removed.</param>
internal sealed record AppOutcome(string? Failure, bool NeedsRestart = false, string? Backup = null)
{
    public static readonly AppOutcome Done = new((string?)null);

    public bool Succeeded => Failure is null;
}
