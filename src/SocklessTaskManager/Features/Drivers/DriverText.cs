using System.Globalization;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>The Drivers page's words: problem codes, stop codes, kinds of change and crash.</summary>
internal static class DriverText
{
    /// <summary>Device Manager's problem codes (cfg.h CM_PROB_*), in a few words each.</summary>
    private static readonly Dictionary<uint, (string Short, string Explanation)> Problems = new()
    {
        [1] = ("Not configured", "Windows hasn't configured it. Reinstalling its driver usually fixes this."),
        [3] = ("Driver may be damaged", "Its driver may be damaged, or Windows is short of memory."),
        [9] = ("Invalid resources", "The firmware reported resources Windows can't use for it."),
        [10] = ("Can't start", "Windows or its driver couldn't start it. A newer driver often helps."),
        [12] = ("Not enough resources", "It can't get the resources it needs: another device may be using them."),
        [14] = ("Needs a restart", "It works after the computer restarts."),
        [16] = ("Resources not identified", "Windows can't identify all the resources it uses."),
        [18] = ("Driver needs reinstalling", "Its driver needs to be installed again."),
        [19] = ("Settings damaged", "Its configuration in the registry is incomplete or damaged."),
        [21] = ("Being removed", "Windows is removing it."),
        [22] = ("Turned off", "Someone turned it off in Device Manager."),
        [24] = ("Not present or not working", "It isn't there, isn't working, or doesn't have all its drivers."),
        [28] = ("No driver", "No driver is installed for it."),
        [29] = ("Off in firmware", "The firmware turned it off; it may be switched on in the UEFI or BIOS settings."),
        [31] = ("Driver won't load", "Windows can't load the drivers it needs."),
        [32] = ("Driver set not to start", "Its driver's service is disabled."),
        [33] = ("Resources unknown", "Windows can't work out which resources it needs."),
        [34] = ("Needs manual setup", "Its resources have to be set by hand."),
        [35] = ("Firmware incomplete", "The firmware doesn't describe it fully. A firmware update may help."),
        [36] = ("Interrupt conflict", "It asked for an interrupt Windows couldn't translate."),
        [37] = ("Driver failed to start", "Its driver returned an error when it started."),
        [38] = ("Earlier driver still loaded", "An earlier copy of its driver is still in memory. Restarting fixes this."),
        [39] = ("Driver missing or damaged", "Its driver is missing or damaged."),
        [40] = ("Driver registration damaged", "Its driver's service entry in the registry is missing or wrong."),
        [41] = ("Device not found by its driver", "Its driver loaded but couldn't find the hardware."),
        [42] = ("Duplicate device", "A copy of it is already running."),
        [43] = ("Stopped after reporting a problem", "Windows stopped it because it reported a problem."),
        [44] = ("Stopped by an app or service", "An app or service shut it down."),
        [45] = ("Not connected", "It isn't connected to the computer now."),
        [46] = ("Unavailable while shutting down", "Windows is shutting down."),
        [47] = ("Ready for safe removal", "It was prepared for safe removal; unplug it and plug it in again."),
        [48] = ("Driver blocked", "Its driver is blocked from starting because of known problems with it."),
        [49] = ("Registry too large", "The system hive is too large for more devices."),
        [50] = ("Properties not applied", "Windows couldn't apply all of its properties."),
        [51] = ("Waiting for another device", "It's waiting for a device it depends on to start."),
        [52] = ("Driver not signed", "Windows can't verify the digital signature of its driver."),
        [53] = ("Reserved for the debugger", "The kernel debugger is using it."),
        [54] = ("Resetting", "It failed and is being reset."),
        [56] = ("Still being set up", "Windows is still setting it up."),
    };

