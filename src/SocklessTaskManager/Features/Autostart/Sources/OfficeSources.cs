using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Office add-ins (COM and VSTO), which each Office program loads as it starts or when first needed,
/// as LoadBehavior says. Turning one off sets LoadBehavior to 0 and remembers what it was.
/// </summary>
internal static class OfficeSources
{
    public const string RememberedValue = "Sockless LoadBehavior";

    private static readonly string[] Programs = ["Word", "Excel", "PowerPoint", "Outlook", "Access", "MS Project", "Visio", "OneNote", "Publisher"];

    private static readonly (RegistryHive Hive, string Root)[] Roots =
    [
        (RegistryHive.CurrentUser, @"Software\Microsoft\Office"),
        (RegistryHive.LocalMachine, @"Software\Microsoft\Office"),
        (RegistryHive.LocalMachine, @"Software\WOW6432Node\Microsoft\Office"),
    ];

    public static IEnumerable<AutostartItem> Scan()
    {
        foreach (var (hive, root) in Roots)
        {
            foreach (var program in Programs)
            {
                var path = $@"{root}\{program}\Addins";
                using var addins = AutostartRegistry.Open(hive, path);
                if (addins is null)
                    continue;

                foreach (var progId in AutostartRegistry.SubKeyNames(addins))
                {
                    using var key = addins.OpenSubKey(progId);
                    if (key is null || AutostartRegistry.Number(key, "LoadBehavior") is not { } load)
                        continue;

                    var manifest = AutostartRegistry.Text(key, "Manifest");
                    var server = ProgIdServer(progId);
                    var command = server ?? manifest ?? string.Empty;
                    var image = server is not null ? CommandLine.Normalize(server)
                        : manifest is { Length: > 0 } ? CommandLine.Normalize(manifest.Split('|')[0])
                        : null;
                    var keyPath = $@"{path}\{progId}";
                    var location = AutostartRegistry.Display(hive, path);
                    var admin = hive == RegistryHive.LocalMachine;
                    yield return new AutostartItem
                    {
                        Id = $"{location}|{progId}",
                        Category = AutostartCategory.Office,
                        Name = (AutostartRegistry.Text(key, "FriendlyName") is { Length: > 0 } friendly ? AutostartRegistry.Indirect(friendly) : progId)
                            + (root.Contains("WOW6432Node", StringComparison.Ordinal) ? $" ({program}, 32-bit)" : $" ({program})"),
                        Location = location,
                        Command = command,
                        Image = image,
                        IsEnabled = (load & 3) != 0 || (load & 8) != 0 || load == 16,
                        IsPerUser = hive == RegistryHive.CurrentUser,
                        About = $"An add-in {program} loads as it starts, or when first needed.",
                        Facts = [new AutostartFact("Program", program), new AutostartFact("Loads", LoadText(load)), new AutostartFact("Add-in", progId)],
                        Toggle = new OfficeAddinSwitch(hive, keyPath) { NeedsAdmin = admin },
                        Removal = new RegistryKeyTarget(hive, keyPath) { NeedsAdmin = admin },
                        RegistryKey = AutostartRegistry.Display(hive, keyPath),
                    };
                }
            }
        }
    }

    private static string LoadText(int load) => load switch
    {
        0 or 1 => "Not automatically",
        2 or 3 => "When the program starts",
        8 or 9 => "When first needed",
        16 => "Once, then when first needed",
        _ => $"LoadBehavior {load}",
    };

    private static string? ProgIdServer(string progId)
    {
        foreach (var hive in (ReadOnlySpan<RegistryHive>)[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            using var key = AutostartRegistry.Open(hive, $@"Software\Classes\{progId}\CLSID");
            if (key is not null && AutostartRegistry.Text(key, null) is { Length: > 0 } clsid)
                return AutostartRegistry.ClassServer(clsid);
        }

        return null;
    }
}
