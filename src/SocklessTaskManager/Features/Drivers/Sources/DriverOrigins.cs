using System.Diagnostics;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>Where a driver came from. The order is the "by source" grouping's order.</summary>
internal enum OriginKind
{
    /// <summary>Shipped with Windows; updated with Windows itself.</summary>
    Windows,

    /// <summary>Installed by Windows Update.</summary>
    WindowsUpdate,

    /// <summary>Came with an installed program or package (the uninstall registry names it).</summary>
    Program,

    /// <summary>Came with a Store app (its INF was in the app's WindowsApps folder).</summary>
    StoreApp,

    /// <summary>An installer unpacked it to a temporary folder and installed it from there.</summary>
    Installer,

    /// <summary>Installed from an INF in an ordinary folder: by hand, or an installer's own folder.</summary>
    Folder,

    /// <summary>Added before the oldest setup log Windows kept, with no other record.</summary>
    Unknown,
}

/// <summary>A program in the uninstall registry: a vendor's driver package or the app a driver came with.</summary>
internal sealed record InstalledPackage(string Name, string? Version, string? Publisher, string? InstallLocation);

/// <summary>Which drivers a vendor's own update tool looks after.</summary>
internal enum ToolScope
{
    Nvidia,
    AmdGraphics,
    Intel,
    IntelGraphics,

    /// <summary>A computer or motherboard maker's tool: every driver it shipped for the machine.</summary>
    ComputerMaker,
}

/// <summary>A vendor's update tool on this computer, opened through its Start menu shortcut.</summary>
internal sealed record VendorTool(string Name, string Shortcut, ToolScope Scope);

/// <param name="Title">A few words for the table: "Windows Update", "NVIDIA Graphics Driver 610.88".</param>
/// <param name="Detail">How that was worked out, in a sentence.</param>
/// <param name="Source">The INF path it was installed from, when the log says.</param>
/// <param name="Package">The installed program it came with, when one matches.</param>
/// <param name="Tools">Vendor tools that look after this driver; Windows Update is implied for Windows and Windows Update sources.</param>
internal sealed record DriverOrigin(
    OriginKind Kind,
    string Title,
    string Detail,
    DateTime? Installed,
    string? Source,
    InstalledPackage? Package,
    IReadOnlyList<VendorTool> Tools);

/// <summary>
/// Works out where each driver came from, from what Windows keeps locally (nothing goes online):
/// <list type="bullet">
/// <item>the setup log (<see cref="SetupLog"/>): which INF each oemNN.inf was published from, and by
/// what (Windows Update, an installer, a Store app);</item>
/// <item>Windows Update's history, for drivers older than the log (matched by version in the title);</item>
/// <item>the uninstall registry: the program whose folder the INF was in, or a vendor's per-driver
/// entry with the same version ("AMD PSP Driver 5.39.0.0"), or NVIDIA's package by its driver
/// number;</item>
/// <item>the Start menu, for vendors' update tools (NVIDIA App, Intel Driver &amp; Support Assistant,
/// AMD Software, computer makers' updaters).</item>
/// </list>
/// </summary>
internal sealed class DriverOrigins
{
    /// <summary>Start menu names (without ® and ™), each the start of a shortcut's name.</summary>
    private static readonly (string Name, ToolScope Scope)[] KnownTools =
    [
        ("NVIDIA App", ToolScope.Nvidia),
        ("GeForce Experience", ToolScope.Nvidia),
        ("AMD Software", ToolScope.AmdGraphics),
        ("Radeon Software", ToolScope.AmdGraphics),
        ("Intel Driver & Support Assistant", ToolScope.Intel),
        ("Intel Graphics Software", ToolScope.IntelGraphics),
        ("Intel Arc Control", ToolScope.IntelGraphics),
        ("Lenovo System Update", ToolScope.ComputerMaker),
        ("Lenovo Vantage", ToolScope.ComputerMaker),
        ("Dell Command Update", ToolScope.ComputerMaker),
        ("Dell SupportAssist", ToolScope.ComputerMaker),
        ("SupportAssist", ToolScope.ComputerMaker),
        ("HP Support Assistant", ToolScope.ComputerMaker),
        ("HP Image Assistant", ToolScope.ComputerMaker),
        ("MSI Center", ToolScope.ComputerMaker),
        ("MSI Driver Utility", ToolScope.ComputerMaker),
        ("Armoury Crate", ToolScope.ComputerMaker),
        ("MyASUS", ToolScope.ComputerMaker),
        ("GIGABYTE Control Center", ToolScope.ComputerMaker),
        ("APP Shop", ToolScope.ComputerMaker),
    ];

