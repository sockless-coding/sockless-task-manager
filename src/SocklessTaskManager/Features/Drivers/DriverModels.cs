using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>A label and value in the details pane.</summary>
public sealed record DriverFact(string Label, string Value);

/// <summary>A titled group of facts in the details pane.</summary>
public sealed record DriverFactGroup(string Title, IReadOnlyList<DriverFact> Facts);

/// <summary>A kernel module loaded right now, with its file and the service that loaded it.</summary>
/// <param name="Path">The .sys (or the kernel's .exe and .dll) on disk.</param>
/// <param name="Service">The driver service whose image it is; null for modules loaded by another driver or the kernel.</param>
internal sealed record LoadedModule(string Name, string Path, ulong ImageBytes, DriverService? Service, DriverFile File);

/// <summary>A driver file as read from disk: version resource and signature.</summary>
internal sealed record DriverFile(string Path, bool Exists, string? Company, string? Description, string? Version, DateTime? Modified, Signature Signature)
{
    public static DriverFile Missing(string path) => new(path, false, null, null, null, null, Signature.None);

    /// <summary>
    /// Signed by Microsoft as part of Windows, as the signature (not the file's own claim) says. Other
    /// companies' drivers that passed Microsoft's hardware program are signed "Microsoft Windows
    /// Hardware Compatibility Publisher": those aren't Windows' own.
    /// </summary>
    public bool IsWindows => Signature is { State: SignatureState.Signed, Signer: { } signer }
        && signer.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase)
        && !IsHardwareProgram;

    /// <summary>Signed by Microsoft for another company, through the Windows hardware program (WHQL or attestation).</summary>
    public bool IsHardwareProgram => Signature is { State: SignatureState.Signed, Signer: { } signer }
        && signer.Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase);

    /// <summary>Unsigned, doesn't verify, or not there: worth a look.</summary>
    public bool NeedsLook => !Exists || Signature.State is SignatureState.Unsigned or SignatureState.Invalid;
}

/// <param name="IsChecking">Signatures are still being verified; another snapshot follows.</param>
internal sealed record DriverSnapshot(
    IReadOnlyList<DeviceInfo> Devices,
    IReadOnlyList<LoadedModule> Modules,
    IReadOnlyDictionary<string, DriverService> Services,
    IReadOnlyDictionary<string, DriverFile> Files,
    bool IsChecking);

/// <summary>A driver update Windows Update offers.</summary>
/// <param name="HardwareId">The device id it's for; matched against devices' hardware and compatible ids.</param>
/// <param name="IsOptional">Settings lists it under optional updates; Windows won't install it on its own.</param>
internal sealed record DriverUpdate(
    string UpdateId,
    string Title,
    string? Description,
    string? HardwareId,
    string? Provider,
    string? Manufacturer,
    string? Model,
    string? DriverClass,
    DateTime? Date,
    bool IsOptional,
    bool IsDownloaded);

/// <summary>What happened to a device or driver. The order is meaningless; the history sorts by time.</summary>
internal enum ChangeKind
{
    /// <summary>A driver was installed on a device (Kernel-PnP 400).</summary>
    Installed,

    /// <summary>The device's driver changed while monitoring.</summary>
    DriverChanged,

    /// <summary>A device Windows hadn't seen this session appeared.</summary>
    Added,

    Connected,
    Disconnected,

    /// <summary>The device was uninstalled (Kernel-PnP 420).</summary>
    Removed,

    /// <summary>A problem code appeared, live or as Kernel-PnP 411 (problem starting).</summary>
    Problem,

    ProblemCleared,

    /// <summary>Windows needs more installation for it (Kernel-PnP 430).</summary>
    NeedsInstall,

    /// <summary>A driver didn't load (Kernel-PnP 219, Service Control Manager 7026).</summary>
    FailedToLoad,
}

/// <param name="InstanceId">The device, when it's about one; its name is looked up when shown.</param>
/// <param name="Subject">What it's about when there's no device, or the name as last known.</param>
/// <param name="IsLive">Seen by this app while running, rather than read from the log.</param>
internal sealed record DriverChange(DateTime Time, ChangeKind Kind, string? InstanceId, string Subject, string Detail, bool IsLive)
{
    public string Id => $"{(IsLive ? "live" : "log")}|{Time.Ticks}|{Kind}|{InstanceId ?? Subject}";
}

internal enum CrashKind
{
    /// <summary>A stop error: Windows halted and restarted.</summary>
    BlueScreen,

    /// <summary>Restarted without shutting down, with no stop error recorded: power lost, a hard hang, the power button held.</summary>
    UnexpectedRestart,

    /// <summary>The display driver stopped responding and Windows restarted it (TDR).</summary>
    DisplayReset,

    /// <summary>A live kernel event: hardware or a driver hung and Windows recovered without restarting.</summary>
    Recovered,
}

/// <param name="Code">The stop code (or live kernel event code), when one was recorded.</param>
/// <param name="Driver">The driver file blamed ("nvlddmkm.sys"), when one could be.</param>
/// <param name="Blame">How the driver was found, in words.</param>
/// <param name="Dump">The memory dump written for it, when there is one.</param>
internal sealed record DriverCrash(
    DateTime Time,
    CrashKind Kind,
    uint? Code,
    IReadOnlyList<ulong> Parameters,
    string? Driver,
    string? Blame,
    string? Dump,
    string? ReportId)
{
    public string Id => $"{Time.Ticks}|{Kind}|{Code}";
}

/// <param name="NeedsElevation">Dumps exist, or may, that only an administrator can read.</param>
internal sealed record CrashSnapshot(IReadOnlyList<DriverCrash> Crashes, bool NeedsElevation);
