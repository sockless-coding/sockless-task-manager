using System.Diagnostics;
using System.Globalization;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>A driver package added to the driver store, as Windows' setup log recorded it.</summary>
/// <param name="Package">The store's name for it: "xvdd.inf_amd64_1e22073ba7136f03".</param>
/// <param name="Inf">The name it was published under: "oem23.inf".</param>
/// <param name="Action">What installed it, in the log's words: "Device Install (DiInstallDriver)", "Device Install (Install Windows Update driver)".</param>
/// <param name="Source">The INF it came from (a path), or for Windows Update the hardware id it was for.</param>
internal sealed record PackageRecord(string Package, string Inf, DateTime Time, string Action, string? Source)
{
    public bool IsWindowsUpdate => Action.Contains("Windows Update", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads Windows' device setup log (%windir%\INF\setupapi.dev.log and the dated copies Windows
/// rotates it into), which every user can read. Each section starts with what ran and the INF it
/// was given ("&gt;&gt;&gt;  [Device Install (DiInstallDriver) - C:\…\foo.inf]"); a package added to the
/// store is "Published '…' to 'oemNN.inf'" inside it. The rotated copies never change, so they're
/// read once; only the current log is read again, when it has grown.
/// </summary>
internal sealed class SetupLog
{
    private readonly Dictionary<string, (long Length, DateTime Modified, List<PackageRecord> Records)> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every package published, oldest first; and when the oldest log starts, since older installs aren't in it.</summary>
    public (List<PackageRecord> Records, DateTime? Since) Read()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        string[] files;
        try
        {
            // "setupapi.dev.20260811_223010.log" sorts by date; the current "setupapi.dev.log" goes last.
            files = [.. Directory.GetFiles(folder, "setupapi.dev*.log").OrderBy(f => Path.GetFileName(f).Length == "setupapi.dev.log".Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], null);
        }

        var records = new List<PackageRecord>();
        DateTime? since = null;
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!_files.TryGetValue(file, out var cached) || cached.Length != info.Length || cached.Modified != info.LastWriteTimeUtc)
                    _files[file] = cached = (info.Length, info.LastWriteTimeUtc, Parse(file));

                records.AddRange(cached.Records);
                since ??= cached.Records.Count > 0 ? cached.Records[0].Time : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Setup log {file} unreadable: {ex.Message}");
            }
        }

        return (records, since);
    }

    private static List<PackageRecord> Parse(string file)
    {
        var records = new List<PackageRecord>();
        string action = string.Empty;
        string? source = null;
        var time = DateTime.MinValue;

        // Windows keeps writing the current log: share it.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(">>>  [", StringComparison.Ordinal))
            {
                // ">>>  [Device Install (DiInstallDriver) - C:\…\foo.inf]"
                var body = line[6..].TrimEnd().TrimEnd(']');
                var dash = body.IndexOf(" - ", StringComparison.Ordinal);
                action = dash < 0 ? body : body[..dash];
                source = dash < 0 ? null : body[(dash + 3)..].Trim();
            }
            else if (line.StartsWith(">>>  Section start ", StringComparison.Ordinal))
            {
                // ">>>  Section start 2026/07/15 12:09:41.743", local time.
                if (DateTime.TryParseExact(line[19..].Trim(), "yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
                    time = local.ToUniversalTime();
            }
            else if (line.Contains("Published '", StringComparison.Ordinal) && line.Contains("' to 'oem", StringComparison.OrdinalIgnoreCase))
            {
                // "Published 'xvdd.inf_amd64_1e22073ba7136f03\xvdd.inf' to 'oem23.inf'."
                var start = line.IndexOf("Published '", StringComparison.Ordinal) + 11;
                var end = line.IndexOf('\'', start);
                var to = line.IndexOf("' to '", StringComparison.Ordinal);
                if (end < 0 || to < 0)
                    continue;

                var published = line[start..end];
                var slash = published.IndexOf('\\');
                var package = slash > 0 ? published[..slash] : published;
                var infStart = to + 6;
                var infEnd = line.IndexOf('\'', infStart);
                if (infEnd > infStart)
                    records.Add(new PackageRecord(package, line[infStart..infEnd], time, action, source));
            }
        }

        return records;
    }
}
