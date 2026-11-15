using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Services and drivers that start on their own (boot, system and automatic), read from the
/// Services key, plus those turned off here, which remember the startup type they had. Starting,
/// stopping and uninstalling them is the Services page's job.
/// </summary>
internal static class ServiceSources
{
    public const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>The startup type each service had before it was turned off here (start | delayed &lt;&lt; 8).</summary>
    public const string RememberedPath = @"SOFTWARE\Sockless Task Manager\Autostart\Services";

    public const int Boot = 0;
    public const int System = 1;
    public const int Automatic = 2;
    public const int Disabled = 4;

    public static IEnumerable<AutostartItem> Scan()
    {
        using var services = AutostartRegistry.Open(RegistryHive.LocalMachine, ServicesPath);
        if (services is null)
            yield break;

        var remembered = Remembered();
        foreach (var name in AutostartRegistry.SubKeyNames(services))
        {
            using var key = services.OpenSubKey(name);
            if (key is null || AutostartRegistry.Number(key, "Type") is not { } type || AutostartRegistry.Number(key, "Start") is not { } start)
                continue;

            var isDriver = (type & 0x3) != 0;
            var isService = (type & 0x30) != 0;
            if (!isDriver && !isService)
                continue;

            int originalStart;
            bool delayed;
            bool enabled;
            if (start <= Automatic)
            {
                enabled = true;
                originalStart = start;
                delayed = AutostartRegistry.Number(key, "DelayedAutostart") == 1;
            }
            else if (start == Disabled && remembered.TryGetValue(name, out var was))
            {
                enabled = false;
                originalStart = was & 0xFF;
                delayed = (was >> 8) == 1;
            }
            else
            {
                continue;
            }

            if (isService && originalStart < Automatic)
                continue;

            var imagePath = AutostartRegistry.Text(key, "ImagePath") ?? (isDriver ? $@"System32\drivers\{name}.sys" : string.Empty);
            var image = imagePath.Length > 0 ? CommandLine.ProgramPath(imagePath) : null;
            if (isService && image is not null && Path.GetFileName(image).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                using var parameters = key.OpenSubKey("Parameters");
                var dll = (parameters is null ? null : AutostartRegistry.Text(parameters, "ServiceDll")) ?? AutostartRegistry.Text(key, "ServiceDll");
                if (dll is { Length: > 0 })
                    image = CommandLine.Normalize(dll);
            }

            var display = AutostartRegistry.Text(key, "DisplayName") is { Length: > 0 } text ? AutostartRegistry.Indirect(text) : name;
            var facts = new List<AutostartFact>
            {
                new("Service name", name),
                new("Startup type", StartText(originalStart, delayed, isDriver)),
            };
            if (isService)
                facts.Add(new AutostartFact("Log on as", AutostartRegistry.Text(key, "ObjectName") ?? "LocalSystem"));
            if (image is not null && !string.Equals(image, CommandLine.ProgramPath(imagePath), StringComparison.OrdinalIgnoreCase))
                facts.Add(new AutostartFact("Service DLL", image));
            if (AutostartRegistry.Text(key, "Description") is { Length: > 0 } description)
                facts.Add(new AutostartFact("Description", AutostartRegistry.Indirect(description)));

            yield return new AutostartItem
            {
                Id = $"service|{name}",
                Category = isDriver ? AutostartCategory.Drivers : AutostartCategory.Services,
                Name = display,
                Location = AutostartRegistry.Display(RegistryHive.LocalMachine, $@"{ServicesPath}\{name}"),
                Command = imagePath,
                Image = image,
                IsEnabled = enabled,
                About = About(isDriver, originalStart, delayed),
                Facts = facts,
                Toggle = new ServiceTarget(name, originalStart, delayed) { NeedsAdmin = true },
                Locked = "Uninstall services and drivers on the Services page.",
                RegistryKey = AutostartRegistry.Display(RegistryHive.LocalMachine, $@"{ServicesPath}\{name}"),
            };
        }
    }

    private static Dictionary<string, int> Remembered()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var key = AutostartRegistry.Open(RegistryHive.LocalMachine, RememberedPath);
        if (key is null)
            return map;

        foreach (var name in AutostartRegistry.ValueNames(key))
        {
            if (AutostartRegistry.Number(key, name) is { } value)
                map[name] = value;
        }

        return map;
    }

    private static string StartText(int start, bool delayed, bool isDriver) => start switch
    {
        Boot => "Boot: loaded by the boot loader",
        System => "System: loaded as the kernel starts",
        _ when delayed => "Automatic (delayed)",
        _ => isDriver ? "Automatic: loaded at startup" : "Automatic",
    };

    private static string About(bool isDriver, int start, bool delayed) => isDriver
        ? start switch
        {
            Boot => "A driver Windows loads while it boots, before anything else. Turning one off can keep Windows from starting.",
            System => "A driver Windows loads as the kernel starts. Turning one off can keep Windows from starting.",
            _ => "A driver Windows loads at startup.",
        }
        : delayed
            ? "A service Windows starts shortly after startup, before anyone signs in, and runs whether or not anyone is."
            : "A service Windows starts at startup, before anyone signs in, and runs whether or not anyone is.";
}
