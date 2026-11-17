using System.Diagnostics;
using System.Globalization;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// What Windows logged about devices and drivers: driver installs, problems starting, removals and
/// devices that need more installation, from the Kernel-PnP configuration log (readable by every
/// user); drivers that didn't load, from the System log. The last 90 days, read when the Changes
/// tab is shown and after a device change while it is.
/// </summary>
internal static class DriverHistory
{
    private const int Max = 3000;
    private static readonly TimeSpan Age = TimeSpan.FromDays(90);

    public static List<DriverChange> Read()
    {
        var changes = new List<DriverChange>();
        var since = EventLog.Within(Age);

        foreach (var e in Safe(() => EventLog.Query("Microsoft-Windows-Kernel-PnP/Configuration",
            $"*[System[(EventID=400 or EventID=411 or EventID=420 or EventID=430) and TimeCreated[{since}]]]", Max)))
        {
            var device = e["DeviceInstanceId"];
            if (device is null)
                continue;

            switch (e.Id)
            {
                case 400:
                    changes.Add(new DriverChange(e.Time, ChangeKind.Installed, device, device, Installed(e), IsLive: false));
                    break;
                case 411:
                    changes.Add(new DriverChange(e.Time, ChangeKind.Problem, device, device,
                        $"Problem starting: {DriverText.Problem(Hex(e["Problem"]))}{Status(e["Status"])}", IsLive: false));
                    break;
                case 420:
                    changes.Add(new DriverChange(e.Time, ChangeKind.Removed, device, device, "Uninstalled: Windows removed the device and its settings", IsLive: false));
                    break;
                case 430:
                    changes.Add(new DriverChange(e.Time, ChangeKind.NeedsInstall, device, device, "Windows needs to finish installing it", IsLive: false));
                    break;
            }
        }

        foreach (var e in Safe(() => EventLog.Query("System",
            $"*[System[((Provider[@Name='Microsoft-Windows-Kernel-PnP'] and EventID=219) or (Provider[@Name='Service Control Manager'] and EventID=7026)) and TimeCreated[{since}]]]", 500)))
        {
            if (e.Id == 219)
            {
                // "\Driver\WUDFRd" couldn't load for the device in DriverName.
                var device = e["DriverName"];
                var driver = e["FailureName"]?.Replace(@"\Driver\", string.Empty, StringComparison.OrdinalIgnoreCase);
                changes.Add(new DriverChange(e.Time, ChangeKind.FailedToLoad, device, driver ?? device ?? "Unknown driver",
                    driver is null ? "Its driver didn't load" : $"The {driver} driver didn't load for it{Status(e["Status"])}", IsLive: false));
            }
            else if (e.At(0) is { } names)
            {
                foreach (var name in names.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    changes.Add(new DriverChange(e.Time, ChangeKind.FailedToLoad, null, name, "A boot or system start driver that didn't load at start", IsLive: false));
            }
        }

        return changes;
    }

    private static string Installed(LogEvent e)
    {
        var parts = new List<string>();
        if (e["DriverProvider"] is { Length: > 0 } provider)
            parts.Add(provider);
        if (e["DriverVersion"] is { Length: > 0 } version)
            parts.Add(version);
        if (e["DriverDate"] is { Length: > 0 } date)
            parts.Add($"dated {date}");
        if (e["DriverName"] is { Length: > 0 } inf)
            parts.Add(inf);
        return parts.Count == 0 ? "Driver installed" : $"Driver installed: {string.Join(" · ", parts)}";
    }

    /// <summary>An NTSTATUS worth showing: decimal (as 219 writes it) or hex, and not success.</summary>
    private static string Status(string? text)
    {
        var value = Hex(text) ?? (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0);
        return value == 0 ? string.Empty : $" (status 0x{value:X8})";
    }

    private static uint? Hex(string? text)
    {
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return null;
        return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static List<LogEvent> Safe(Func<List<LogEvent>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Driver history log failed: {ex}");
            return [];
        }
    }
}