    /// <summary>Stop code names (bugcodes.h) for the codes people meet.</summary>
    private static readonly Dictionary<uint, (string Name, string? Explanation)> StopCodes = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "Kernel code touched memory it shouldn't have at a raised interrupt level: usually a driver, sometimes faulty memory."),
        [0x19] = ("BAD_POOL_HEADER", "The kernel's memory pool was damaged, usually by a driver."),
        [0x1A] = ("MEMORY_MANAGEMENT", "A memory management error: faulty RAM, or a driver corrupting memory."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "Kernel code raised an exception nothing handled, usually in a driver."),
        [0x24] = ("NTFS_FILE_SYSTEM", "The NTFS file system driver failed: often a failing disk."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "An exception while running a system call, often in a graphics or security driver."),
        [0x4E] = ("PFN_LIST_CORRUPT", "The memory manager's page list was damaged: a driver or faulty RAM."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Kernel code read memory that isn't there: a driver bug or faulty RAM."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Kernel data couldn't be read back from the page file: often a failing disk or cable."),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "Windows lost access to the disk it starts from: a storage driver or a firmware setting."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A system thread raised an exception nothing handled, usually in a driver."),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "The processor trapped in kernel mode: hardware, overclocking or a driver."),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", "The processor reported a hardware error."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver didn't finish a power change (sleep, wake, shutdown) in time."),
        [0xA0] = ("INTERNAL_POWER_ERROR", "The power manager hit an error, often while sleeping or hibernating."),
        [0xA5] = ("ACPI_BIOS_ERROR", "The firmware's ACPI tables aren't what Windows expects. A firmware update may help."),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "A driver wrote to read-only memory."),
        [0xC2] = ("BAD_POOL_CALLER", "A driver misused the kernel's memory pool."),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver Verifier caught a driver breaking the rules."),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "A driver damaged the kernel's memory pool."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory it shouldn't have at a raised interrupt level."),
        [0xD5] = ("DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "A driver used memory after freeing it."),
        [0xE2] = ("MANUALLY_INITIATED_CRASH", "Someone crashed Windows on purpose, from the keyboard or a debugger."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A process Windows can't run without stopped."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "A process or thread Windows needs ended: often a failing disk."),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "Kernel code tried to run data as code."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "A processor stopped responding: hardware, overclocking or firmware."),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Kernel code or data was changed: a driver, faulty RAM, or tampering."),
        [0x116] = ("VIDEO_TDR_FAILURE", "The display driver stopped responding and couldn't be restarted."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "The display driver stopped responding; Windows restarted it."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics scheduler found a fatal error, usually the display driver."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it couldn't correct: processor, memory, power or overclocking."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver kept a processor busy at a raised interrupt level for too long."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Kernel code found its data damaged, usually by a driver."),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "The kernel's heap was damaged, usually by a driver."),
        [0x141] = ("VIDEO_ENGINE_TIMEOUT_DETECTED", "A graphics engine stopped responding; Windows recovered it."),
        [0x144] = ("BUGCODE_USB3_DRIVER", "The USB 3 driver stack found a fatal error."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "The memory compression store failed: often a failing disk."),
        [0x15F] = ("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "Something kept the computer from settling into modern standby."),
        [0x193] = ("VIDEO_DXGKRNL_LIVEDUMP", "The graphics kernel recorded a problem and carried on."),
        [0x1A1] = ("WIN32K_CALLOUT_WATCHDOG_LIVEDUMP", "A graphics call took too long; Windows recorded it and carried on."),
        [0x1CC] = ("EXRESOURCE_TIMEOUT_LIVEDUMP", "A kernel lock was held too long; Windows recorded it and carried on."),
    };

    public static string Problem(uint? code) => code is { } c and > 0
        ? Problems.TryGetValue(c, out var problem) ? $"{problem.Short} (code {c})" : $"Problem code {c}"
        : "No problem";

    public static string ProblemExplanation(uint code) =>
        Problems.TryGetValue(code, out var problem) ? problem.Explanation : "Device Manager's help for this code says more.";

    /// <summary>"0x9F DRIVER_POWER_STATE_FAILURE"; the 0x1000_0000 variants read as the code they vary.</summary>
    public static string StopCode(uint code)
    {
        var plain = code & ~0x10000000u;
        return StopCodes.TryGetValue(plain, out var stop) ? $"0x{code:X} {stop.Name}" : $"0x{code:X}";
    }

    public static string? StopExplanation(uint code) => StopCodes.TryGetValue(code & ~0x10000000u, out var stop) ? stop.Explanation : null;

    public static string ChangeTitle(ChangeKind kind) => kind switch
    {
        ChangeKind.Installed => "Driver installed",
        ChangeKind.DriverChanged => "Driver changed",
        ChangeKind.Added => "New device",
        ChangeKind.Connected => "Connected",
        ChangeKind.Disconnected => "Disconnected",
        ChangeKind.Removed => "Removed",
        ChangeKind.Problem => "Problem",
        ChangeKind.ProblemCleared => "Working again",
        ChangeKind.NeedsInstall => "Needs installing",
        ChangeKind.FailedToLoad => "Didn't load",
        _ => kind.ToString(),
    };

    /// <summary>Brass for problems, verdigris for fixes; the badge always carries the word too.</summary>
    public static BadgeKind ChangeBadge(ChangeKind kind) => kind switch
    {
        ChangeKind.Problem or ChangeKind.FailedToLoad or ChangeKind.NeedsInstall => BadgeKind.Warning,
        ChangeKind.ProblemCleared or ChangeKind.Installed or ChangeKind.DriverChanged => BadgeKind.Success,
        ChangeKind.Removed => BadgeKind.Danger,
        _ => BadgeKind.Neutral,
    };

    public static string CrashTitle(CrashKind kind) => kind switch
    {
        CrashKind.BlueScreen => "Stop error",
        CrashKind.UnexpectedRestart => "Unexpected restart",
        CrashKind.DisplayReset => "Display driver reset",
        CrashKind.Recovered => "Recovered hang",
        _ => kind.ToString(),
    };

    public static string CrashAbout(CrashKind kind) => kind switch
    {
        CrashKind.BlueScreen => "Windows stopped with an error (a blue screen) and restarted. The stop code says what kind of failure it was; a driver named in the dump or Windows' report is the usual suspect.",
        CrashKind.UnexpectedRestart => "Windows restarted without shutting down and without a stop error: power was lost, the computer hung hard, or the power button was held.",
        CrashKind.DisplayReset => "The display driver stopped responding and Windows restarted it. The screen goes black for a moment; apps using the graphics card may close.",
        CrashKind.Recovered => "Hardware or a driver stopped responding, and Windows recovered without restarting. It wrote a live kernel report, usually for the graphics card.",
        _ => string.Empty,
    };

    public static BadgeKind CrashBadge(CrashKind kind) => kind switch
    {
        CrashKind.BlueScreen => BadgeKind.Danger,
        CrashKind.UnexpectedRestart => BadgeKind.Warning,
        _ => BadgeKind.Neutral,
    };

    public static string StartType(int start) => start switch
    {
        0 => "Boot (loaded by the boot loader)",
        1 => "System (while Windows starts)",
        2 => "Automatic",
        3 => "On demand",
        4 => "Disabled",
        _ => start.ToString(CultureInfo.InvariantCulture),
    };

    public static string SignatureText(Signature signature) => signature.State switch
    {
        SignatureState.Pending => "Checking…",
        SignatureState.Signed => signature.ByCatalog ? "Signed (by a Windows catalog)" : "Signed",
        SignatureState.Unsigned => "Not signed",
        SignatureState.Invalid => signature.Signer is { } signer ? $"Doesn't verify (says {signer})" : "Doesn't verify",
        SignatureState.Unreadable => "Couldn't be read to check",
        _ => "—",
    };

    /// <summary>A short word for the table: "Unsigned", "Doesn't verify", "Not found"; empty when there's nothing to check.</summary>
    public static string Look(DriverFile file) =>
        !file.Exists ? "Not found"
        : file.Signature.State == SignatureState.Unsigned ? "Unsigned"
        : file.Signature.State == SignatureState.Invalid ? "Doesn't verify"
        : string.Empty;

    public static string LookExplanation(DriverFile file) =>
        !file.Exists ? "The file this driver was loaded from isn't where Windows says it is. It may have been replaced or removed since it loaded."
        : file.Signature.State == SignatureState.Unsigned ? "Windows normally loads only signed kernel drivers. An unsigned one means test signing is on, or it was loaded some other way."
        : file.Signature.State == SignatureState.Invalid ? "The file's signature doesn't verify: it was changed after signing, or the certificate is revoked or distrusted."
        : string.Empty;

    public static string Date(DateTime? utc) => utc is { } d ? d.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) : "—";

    /// <summary>Driver dates are calendar dates (midnight UTC): shown as is, not shifted to local time.</summary>
    public static string DriverDate(DateTime? date) => date is { } d ? d.ToString("d", CultureInfo.CurrentCulture) : "—";

    public static string Time(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>A badge's status colour. Always paired with a word.</summary>
public enum BadgeKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Info,
}
