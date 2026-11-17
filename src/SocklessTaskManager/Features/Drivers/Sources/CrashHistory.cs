using System.Diagnostics;
using System.Globalization;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// Blue screens, unexpected restarts, display driver resets and recovered hangs, from the logs
/// Windows keeps and the dumps it writes:
/// <list type="bullet">
/// <item>System log: the stop error report after restarting (WER-SystemErrorReporting 1001), an
/// unclean restart (Kernel-Power 41) and a display driver reset (Display 4101).</item>
/// <item>Application log: Windows Error Reporting's "BlueScreen" and "LiveKernelEvent" reports, whose
/// bucket names the driver Windows blamed ("…_IMAGE_nvlddmkm.sys").</item>
/// <item>Dumps (Minidump, MEMORY.DMP, LiveKernelReports), which only administrators can read:
/// <see cref="KernelDump"/> finds the driver involved.</item>
/// </list>
/// Read when the Crashes tab is first shown and on Refresh.
/// </summary>
internal static class CrashHistory
{
    private static readonly TimeSpan Age = TimeSpan.FromDays(365);

    /// <summary>A report is written at the next start, minutes after the crash; a dump's time is the crash's.</summary>
    private static readonly TimeSpan SameCrash = TimeSpan.FromMinutes(45);

    public static CrashSnapshot Read()
    {
        var since = EventLog.Within(Age);
        var system = Safe(() => EventLog.Query("System",
            $"*[System[TimeCreated[{since}] and ((Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001)"
            + " or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41)"
            + " or (Provider[@Name='Display'] and EventID=4101))]]", 1000));
        // WER logs a report again each time it retries sending it: one per report id, the first time it was logged.
        var reports = Safe(() => EventLog.Query("Application",
                $"*[System[Provider[@Name='Windows Error Reporting'] and EventID=1001 and TimeCreated[{since}]]"
                + " and EventData[Data='BlueScreen' or Data='LiveKernelEvent']]", 5000))
            .Where(e => e.At(2) is "BlueScreen" or "LiveKernelEvent")
            .GroupBy(e => ReportId(e) ?? $"{e.Time.Ticks}")
            .Select(g => g.MinBy(e => e.Time)! is var first && first.At(0) is { Length: > 0 }
                ? first
                : g.FirstOrDefault(e => e.At(0) is { Length: > 0 }) is { } named ? named with { Time = first.Time } : first)
            .ToList();

        var (dumps, unreadable) = Dumps();
        var crashes = new List<DriverCrash>();
        var usedDumps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedReports = new HashSet<LogEvent>(ReferenceEqualityComparer.Instance);

        // Stop errors: the System log's report, with the WER report's blame and the dump's.
        var stops = system.Where(e => e.Provider == "Microsoft-Windows-WER-SystemErrorReporting").ToList();
        foreach (var stop in stops)
        {
            var (code, parameters) = ParseStop(stop.At(0));
            var dumpPath = stop.At(1) is { Length: > 0 } p ? p : null;
            var dump = dumps.FirstOrDefault(d => dumpPath is not null && string.Equals(d.Path, dumpPath, StringComparison.OrdinalIgnoreCase))
                ?? Closest(dumps.Where(d => !usedDumps.Contains(d.Path) && !IsLive(d.Path)), stop.Time);
            var report = Closest(reports.Where(r => r.At(2) == "BlueScreen" && !usedReports.Contains(r)), stop.Time);
            if (dump is not null)
                usedDumps.Add(dump.Path);
            if (report is not null)
                usedReports.Add(report);

            crashes.Add(Stop(dump?.Time ?? stop.Time, code ?? dump?.Code, parameters.Count > 0 ? parameters : dump?.Parameters ?? [],
                dump, report, dumpPath ?? dump?.Path, stop.At(2)));
        }

        // Unclean restarts: a stop error when it has a code and no report says so already, else power or a hang.
        foreach (var power in system.Where(e => e.Provider == "Microsoft-Windows-Kernel-Power"))
        {
            var code = Number(power["BugcheckCode"]);
            if (code is > 0)
            {
                if (stops.Any(s => (s.Time - power.Time).Duration() < SameCrash))
                    continue;

                var dump = Closest(dumps.Where(d => !usedDumps.Contains(d.Path) && !IsLive(d.Path)), power.Time);
                if (dump is not null)
                    usedDumps.Add(dump.Path);
                var report = Closest(reports.Where(r => r.At(2) == "BlueScreen" && !usedReports.Contains(r)), power.Time);
                if (report is not null)
                    usedReports.Add(report);

                crashes.Add(Stop(dump?.Time ?? power.Time, (uint)code.Value,
                    dump?.Parameters ?? [.. new[] { "BugcheckParameter1", "BugcheckParameter2", "BugcheckParameter3", "BugcheckParameter4" }
                        .Select(n => Hex(power[n]) ?? 0)],
                    dump, report, dump?.Path, null));
                continue;
            }

            crashes.Add(new DriverCrash(power.Time, CrashKind.UnexpectedRestart, null, [], null, null, null, null));
        }

        // Dumps no log mentions (logs cleared, or too old for them).
        foreach (var dump in dumps.Where(d => !usedDumps.Contains(d.Path) && !IsLive(d.Path)))
        {
            usedDumps.Add(dump.Path);
            crashes.Add(Stop(dump.Time ?? File.GetLastWriteTimeUtc(dump.Path), dump.Code, dump.Parameters, dump, null, dump.Path, null));
        }

        // BlueScreen reports with no System log record.
        foreach (var report in reports.Where(r => r.At(2) == "BlueScreen" && !usedReports.Contains(r)))
        {
            var (driver, _) = FromBucket(report.At(0));
            crashes.Add(new DriverCrash(report.Time, CrashKind.BlueScreen, Hex(report.At(5)) is { } c ? (uint)c : null, [],
                driver, driver is null ? null : "Windows Error Reporting's analysis names it", null, ReportId(report)));
        }

        // Recovered hangs (live kernel events), with their live dump when there is one.
        foreach (var report in reports.Where(r => r.At(2) == "LiveKernelEvent"))
        {
            var attached = AttachedDump(report);
            var dump = dumps.FirstOrDefault(d => attached is not null && string.Equals(d.Path, attached, StringComparison.OrdinalIgnoreCase))
                ?? Closest(dumps.Where(d => !usedDumps.Contains(d.Path) && IsLive(d.Path)), report.Time);
            if (dump is not null)
                usedDumps.Add(dump.Path);
            var (driver, blame) = dump?.Culprit is { } culprit ? (culprit, dump.Blame) : FromBucket(report.At(0));
            var time = dump?.Time ?? DumpNameTime(attached) ?? report.Time;
            crashes.Add(new DriverCrash(time, CrashKind.Recovered, Hex(report.At(5)) is { } c ? (uint)c : dump?.Code, dump?.Parameters ?? [],
                driver, driver is null ? null : blame, dump?.Path ?? attached, ReportId(report)));
        }

        // Display driver resets: the driver is named in the event.
        foreach (var reset in system.Where(e => e.Provider == "Display"))
        {
            var name = reset.At(0) is { Length: > 0 } n ? (n.Contains('.') ? n : n + ".sys") : null;
            crashes.Add(new DriverCrash(reset.Time, CrashKind.DisplayReset, null, [], name, name is null ? null : "The event names it", null, null));
        }

        crashes.Sort((a, b) => b.Time.CompareTo(a.Time));
        return new CrashSnapshot(crashes, unreadable);
    }

