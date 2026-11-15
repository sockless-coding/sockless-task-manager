namespace Sockless.TaskManager.Features.Autostart;

/// <summary>How entries read on the page, and the judgements behind the filters: Windows' own, and worth a look.</summary>
internal static class AutostartText
{
    private static readonly string WindowsFolder = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    public static string CategoryTitle(AutostartCategory category) => category switch
    {
        AutostartCategory.SignIn => "Sign-in",
        AutostartCategory.ScheduledTasks => "Scheduled tasks",
        AutostartCategory.Services => "Services",
        AutostartCategory.Drivers => "Drivers",
        AutostartCategory.Explorer => "Explorer add-ins",
        AutostartCategory.Browser => "Browser add-ins",
        AutostartCategory.Office => "Office add-ins",
        AutostartCategory.Winlogon => "Sign-in process",
        AutostartCategory.Boot => "Boot",
        AutostartCategory.ProgramLaunch => "Program launch",
        AutostartCategory.LoadedDlls => "DLLs loaded into programs",
        AutostartCategory.SecurityProviders => "Security providers",
        AutostartCategory.Network => "Network providers",
        AutostartCategory.Printing => "Print providers",
        AutostartCategory.Wmi => "WMI subscriptions",
        AutostartCategory.CommandShells => "Command shells",
        _ => "Other",
    };

    public static string CategoryAbout(AutostartCategory category) => category switch
    {
        AutostartCategory.SignIn => "Programs that start when you, or anyone, signs in: what Task Manager's Startup apps lists, and more",
        AutostartCategory.ScheduledTasks => "Programs Task Scheduler starts at startup, at sign-in, on a schedule or on an event",
        AutostartCategory.Services => "Services Windows starts on its own, before anyone signs in",
        AutostartCategory.Drivers => "Drivers Windows loads while it starts",
        AutostartCategory.Explorer => "Code File Explorer loads into itself",
        AutostartCategory.Browser => "Code the browser loads into itself",
        AutostartCategory.Office => "Add-ins Office programs load",
        AutostartCategory.Winlogon => "What Windows' sign-in process starts and loads",
        AutostartCategory.Boot => "Programs that run while Windows starts, before services",
        AutostartCategory.ProgramLaunch => "What runs when a program starts, exits or crashes, or a program file is opened",
        AutostartCategory.LoadedDlls => "DLLs Windows loads into many programs",
        AutostartCategory.SecurityProviders => "DLLs loaded by the Local Security Authority, which handles sign-ins and passwords",
        AutostartCategory.Network => "DLLs Windows loads for networking",
        AutostartCategory.Printing => "DLLs the Print Spooler loads",
        AutostartCategory.Wmi => "Commands WMI runs when an event happens",
        AutostartCategory.CommandShells => "What runs when Command Prompt or PowerShell starts",
        _ => "Other things Windows starts on its own",
    };

    /// <summary>
    /// Windows' own: signed by Microsoft and either under the Windows folder or signed as part of
    /// Windows. Microsoft's apps (OneDrive, Edge, Teams) don't count: they're things a person may
    /// want to turn off. Until the signature is checked, the version resource and folder stand in.
    /// </summary>
    public static bool IsWindows(AutostartEntry entry)
    {
        if (entry.Item.IsKnownWindows)
            return true;
        if (entry.File is not { Exists: true } file)
            return false;

        var underWindows = IsUnder(file.Path, WindowsFolder);
        return file.Signature.State switch
        {
            SignatureState.Signed => file.Signature.Signer is { } signer
                && signer.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                && (underWindows || signer.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase)),
            SignatureState.Pending => underWindows && file.Company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true,
            _ => false,
        };
    }

    /// <summary>"Unsigned", "Not found", "Doesn't verify"; empty when nothing stands out.</summary>
    public static string Look(AutostartEntry entry) => entry.File switch
    {
        null => string.Empty,
        { Exists: false } => "Not found",
        { Signature.State: SignatureState.Unsigned } => "Unsigned",
        { Signature.State: SignatureState.Invalid } => "Doesn't verify",
        _ => string.Empty,
    };

    public static string LookExplanation(AutostartEntry entry) => Look(entry) switch
    {
        "Not found" => "The file it names isn't there. Often a leftover of something uninstalled, and safe to remove; or something that would run if a file appeared there.",
        "Unsigned" => "The file has no digital signature, so nothing says who made it or that it's unchanged. Plenty of honest software is unsigned; check that you know what it is.",
        "Doesn't verify" => "The file has a signature that doesn't check out: the file changed after it was signed, or the certificate isn't trusted.",
        _ => string.Empty,
    };

    /// <summary>The verified signer; else the company the file claims, marked as unverified.</summary>
    public static string Publisher(AutostartEntry entry) => entry.File switch
    {
        null => "—",
        { Exists: false } => "—",
        { Signature: { State: SignatureState.Signed, Signer: { } signer } } => signer,
        { Company: { } company } => $"{company} (not verified)",
        _ => "—",
    };

    public static string SignatureText(Signature signature) => signature.State switch
    {
        SignatureState.Pending => "Checking…",
        SignatureState.Signed => signature.ByPackage ? "Signed by its app package, which Windows checked when it was installed"
            : signature.ByCatalog ? "Signed (by a Windows catalog)"
            : "Signed",
        SignatureState.Unsigned => "Not signed",
        SignatureState.Invalid => signature.Signer is { } signer ? $"Doesn't verify (says {signer})" : "Doesn't verify",
        SignatureState.Unreadable => "Couldn't be read to check",
        _ => "—",
    };

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 0
        && path.Length > folder.Length
        && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && path[folder.Length] == Path.DirectorySeparatorChar;
}
