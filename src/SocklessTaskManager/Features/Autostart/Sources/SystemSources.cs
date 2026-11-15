using System.Text;
using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// The less visible places Windows starts or loads code from: sign-in and boot programs, programs
/// started in place of others, DLLs loaded into every program or into the security subsystem,
/// network, print and time providers, and command shell start-up scripts. Most hold only Windows'
/// own entries; anything else in them deserves a look.
/// </summary>
internal static class SystemSources
{
    private const RegistryHive Machine = RegistryHive.LocalMachine;
    private const RegistryHive User = RegistryHive.CurrentUser;

    private const string Winlogon = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string Ifeo = @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string Lsa = @"SYSTEM\CurrentControlSet\Control\Lsa";

    public static IEnumerable<AutostartItem> Scan() =>
    [
        .. WinlogonEntries(),
        .. BootEntries(),
        .. ProgramLaunchEntries(),
        .. LoadedDllEntries(),
        .. SecurityProviderEntries(),
        .. NetworkEntries(),
        .. PrintEntries(),
        .. ShellEntries(),
        .. OtherEntries(),
    ];

    // ---------- Winlogon ----------

    private static IEnumerable<AutostartItem> WinlogonEntries()
    {
        const string signIn = "Started by Windows' sign-in process for every user who signs in.";
        foreach (var item in ListItems(AutostartCategory.Winlogon, Machine, Winlogon, "Userinit", ',', signIn + " Windows' own is userinit.exe."))
            yield return item;
        foreach (var item in ListItems(AutostartCategory.Winlogon, Machine, Winlogon, "Shell", ',', "The shell every user gets at sign-in. Windows' own is explorer.exe."))
            yield return item;
        foreach (var item in ValueEntry(AutostartCategory.Winlogon, User, Winlogon, "Shell", "Your shell at sign-in, in place of the computer's (explorer.exe).", movable: false))
            yield return item;
        foreach (var item in ValueEntry(AutostartCategory.Winlogon, User, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Shell", "Your shell at sign-in, set as a policy.", movable: false))
            yield return item;
        foreach (var name in (string[])["Taskman", "AppSetup", "VmApplet", "GinaDLL"])
        {
            foreach (var item in ValueEntry(AutostartCategory.Winlogon, Machine, Winlogon, name, signIn, movable: false))
                yield return item;
        }

        foreach (var item in KeyEntries(AutostartCategory.Winlogon, Machine, $@"{Winlogon}\Notify", "DllName",
                     "A Winlogon notification package: loaded by the sign-in process (older versions of Windows only)."))
            yield return item;
        foreach (var item in KeyEntries(AutostartCategory.Winlogon, Machine, $@"{Winlogon}\GPExtensions", "DllName",
                     "A Group Policy extension: loaded by the Group Policy service at startup and sign-in to apply its settings."))
            yield return item;

        foreach (var (path, kind) in ((string, string)[])[
                     (@"Software\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers", "Credential provider"),
                     (@"Software\Microsoft\Windows\CurrentVersion\Authentication\Credential Provider Filters", "Credential provider filter"),
                     (@"Software\Microsoft\Windows\CurrentVersion\Authentication\PLAP Providers", "Pre-sign-in network provider")])
        {
            foreach (var (clsid, enabled) in ExplorerSources.Keys(Machine, path))
            {
                if (!clsid.StartsWith('{'))
                    continue;

                var server = AutostartRegistry.ClassServer(clsid);
                var location = AutostartRegistry.Display(Machine, path);
                var keyPath = enabled ? $@"{path}\{clsid}" : $@"{path}\{AutostartRegistry.DisabledKey}\{clsid}";
                yield return new AutostartItem
                {
                    Id = $"{location}|{clsid}",
                    Category = AutostartCategory.Winlogon,
                    Name = AutostartRegistry.ClassName(clsid) ?? clsid,
                    Location = location,
                    Command = server ?? string.Empty,
                    Image = server is null ? null : CommandLine.Normalize(server),
                    IsEnabled = enabled,
                    About = $"A {kind.ToLowerInvariant()}: the sign-in and lock screens load it to offer a way to sign in.",
                    Facts = [new AutostartFact("Kind", kind), new AutostartFact("Class", clsid)],
                    Toggle = new MovedKeyTarget(Machine, path, clsid) { NeedsAdmin = true },
                    Removal = new RegistryKeyTarget(Machine, keyPath) { NeedsAdmin = true },
                    RegistryKey = location,
                };
            }
        }
    }

    // ---------- Boot ----------

    private static IEnumerable<AutostartItem> BootEntries()
    {
        foreach (var name in (string[])["BootExecute", "SetupExecute", "Execute", "PlatformExecute", "S0InitialCommand"])
        {
            foreach (var item in ListItems(AutostartCategory.Boot, Machine, SessionManager, name, '\n',
                         "Run by the Session Manager while Windows starts, before services and before anyone signs in. Windows' own is autocheck autochk *, which checks disks.",
                         native: true))
                yield return item;
        }

        foreach (var item in ValueEntry(AutostartCategory.Boot, Machine, @"SYSTEM\CurrentControlSet\Control\SafeBoot", "AlternateShell",
                     "The shell in Safe Mode with Command Prompt. Windows' own is cmd.exe.", movable: false, removable: false))
            yield return item;
    }

    // ---------- Programs started in place of others ----------

    private static IEnumerable<AutostartItem> ProgramLaunchEntries()
    {
        foreach (var root in (string[])[Ifeo, @"Software\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"])
        {
            using var options = AutostartRegistry.Open(Machine, root);
            if (options is null)
                continue;

            foreach (var program in AutostartRegistry.SubKeyNames(options))
            {
                foreach (var item in ValueEntry(AutostartCategory.ProgramLaunch, Machine, $@"{root}\{program}", "Debugger",
                             $"Starts in place of {program}: Windows runs this whenever {program} is started, and passes it {program}'s command line.",
                             display: $"{program} → Debugger"))
                    yield return item;
                foreach (var item in ValueEntry(AutostartCategory.ProgramLaunch, Machine, $@"{root}\{program}", "VerifierDlls",
                             $"DLLs Application Verifier loads into {program} when it starts.", display: $"{program} → VerifierDlls"))
                    yield return item;
            }
        }

        const string silentExit = @"Software\Microsoft\Windows NT\CurrentVersion\SilentProcessExit";
        using (var monitored = AutostartRegistry.Open(Machine, silentExit))
        {
            if (monitored is not null)
            {
                foreach (var program in AutostartRegistry.SubKeyNames(monitored))
                {
                    foreach (var item in ValueEntry(AutostartCategory.ProgramLaunch, Machine, $@"{silentExit}\{program}", "MonitorProcess",
                                 $"Started whenever {program} exits.", display: $"{program} → on exit"))
                        yield return item;
                }
            }
        }

        foreach (var root in (string[])[@"Software\Microsoft\Windows NT\CurrentVersion\AeDebug", @"Software\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AeDebug"])
        {
            foreach (var item in ValueEntry(AutostartCategory.ProgramLaunch, Machine, root, "Debugger",
                         "Started when a program crashes, to debug it or report the crash.", movable: false))
                yield return item;
        }

        // How Windows opens programs and scripts: anything but "%1" %* runs something else first.
        foreach (var type in (string[])["exefile", "comfile", "batfile", "cmdfile", "piffile"])
        {
            foreach (var hive in (RegistryHive[])[User, Machine])
            {
                var path = $@"Software\Classes\{type}\shell\open\command";
                using var key = AutostartRegistry.Open(hive, path);
                if (key is null || AutostartRegistry.Text(key, null) is not { } command)
                    continue;

                var plain = command.Replace(" ", string.Empty, StringComparison.Ordinal);
                if (hive == Machine && plain is "\"%1\"%*" or "\"%1\"")
                    continue;

                var location = AutostartRegistry.Display(hive, path);
                yield return new AutostartItem
                {
                    Id = $"{location}|",
                    Category = AutostartCategory.ProgramLaunch,
                    Name = $"Opening {type[..^4]} files",
                    Location = location,
                    Command = command,
                    Image = SignInSources.Image(command),
                    IsPerUser = hive == User,
                    About = $"How Windows opens {type[..^4]} files. Windows' own is \"%1\" %*, which runs the file itself; anything else runs first, every time.",
                    Removal = hive == User ? new RegistryKeyTarget(User, path) : null,
                    Locked = hive == User ? null : "Set it back to \"%1\" %* in Registry Editor.",
                    RegistryKey = location,
                };
            }
        }
    }

    // ---------- DLLs loaded into programs ----------

    private static IEnumerable<AutostartItem> LoadedDllEntries()
    {
        foreach (var root in (string[])[@"Software\Microsoft\Windows NT\CurrentVersion\Windows", @"Software\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows"])
        {
            using var key = AutostartRegistry.Open(Machine, root);
            if (key is null || AutostartRegistry.Text(key, "AppInit_DLLs") is not { Length: > 0 } list)
                continue;

            var loads = AutostartRegistry.Number(key, "LoadAppInit_DLLs") == 1;
            var separator = list.Contains(',') ? ',' : ' ';
            foreach (var dll in list.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return Item(AutostartCategory.LoadedDlls, Machine, root, "AppInit_DLLs", dll, dll, CommandLine.Normalize(dll),
                    loads
                        ? "Loaded into every program that uses user32.dll, which is nearly all of them."
                        : "Would be loaded into nearly every program, but LoadAppInit_DLLs is off, so Windows ignores it.",
                    toggle: null, removal: new ListItemTarget(Machine, root, "AppInit_DLLs", dll, separator) { NeedsAdmin = true }, enabled: loads);
            }
        }

        foreach (var item in Values(AutostartCategory.LoadedDlls, Machine, $@"{SessionManager}\AppCertDlls",
                     "Loaded into every program that starts other programs (CreateProcess)."))
            yield return item;
    }

    // ---------- Security providers ----------

    private static IEnumerable<AutostartItem> SecurityProviderEntries()
    {
        foreach (var (path, value, about) in ((string, string, string)[])[
                     (Lsa, "Authentication Packages", "An authentication package: loaded by the Local Security Authority (lsass.exe) at startup, and sees sign-ins."),
                     (Lsa, "Notification Packages", "A password filter: loaded by the Local Security Authority, and sees every password change."),
                     (Lsa, "Security Packages", "A security package: loaded by the Local Security Authority at startup."),
                     ($@"{Lsa}\OSConfig", "Security Packages", "A security package: loaded by the Local Security Authority at startup.")])
        {
            foreach (var item in ListItems(AutostartCategory.SecurityProviders, Machine, path, value, '\n', about, dll: true))
                yield return item;
        }

        foreach (var item in ListItems(AutostartCategory.SecurityProviders, Machine, @"SYSTEM\CurrentControlSet\Control\SecurityProviders", "SecurityProviders", ',',
                     "A security support provider: loaded into programs that use Windows authentication.", dll: true))
            yield return item;
    }

    // ---------- Network ----------

    private static IEnumerable<AutostartItem> NetworkEntries()
    {
        const string order = @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order";
        using (var key = AutostartRegistry.Open(Machine, order))
        {
            if (key is not null && AutostartRegistry.Text(key, "ProviderOrder") is { Length: > 0 } providers)
            {
                foreach (var name in providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using var provider = AutostartRegistry.Open(Machine, $@"{ServiceSources.ServicesPath}\{name}\NetworkProvider");
                    var dll = provider is null ? null : AutostartRegistry.Text(provider, "ProviderPath");
                    yield return Item(AutostartCategory.Network, Machine, order, "ProviderOrder", name, dll ?? string.Empty,
                        dll is null ? null : CommandLine.Normalize(dll),
                        "A network provider: loaded at sign-in to connect network drives and resources, and sees the password you sign in with.",
                        toggle: null, removal: new ListItemTarget(Machine, order, "ProviderOrder", name, ',') { NeedsAdmin = true });
                }
            }
        }

        const string winsock = @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters";
        foreach (var catalog in (string[])[$@"{winsock}\NameSpace_Catalog5\Catalog_Entries64", $@"{winsock}\NameSpace_Catalog5\Catalog_Entries"])
        {
            using var entries = AutostartRegistry.Open(Machine, catalog);
            if (entries is null)
                continue;

            foreach (var id in AutostartRegistry.SubKeyNames(entries))
            {
                using var entry = entries.OpenSubKey(id);
                if (entry is null || AutostartRegistry.Text(entry, "LibraryPath") is not { Length: > 0 } dll)
                    continue;

                var name = AutostartRegistry.Text(entry, "DisplayString") is { Length: > 0 } display ? AutostartRegistry.Indirect(display) : id;
                yield return Winsock(catalog, id, name, dll, "A Winsock name space provider: loaded into every program that looks up network names.");
            }
        }

        foreach (var catalog in (string[])[$@"{winsock}\Protocol_Catalog9\Catalog_Entries64", $@"{winsock}\Protocol_Catalog9\Catalog_Entries"])
        {
            using var entries = AutostartRegistry.Open(Machine, catalog);
            if (entries is null)
                continue;

            foreach (var id in AutostartRegistry.SubKeyNames(entries))
            {
                using var entry = entries.OpenSubKey(id);
                // WSAPROTOCOL_INFO is packed after the provider's path (MAX_PATH ANSI characters).
                if (entry is null || AutostartRegistry.Binary(entry, "PackedCatalogItem") is not { Length: > 260 } packed)
                    continue;

                var end = Array.IndexOf(packed, (byte)0, 0, 260);
                var dll = Encoding.Latin1.GetString(packed, 0, end < 0 ? 260 : end);
                if (dll.Length == 0)
                    continue;

                // WSAPROTOCOL_INFOW: szProtocol (256 characters) starts 116 bytes in.
                var name = packed.Length >= 260 + 628 ? Utf16(packed.AsSpan(260 + 116, 256 * 2)) : id;
                yield return Winsock(catalog, id, name.Length > 0 ? name : id, dll,
                    "A Winsock protocol provider: loaded into every program that uses the network.");
            }
        }

        foreach (var item in Values(AutostartCategory.Network, Machine, @"Software\Microsoft\NetSh",
                     "A netsh helper: loaded whenever netsh.exe runs, which Windows and setup programs do often."))
            yield return item;
    }

    private static AutostartItem Winsock(string catalog, string id, string name, string dll, string about)
    {
        var location = AutostartRegistry.Display(Machine, catalog);
        return new AutostartItem
        {
            Id = $"{location}|{id}",
            Category = AutostartCategory.Network,
            Name = name,
            Location = location,
            Command = dll,
            Image = CommandLine.Normalize(dll),
            About = about,
            Facts = [new AutostartFact("Catalog entry", id)],
            Locked = "Remove Winsock providers with the program that installed them, or reset Winsock (netsh winsock reset) to Windows' own.",
            RegistryKey = $@"{location}\{id}",
        };
    }

    // ---------- Printing ----------

    private static IEnumerable<AutostartItem> PrintEntries()
    {
        const string monitors = @"SYSTEM\CurrentControlSet\Control\Print\Monitors";
        foreach (var item in KeyEntries(AutostartCategory.Printing, Machine, monitors, "Driver",
                     "A print monitor: loaded by the Print Spooler service at startup.", folder: Environment.SystemDirectory))
            yield return item;

        var processors = $@"SYSTEM\CurrentControlSet\Control\Print\Environments\Windows x64\Print Processors";
        foreach (var item in KeyEntries(AutostartCategory.Printing, Machine, processors, "Driver",
                     "A print processor: loaded by the Print Spooler service.", folder: Path.Join(Environment.SystemDirectory, @"spool\prtprocs\x64")))
            yield return item;
    }

    // ---------- Command shells ----------

    private static IEnumerable<AutostartItem> ShellEntries()
    {
        foreach (var hive in (RegistryHive[])[User, Machine])
        {
            foreach (var item in ValueEntry(AutostartCategory.CommandShells, hive, @"Software\Microsoft\Command Processor", "AutoRun",
                         hive == User ? "Runs each time you open Command Prompt (cmd.exe), and whenever a program runs a command through it." : "Runs each time anyone opens Command Prompt (cmd.exe), and whenever a program runs a command through it."))
                yield return item;
        }

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsPowerShell = Path.Join(Environment.SystemDirectory, @"WindowsPowerShell\v1.0");
        foreach (var (path, everyone) in ((string, bool)[])[
                     (Path.Join(windowsPowerShell, "profile.ps1"), true),
                     (Path.Join(windowsPowerShell, "Microsoft.PowerShell_profile.ps1"), true),
                     (Path.Join(programFiles, @"PowerShell\7\profile.ps1"), true),
                     (Path.Join(programFiles, @"PowerShell\7\Microsoft.PowerShell_profile.ps1"), true),
                     (Path.Join(documents, @"WindowsPowerShell\profile.ps1"), false),
                     (Path.Join(documents, @"WindowsPowerShell\Microsoft.PowerShell_profile.ps1"), false),
                     (Path.Join(documents, @"PowerShell\profile.ps1"), false),
                     (Path.Join(documents, @"PowerShell\Microsoft.PowerShell_profile.ps1"), false)])
        {
            if (!File.Exists(path))
                continue;

            yield return new AutostartItem
            {
                Id = $"file|{path}",
                Category = AutostartCategory.CommandShells,
                Name = $"PowerShell profile ({Path.GetFileName(path)})",
                Location = Path.GetDirectoryName(path) ?? path,
                Command = path,
                Image = path,
                IsPerUser = !everyone,
                About = everyone
                    ? "A PowerShell profile: runs each time anyone starts PowerShell, unless it's started with -NoProfile."
                    : "Your PowerShell profile: runs each time you start PowerShell, unless it's started with -NoProfile.",
                Removal = new FileTarget(path) { NeedsAdmin = everyone },
            };
        }
    }

    // ---------- Other ----------

    private static IEnumerable<AutostartItem> OtherEntries()
    {
        const string windows = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";
        foreach (var name in (string[])["Load", "Run"])
        {
            foreach (var item in ValueEntry(AutostartCategory.SignIn, User, windows, name,
                         "Starts when you sign in: the list from Windows 3.1's WIN.INI, still honoured.", movable: false))
                yield return item;
        }

        foreach (var item in ValueEntry(AutostartCategory.SignIn, User, "Environment", "UserInitMprLogonScript",
                     "A sign-in script: runs when you sign in.", movable: false))
            yield return item;

        foreach (var root in (string[])[@"Software\Microsoft\Active Setup\Installed Components", @"Software\WOW6432Node\Microsoft\Active Setup\Installed Components"])
        {
            using var components = AutostartRegistry.Open(Machine, root);
            if (components is null)
                continue;

            foreach (var id in AutostartRegistry.SubKeyNames(components))
            {
                using var component = components.OpenSubKey(id);
                if (component is null || AutostartRegistry.Text(component, "StubPath") is not { Length: > 0 } stub)
                    continue;
                if (AutostartRegistry.Number(component, "IsInstalled") == 0)
                    continue;

                var keyPath = $@"{root}\{id}";
                var location = AutostartRegistry.Display(Machine, root);
                yield return new AutostartItem
                {
                    Id = $"{location}|{id}",
                    Category = AutostartCategory.SignIn,
                    Name = AutostartRegistry.Text(component, null) is { Length: > 0 } name ? AutostartRegistry.Indirect(name) : id,
                    Location = location,
                    Command = stub,
                    Image = SignInSources.Image(stub),
                    About = "Active Setup: runs once for each user, at their first sign-in after it was installed (or updated), before the desktop appears.",
                    Facts = [new AutostartFact("Component", id)],
                    Removal = new RegistryValueTarget(Machine, keyPath, "StubPath") { NeedsAdmin = true },
                    RegistryKey = AutostartRegistry.Display(Machine, keyPath),
                };
            }
        }

        foreach (var hive in (RegistryHive[])[Machine, User])
        {
            const string scripts = @"Software\Microsoft\Windows\CurrentVersion\Group Policy\Scripts";
            foreach (var phase in (string[])["Startup", "Shutdown", "Logon", "Logoff"])
            {
                using var policies = AutostartRegistry.Open(hive, $@"{scripts}\{phase}");
                if (policies is null)
                    continue;

                foreach (var policy in AutostartRegistry.SubKeyNames(policies))
                {
                    using var policyKey = policies.OpenSubKey(policy);
                    if (policyKey is null)
                        continue;

                    foreach (var index in AutostartRegistry.SubKeyNames(policyKey))
                    {
                        using var script = policyKey.OpenSubKey(index);
                        if (script is null || AutostartRegistry.Text(script, "Script") is not { Length: > 0 } file)
                            continue;

                        var parameters = AutostartRegistry.Text(script, "Parameters") ?? string.Empty;
                        var location = AutostartRegistry.Display(hive, $@"{scripts}\{phase}\{policy}\{index}");
                        yield return new AutostartItem
                        {
                            Id = $"{location}|script",
                            Category = AutostartCategory.SignIn,
                            Name = Path.GetFileName(file),
                            Location = location,
                            Command = parameters.Length > 0 ? $"{file} {parameters}" : file,
                            Image = CommandLine.Normalize(file),
                            IsPerUser = hive == User,
                            About = phase switch
                            {
                                "Startup" => "A Group Policy startup script: runs as the computer starts, before anyone signs in.",
                                "Shutdown" => "A Group Policy shutdown script: runs as the computer shuts down.",
                                "Logon" => "A Group Policy sign-in script: runs when the user signs in.",
                                _ => "A Group Policy sign-out script: runs when the user signs out.",
                            },
                            Locked = "Set by Group Policy: change it in the policy.",
                            RegistryKey = location,
                        };
                    }
                }
            }
        }

        foreach (var item in ValueEntry(AutostartCategory.Other, User, @"Control Panel\Desktop", "SCRNSAVE.EXE",
                     "Your screen saver: starts after the computer has been idle for the time set.", movable: false))
            yield return item;

        const string timeProviders = @"SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders";
        foreach (var item in KeyEntries(AutostartCategory.Other, Machine, timeProviders, "DllName",
                     "A time provider: loaded by the Windows Time service to read or serve the time."))
            yield return item;
    }

    // ---------- Builders ----------

    /// <summary>A single value holding one command or DLL.</summary>
    private static IEnumerable<AutostartItem> ValueEntry(AutostartCategory category, RegistryHive hive, string path, string name, string about,
        bool movable = true, bool removable = true, string? display = null)
    {
        foreach (var (key, enabled) in (ReadOnlySpan<(string, bool)>)[(path, true), ($@"{path}\{AutostartRegistry.DisabledKey}", false)])
        {
            if (!enabled && !movable)
                break;

            using var open = AutostartRegistry.Open(hive, key);
            if (open is null || AutostartRegistry.Text(open, name) is not { Length: > 0 } command)
                continue;

            var admin = hive == Machine;
            return
            [
                Item(category, hive, path, name, display ?? name, command, SignInSources.Image(command), about,
                    movable ? new MovedValueTarget(hive, path, name) { NeedsAdmin = admin } : null,
                    removable ? new RegistryValueTarget(hive, key, name) { NeedsAdmin = admin } : null,
                    enabled, registryKey: AutostartRegistry.Display(hive, key)),
            ];
        }

        return [];
    }

    /// <summary>Every value in a key, each one command or DLL.</summary>
    private static IEnumerable<AutostartItem> Values(AutostartCategory category, RegistryHive hive, string path, string about)
    {
        var admin = hive == Machine;
        foreach (var (name, value, enabled) in ExplorerSources.Values(hive, path))
        {
            if (value.Length == 0)
                continue;

            var key = enabled ? path : $@"{path}\{AutostartRegistry.DisabledKey}";
            yield return Item(category, hive, path, name.Length > 0 ? name : "(Default)", name, value, SignInSources.Image(value), about,
                new MovedValueTarget(hive, path, name) { NeedsAdmin = admin },
                new RegistryValueTarget(hive, key, name) { NeedsAdmin = admin }, enabled, registryKey: AutostartRegistry.Display(hive, key));
        }
    }

    /// <summary>Each item of a list kept in one value (REG_MULTI_SZ, or text split by <paramref name="separator"/>).</summary>
    private static IEnumerable<AutostartItem> ListItems(AutostartCategory category, RegistryHive hive, string path, string name, char separator, string about,
        bool dll = false, bool native = false)
    {
        using var key = AutostartRegistry.Open(hive, path);
        if (key is null)
            yield break;

        var items = separator == '\n'
            ? AutostartRegistry.Lines(key, name)
            : (AutostartRegistry.Text(key, name) ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in items)
        {
            var text = entry.Trim().Trim('"');
            if (text.Length == 0)
                continue;

            string? image;
            if (dll)
                image = CommandLine.Normalize(Path.HasExtension(text) ? text : text + ".dll");
            else if (native)
            {
                // "autocheck autochk *": autocheck is a keyword, the program follows it.
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var program = words.Length > 1 && words[0].Equals("autocheck", StringComparison.OrdinalIgnoreCase) ? words[1] : words[0];
                image = CommandLine.Normalize(Path.HasExtension(program) ? program : program + ".exe");
            }
            else
                image = SignInSources.Image(text);

            yield return Item(category, hive, path, name, $"{name}: {text}", entry.Trim(), image, about, toggle: null,
                removal: new ListItemTarget(hive, path, name, entry.Trim(), separator) { NeedsAdmin = hive == Machine });
        }
    }

    /// <summary>Each subkey of a list, whose <paramref name="valueName"/> names its DLL.</summary>
    private static IEnumerable<AutostartItem> KeyEntries(AutostartCategory category, RegistryHive hive, string path, string valueName, string about, string? folder = null)
    {
        foreach (var (name, enabled) in ExplorerSources.Keys(hive, path))
        {
            var keyPath = enabled ? $@"{path}\{name}" : $@"{path}\{AutostartRegistry.DisabledKey}\{name}";
            using var key = AutostartRegistry.Open(hive, keyPath);
            if (key is null || AutostartRegistry.Text(key, valueName) is not { Length: > 0 } dll)
                continue;

            var image = folder is not null && !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(dll))
                ? Path.Join(folder, dll)
                : CommandLine.Normalize(dll);
            var display = AutostartRegistry.Text(key, null) is { Length: > 0 } named && !named.StartsWith('{') ? AutostartRegistry.Indirect(named) : name;
            yield return Item(category, hive, path, name, display, dll, image, about,
                new MovedKeyTarget(hive, path, name) { NeedsAdmin = hive == Machine },
                new RegistryKeyTarget(hive, keyPath) { NeedsAdmin = hive == Machine }, enabled,
                registryKey: AutostartRegistry.Display(hive, keyPath));
        }
    }

    private static AutostartItem Item(AutostartCategory category, RegistryHive hive, string path, string id, string name, string command, string? image,
        string about, AutostartTarget? toggle, AutostartTarget? removal, bool enabled = true, string? registryKey = null)
    {
        var location = AutostartRegistry.Display(hive, path);
        return new AutostartItem
        {
            Id = $"{location}|{id}|{command}",
            Category = category,
            Name = name,
            Location = location,
            Command = command,
            Image = image,
            IsEnabled = enabled,
            IsPerUser = hive == User,
            About = about,
            Toggle = toggle,
            Removal = removal,
            RegistryKey = registryKey ?? location,
        };
    }

    private static string Utf16(ReadOnlySpan<byte> bytes)
    {
        var chars = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(bytes);
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }
}