    private static DriverCrash Stop(DateTime time, uint? code, IReadOnlyList<ulong> parameters, DumpInfo? dump, LogEvent? report, string? dumpPath, string? reportId)
    {
        string? driver = null, blame = null;
        if (dump?.Culprit is { } culprit)
            (driver, blame) = (culprit, dump.Blame);
        else if (FromBucket(report?.At(0)) is { Driver: { } named } bucket)
            (driver, blame) = (named, bucket.Blame);
        else if (dump?.Blame is { } why)
            blame = why;

        var existing = dumpPath ?? dump?.Path;
        return new DriverCrash(time, CrashKind.BlueScreen, code, parameters, driver, blame, existing, reportId ?? (report is null ? null : ReportId(report)));
    }

    /// <summary>"0x0000009f (0x0000000000000003, 0xffffb38a…, …)" as the code and its parameters.</summary>
    private static (uint? Code, List<ulong> Parameters) ParseStop(string? text)
    {
        var parameters = new List<ulong>();
        if (string.IsNullOrWhiteSpace(text))
            return (null, parameters);

        var open = text.IndexOf('(');
        var code = Hex(open > 0 ? text[..open] : text);
        if (open > 0)
        {
            foreach (var part in text[(open + 1)..].TrimEnd(')', ' ').Split(','))
                parameters.Add(Hex(part) ?? 0);
        }

        return (code is null ? null : (uint)code.Value, parameters);
    }