    private static readonly TimeSpan HistoryLifetime = TimeSpan.FromMinutes(10);

    private readonly SetupLog _log = new();
    private List<(DateTime Time, string Title)>? _history;
    private DateTime _historyRead;

    /// <summary>Reads the sources; the result resolves each device and module.</summary>
    public OriginIndex Read(bool needsHistory)
    {
        var (records, since) = Safe(_log.Read, ([], null));

        // The latest publication wins: oem numbers are reused after a package is removed.
        var byInf = new Dictionary<string, PackageRecord>(StringComparer.OrdinalIgnoreCase);
        var byPackage = new Dictionary<string, PackageRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            byInf[record.Inf] = record;
            byPackage[record.Package] = record;
        }

        if (needsHistory && (_history is null || DateTime.UtcNow - _historyRead > HistoryLifetime))
        {
            _history = Safe(WindowsUpdate.History, []);
            _historyRead = DateTime.UtcNow;
        }

        return new OriginIndex(byInf, byPackage, since, _history ?? [], Safe(Packages, []), Safe(Tools, []));
    }

    private static List<InstalledPackage> Packages()
    {
        var packages = new List<InstalledPackage>();
        foreach (var (_, _, _, key, _) in UninstallRegistry.Entries())
        {
            if (UninstallRegistry.Text(key, "DisplayName") is { } name)
            {
                packages.Add(new InstalledPackage(name, UninstallRegistry.Text(key, "DisplayVersion"),
                    UninstallRegistry.Text(key, "Publisher"), UninstallRegistry.Text(key, "InstallLocation")));
            }
        }

        return packages;
    }

    /// <summary>Vendors' update tools with a Start menu shortcut, for everyone or this user.</summary>
    private static List<VendorTool> Tools()
    {
        var tools = new List<VendorTool>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };

        foreach (var root in roots.Where(r => r.Length > 0 && Directory.Exists(r)))
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 })
                    .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var name = Plain(Path.GetFileNameWithoutExtension(shortcut));
                foreach (var (known, scope) in KnownTools)
                {
                    if (name.StartsWith(known, StringComparison.OrdinalIgnoreCase) && !tools.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        tools.Add(new VendorTool(name, shortcut, scope));
                        break;
                    }
                }
            }
        }

        return tools;
    }

    /// <summary>"Intel® Graphics Software" as "Intel Graphics Software".</summary>
    private static string Plain(string text) => text.Replace("®", string.Empty).Replace("™", string.Empty).Replace("  ", " ").Trim();

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Driver origins source failed: {ex}");
            return fallback;
        }
    }
}

