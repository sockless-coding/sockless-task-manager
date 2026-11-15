using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>Where an entry starts from. The order is the page's group order.</summary>
internal enum AutostartCategory
{
    SignIn,
    ScheduledTasks,
    Services,
    Drivers,
    Explorer,
    Browser,
    Office,
    Winlogon,
    Boot,
    ProgramLaunch,
    LoadedDlls,
    SecurityProviders,
    Network,
    Printing,
    Wmi,
    CommandShells,
    Other,
}

/// <summary>A label and value in the details pane.</summary>
public sealed record AutostartFact(string Label, string Value);

/// <summary>
/// Something Windows starts on its own: a Run value, a shortcut in a Startup folder, a scheduled
/// task, a service, a DLL a system component loads. Read on the scanning thread, immutable after.
/// </summary>
internal sealed record AutostartItem
{
    /// <summary>Stable across scans: category, location and name.</summary>
    public required string Id { get; init; }

    public required AutostartCategory Category { get; init; }

    /// <summary>What the entry is called where it's registered: a value name, file name, task or service name.</summary>
    public required string Name { get; init; }

    /// <summary>Where it's registered, as a person would look for it: a registry key, folder or task folder.</summary>
    public required string Location { get; init; }

    /// <summary>What runs, as registered: a command line or a DLL path. Empty when there's nothing to run.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>The file that runs or is loaded, resolved from <see cref="Command"/>; checked for a signature.</summary>
    public string? Image { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>Starts only for this user (HKCU, the user's Startup folder), not everyone.</summary>
    public bool IsPerUser { get; init; }

    /// <summary>What this kind of entry does, in a sentence or two: when it runs, and for whom.</summary>
    public string About { get; init; } = string.Empty;

    /// <summary>Facts particular to the kind: a task's triggers, a service's account.</summary>
    public IReadOnlyList<AutostartFact> Facts { get; init; } = [];

    /// <summary>How to turn it off and on again; null when there's no safe way.</summary>
    public AutostartTarget? Toggle { get; init; }

    /// <summary>How to remove it for good; null when it can't be removed from here.</summary>
    public AutostartTarget? Removal { get; init; }

    /// <summary>Why it can't be turned off or removed, when it can't.</summary>
    public string? Locked { get; init; }

    /// <summary>The registry key to open in Registry Editor, when it lives in the registry.</summary>
    public string? RegistryKey { get; init; }

    /// <summary>
    /// A packaged app's publisher. Its files aren't signed one by one: Windows checks the package's
    /// signature when it's installed, and the WindowsApps folder can't be written to after.
    /// </summary>
    public string? PackageSigner { get; init; }

    /// <summary>A Windows default that has no file of its own to check (the WMI event log consumer).</summary>
    public bool IsKnownWindows { get; init; }
}

/// <summary>What a change acts on. Each kind is turned off, on or removed in its own way (<see cref="AutostartChanges"/>).</summary>
internal abstract record AutostartTarget
{
    /// <summary>Changing it needs administrator rights (machine-wide registry, services, other users' tasks).</summary>
    public bool NeedsAdmin { get; init; }
}

/// <summary>
/// Run values and Startup folder files, switched the way Task Manager and Settings do: a value under
/// Explorer\StartupApproved whose first byte is even when on and odd when off.
/// </summary>
internal sealed record ApprovedSwitch(RegistryHive Hive, string List, string ValueName) : AutostartTarget;

/// <summary>A packaged app's startup task: its State under the app's SystemAppData key.</summary>
internal sealed record PackagedTaskSwitch(string KeyPath) : AutostartTarget;

/// <summary>A scheduled task, by its path in the Task Scheduler library; its XML is saved before it's deleted.</summary>
internal sealed record TaskTarget(string Path, string Xml) : AutostartTarget;

/// <summary>A service or driver: turned off by setting its startup type to Disabled, and back to what it was.</summary>
internal sealed record ServiceTarget(string Name, int Start, bool Delayed) : AutostartTarget;

/// <summary>A registry value; removing it saves a .reg file first.</summary>
internal sealed record RegistryValueTarget(RegistryHive Hive, string Key, string ValueName) : AutostartTarget;

/// <summary>A registry key and everything under it; removing it saves a .reg file first.</summary>
internal sealed record RegistryKeyTarget(RegistryHive Hive, string Key) : AutostartTarget;

/// <summary>
/// One item in a list held in a single value (comma-separated, space-separated or REG_MULTI_SZ):
/// removing it rewrites the value without it, after saving a .reg file of the whole value.
/// </summary>
internal sealed record ListItemTarget(RegistryHive Hive, string Key, string ValueName, string Item, char Separator) : AutostartTarget;

/// <summary>
/// A value moved aside to turn it off: into a "Sockless disabled" key beside it, where Windows
/// doesn't look, and back again to turn it on.
/// </summary>
internal sealed record MovedValueTarget(RegistryHive Hive, string Key, string ValueName) : AutostartTarget;

/// <summary>A key moved aside to turn it off, like <see cref="MovedValueTarget"/>.</summary>
internal sealed record MovedKeyTarget(RegistryHive Hive, string Parent, string Name) : AutostartTarget;

/// <summary>A file, moved to the backups folder when removed.</summary>
internal sealed record FileTarget(string Path) : AutostartTarget;

/// <summary>A WMI event subscription: the binding, and the consumer and filter it ties together.</summary>
internal sealed record WmiTarget(string Binding, string Consumer, string Filter) : AutostartTarget;

/// <summary>An Office add-in, switched by its LoadBehavior value.</summary>
internal sealed record OfficeAddinSwitch(RegistryHive Hive, string Key) : AutostartTarget;

/// <summary>A file that runs or is loaded, as read from disk.</summary>
/// <param name="Path">Full path.</param>
/// <param name="Exists">False when the registration points at nothing: often a leftover, sometimes a hijack waiting for a file.</param>
internal sealed record AutostartFile(
    string Path,
    bool Exists,
    string? Company,
    string? Description,
    string? Product,
    string? Version,
    DateTime? Modified,
    Signature Signature);

internal enum SignatureState
{
    /// <summary>Not verified yet: the list shows before every file is checked.</summary>
    Pending,
    Signed,
    Unsigned,

    /// <summary>A signature that doesn't verify: a changed file, a distrusted or revoked certificate.</summary>
    Invalid,

    /// <summary>No file to check.</summary>
    None,

    /// <summary>The file is there but can't be opened to check (in use exclusively, or denied).</summary>
    Unreadable,
}

/// <param name="Signer">The certificate's subject, when signed.</param>
/// <param name="ByCatalog">Signed through a Windows catalog rather than in the file itself.</param>
internal sealed record Signature(SignatureState State, string? Signer = null, bool ByCatalog = false)
{
    /// <summary>Signed as part of an app package (see <see cref="AutostartItem.PackageSigner"/>).</summary>
    public bool ByPackage { get; init; }

    public static readonly Signature Pending = new(SignatureState.Pending);
    public static readonly Signature None = new(SignatureState.None);
}