    /// <summary>
    /// A WER bucket names the module it blamed: "0x116_IMAGE_nvlddmkm.sys", "LKD_0x141_Tdr:6_IMAGE_amdkmdag.sys_…",
    /// or "AV_R_INVALID_nvlddmkm!unknown_function".
    /// </summary>
    private static (string? Driver, string? Blame) FromBucket(string? bucket)
    {
        if (string.IsNullOrEmpty(bucket))
            return (null, null);

        var image = bucket.IndexOf("IMAGE_", StringComparison.OrdinalIgnoreCase);
        if (image >= 0)
        {
            var rest = bucket[(image + 6)..];
            foreach (var extension in new[] { ".sys", ".dll", ".exe" })
            {
                var end = rest.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
                if (end > 0)
                    return Named(rest[..(end + extension.Length)]);
            }
        }

        var bang = bucket.IndexOf('!');
        if (bang > 0)
        {
            var start = bucket.LastIndexOf('_', bang) + 1;
            if (bang - start > 0)
                return Named(bucket[start..bang] + ".sys");
        }

        return (null, null);

        static (string?, string?) Named(string module) =>
            module.StartsWith("nt", StringComparison.OrdinalIgnoreCase) && module.Contains("krnl", StringComparison.OrdinalIgnoreCase)
                ? (null, null)
                : (module, "Windows Error Reporting's analysis names it");
    }

    /// <summary>Every dump this user can read, and whether some couldn't be.</summary>
    private static (List<DumpInfo> Dumps, bool Unreadable) Dumps()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var dumps = new List<DumpInfo>();
        var unreadable = false;

        void Add(string path)
        {
            if (KernelDump.Read(path) is { } dump)
                dumps.Add(dump);
            else if (!Elevation.IsElevated)
                unreadable = true;
        }

        foreach (var folder in new[] { Path.Combine(windows, "Minidump"), Path.Combine(windows, "LiveKernelReports") })
        {
            try
            {
                if (!Directory.Exists(folder))
                    continue;

                foreach (var file in Directory.EnumerateFiles(folder, "*.dmp", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Take(200))
                    Add(file);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                unreadable = true;
            }
        }

        var memory = Path.Combine(windows, "MEMORY.DMP");
        try
        {
            if (File.Exists(memory))
                Add(memory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            unreadable = true;
        }

        // Without elevation the Minidump folder can't even be listed: say so if any crash might have a dump.
        if (!Elevation.IsElevated)
            unreadable = true;

        return (dumps, unreadable);
    }

    /// <summary>WER's report ID, which the Reliability Monitor and the report queue use.</summary>
    private static string? ReportId(LogEvent report) => report.At(19) is { Length: > 0 } id ? id : null;

    /// <summary>The dump attached to a WER report: its attached files are one path per line, "\\?\"-prefixed.</summary>
    private static string? AttachedDump(LogEvent report) =>
        report.At(15)?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.StartsWith(@"\\?\", StringComparison.Ordinal) ? f[4..] : f)
            .FirstOrDefault(f => f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));

    /// <summary>Live kernel reports are named for when they were written: "WATCHDOG-20261108-1554.dmp" (local time).</summary>
    private static DateTime? DumpNameTime(string? path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name is null || name.Length < 14)
            return null;

        return DateTime.TryParseExact(name[^13..], "yyyyMMdd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? local.ToUniversalTime()
            : null;
    }

    private static bool IsLive(string path) => path.Contains(@"\LiveKernelReports\", StringComparison.OrdinalIgnoreCase);

    private static DumpInfo? Closest(IEnumerable<DumpInfo> dumps, DateTime time) =>
        dumps.Select(d => (Dump: d, Gap: ((d.Time ?? File.GetLastWriteTimeUtc(d.Path)) - time).Duration()))
            .Where(c => c.Gap < SameCrash)
            .OrderBy(c => c.Gap)
            .Select(c => c.Dump)
            .FirstOrDefault();

    private static LogEvent? Closest(IEnumerable<LogEvent> reports, DateTime time) =>
        reports.Select(r => (Report: r, Gap: (r.Time - time).Duration()))
            .Where(c => c.Gap < SameCrash)
            .OrderBy(c => c.Gap)
            .Select(c => c.Report)
            .FirstOrDefault();

    private static long? Number(string? text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : Hex(text) is { } hex ? (long)hex : null;

    private static ulong? Hex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static List<LogEvent> Safe(Func<List<LogEvent>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Crash history log failed: {ex}");
            return [];
        }
    }
}