/// <summary>What <see cref="DriverOrigins"/> read, to resolve each driver against.</summary>
internal sealed class OriginIndex(
    Dictionary<string, PackageRecord> byInf,
    Dictionary<string, PackageRecord> byPackage,
    DateTime? logSince,
    List<(DateTime Time, string Title)> history,
    List<InstalledPackage> packages,
    List<VendorTool> tools)
{
    private static readonly string FileRepository = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;

    public IReadOnlyList<VendorTool> Tools => tools;

    /// <summary>A device's driver. Null when it has none.</summary>
    public DriverOrigin? Resolve(DeviceInfo device)
    {
        if (!device.HasDriver || device.Inf is not { } inf)
            return null;

        var tools = ToolsFor(device.Provider, device.ClassTitle, device.Name, device.IsThirdParty);
        if (!device.IsThirdParty)
            return new DriverOrigin(OriginKind.Windows, "Windows", "Its INF shipped with Windows, so Windows' own updates keep it current.", null, null, null, []);

        return byInf.TryGetValue(inf, out var record)
            ? FromRecord(record, device.Provider, device.Version, tools)
            : WithoutRecord(device.Provider, device.Version, tools);
    }

    /// <summary>
    /// A loaded module: its store package when it runs from the driver store, else the device whose
    /// service it is, else Windows when Windows signed it, else a matching package.
    /// </summary>
    public DriverOrigin? Resolve(LoadedModule module, DeviceInfo? device, DriverOrigin? deviceOrigin)
    {
        var file = module.File;
        var tools = ToolsFor(file.Company, device?.ClassTitle, file.Description, !file.IsWindows);
        if (module.Path.StartsWith(FileRepository, StringComparison.OrdinalIgnoreCase))
        {
            var package = module.Path[FileRepository.Length..].Split(Path.DirectorySeparatorChar)[0];
            if (byPackage.TryGetValue(package, out var record))
                return FromRecord(record, file.Company, file.Version, tools);
        }

        if (deviceOrigin is not null)
            return deviceOrigin;

        // Copied out of the store (System32\drivers): the package named after the file, if it still holds it.
        var stem = Path.GetFileNameWithoutExtension(module.Name) + ".inf_";
        var copied = byPackage.Values
            .Where(r => r.Package.StartsWith(stem, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(FileRepository, r.Package, module.Name)))
            .MaxBy(r => r.Time);
        if (copied is not null)
            return FromRecord(copied, file.Company, file.Version, tools);

        if (file.IsWindows)
            return new DriverOrigin(OriginKind.Windows, "Windows", "Signed by Microsoft as part of Windows, so Windows' own updates keep it current.", null, null, null, []);

        return WithoutRecord(file.Company, file.Version, tools);
    }

    private DriverOrigin FromRecord(PackageRecord record, string? vendor, string? version, IReadOnlyList<VendorTool> tools)
    {
        var how = $"Windows' setup log: {record.Action}.";
        if (record.IsWindowsUpdate || record.Source?.Contains(@"\SoftwareDistribution\", StringComparison.OrdinalIgnoreCase) == true)
            return new DriverOrigin(OriginKind.WindowsUpdate, "Windows Update", $"Windows Update installed it. {how}", record.Time, record.Source, null, tools);

        if (record.Source is not { } source || !source.Contains('\\'))
        {
            // Sections without a path: Windows installing or migrating driver updates itself.
            return new DriverOrigin(OriginKind.Windows, "Windows", $"Windows installed it itself. {how}", record.Time, record.Source, null, tools);
        }

        // A Store app's folder: "C:\Program Files\WindowsApps\Microsoft.GamingServices_38.116…_x64__8wekyb3d8bbwe\drivers\xvdd.inf".
        var apps = source.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        if (apps >= 0)
        {
            var folder = source[(apps + 13)..].Split('\\')[0];
            var app = folder.Split('_')[0];
            return new DriverOrigin(OriginKind.StoreApp, app, $"It came with the {app} app, from the app's folder. {how}", record.Time, source, null, tools);
        }

        // The folder of an installed program (an antivirus, a hardware utility).
        var owner = packages
            .Where(p => p.InstallLocation is { Length: > 3 } location && source.StartsWith(Path.TrimEndingDirectorySeparator(location) + "\\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.InstallLocation!.Length)
            .FirstOrDefault();
        if (owner is not null)
            return new DriverOrigin(OriginKind.Program, Named(owner), $"It came with {owner.Name}, from the program's folder. {how}", record.Time, source, owner, tools);

        var package = Package(vendor, version);
        var temporary = source.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) || source.Contains(@"\Tmp\", StringComparison.OrdinalIgnoreCase);
        if (package is not null)
        {
            return new DriverOrigin(OriginKind.Program, Named(package),
                $"{package.Name} installed it{(temporary ? ", unpacking it to a temporary folder first" : string.Empty)}: the installed version matches. {how}",
                record.Time, source, package, tools);
        }

        return temporary
            ? new DriverOrigin(OriginKind.Installer, vendor is null ? "An installer" : $"{Short(vendor)} installer", $"An installer unpacked it to a temporary folder and installed it from there. {how}", record.Time, source, null, tools)
            : new DriverOrigin(OriginKind.Folder, Folder(source), $"Installed from an INF in {Path.GetDirectoryName(source)}: by hand, or by a program from its own folder. {how}", record.Time, source, null, tools);
    }

    /// <summary>Older than the setup log: Windows Update's history, a matching package, or nothing.</summary>
    private DriverOrigin WithoutRecord(string? vendor, string? version, IReadOnlyList<VendorTool> tools)
    {
        if (version is not null)
        {
            var update = history
                .Where(h => h.Title.EndsWith($"({version})", StringComparison.OrdinalIgnoreCase) && SameVendor(h.Title, vendor))
                .OrderByDescending(h => h.Time)
                .Cast<(DateTime Time, string Title)?>()
                .FirstOrDefault();
            if (update is { } found)
                return new DriverOrigin(OriginKind.WindowsUpdate, "Windows Update", $"Windows Update's history has it: {found.Title}.", found.Time, null, null, tools);

            if (Package(vendor, version) is { } package)
                return new DriverOrigin(OriginKind.Program, Named(package), $"{package.Name} is installed, and its version matches the driver's.", null, null, package, tools);
        }

        var since = logSince is { } d ? $" (it starts {DriverText.Date(d)})" : string.Empty;
        return new DriverOrigin(OriginKind.Unknown, "Not recorded",
            $"It was added before the oldest setup log Windows kept{since}, and neither Windows Update's history nor an installed program matches it.",
            null, null, null, tools);
    }

    /// <summary>
    /// A vendor's package for this driver, matched exactly: a per-driver entry with the same version
    /// ("AMD PSP Driver" 5.39.0.0), or NVIDIA's "NVIDIA Graphics Driver 610.88" for 32.0.16.1088.
    /// </summary>
    private InstalledPackage? Package(string? vendor, string? version)
    {
        if (vendor is null || version is null)
            return null;

        var key = VendorKey(vendor);
        var same = packages.Where(p => p.Publisher is { } publisher && VendorKey(publisher) == key).ToList();
        var exact = same.FirstOrDefault(p => string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        if (key == "nvidia" && NvidiaRelease(version) is { } release)
            return same.FirstOrDefault(p => p.Name.StartsWith("NVIDIA Graphics Driver", StringComparison.OrdinalIgnoreCase) && p.Version == release);

        return null;
    }

    /// <summary>NVIDIA's Windows driver version ends in its release: 32.0.16.1088 is 610.88 (the last five digits of 16 and 1088).</summary>
    private static string? NvidiaRelease(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[2], out var minor) || !int.TryParse(parts[3], out var build))
            return null;

        var digits = (minor % 10 * 10000 + build).ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
        return $"{digits[..3]}.{digits[3..]}";
    }

    private IReadOnlyList<VendorTool> ToolsFor(string? vendor, string? classTitle, string? name, bool thirdParty)
    {
        if (!thirdParty || tools.Count == 0)
            return [];

        var key = vendor is null ? null : VendorKey(vendor);
        var graphics = classTitle?.Contains("Display", StringComparison.OrdinalIgnoreCase) == true
            || name?.Contains("Graphics", StringComparison.OrdinalIgnoreCase) == true
            || name?.Contains("Radeon", StringComparison.OrdinalIgnoreCase) == true;

        return [.. tools.Where(t => t.Scope switch
        {
            ToolScope.Nvidia => key == "nvidia",
            ToolScope.AmdGraphics => key == "amd" && graphics,
            ToolScope.Intel => key == "intel",
            ToolScope.IntelGraphics => key == "intel" && graphics,
            _ => true,
        }).OrderBy(t => t.Scope == ToolScope.ComputerMaker)];
    }

    /// <summary>One word per vendor, however a provider, publisher or company writes it.</summary>
    private static string VendorKey(string name)
    {
        var text = name.Trim();
        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return "nvidia";
        if (text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || text.StartsWith("AMD", StringComparison.OrdinalIgnoreCase) || text.StartsWith("ATI ", StringComparison.OrdinalIgnoreCase))
            return "amd";
        if (text.StartsWith("Intel", StringComparison.OrdinalIgnoreCase))
            return "intel";
        var space = text.IndexOfAny([' ', ',']);
        return (space > 0 ? text[..space] : text).ToLowerInvariant();
    }

    /// <summary>"Intel Corporation" as "Intel", "Advanced Micro Devices, Inc." as "AMD".</summary>
    private static string Short(string vendor) => VendorKey(vendor) switch
    {
        "nvidia" => "NVIDIA",
        "amd" => "AMD",
        "intel" => "Intel",
        _ => vendor.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? vendor,
    };

    private static bool SameVendor(string title, string? vendor) => vendor is null || VendorKey(title) == VendorKey(vendor);

    private static string Named(InstalledPackage package) =>
        package.Version is { } version && !package.Name.Contains(version, StringComparison.OrdinalIgnoreCase) ? $"{package.Name} {version}" : package.Name;

    /// <summary>"C:\NVIDIA\DisplayDriver\610.88\…\nv.inf" as "C:\NVIDIA\DisplayDriver".</summary>
    private static string Folder(string source)
    {
        var parts = (Path.GetDirectoryName(source) ?? source).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? string.Join('\\', parts) : string.Join('\\', parts.Take(3)) + @"\…";
    }
}
