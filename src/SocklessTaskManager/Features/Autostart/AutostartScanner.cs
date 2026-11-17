using System.Diagnostics;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>An entry with the file it runs, as read in one scan.</summary>
internal sealed record AutostartEntry(AutostartItem Item, AutostartFile? File);

/// <param name="IsChecking">Signatures are still being verified; another snapshot follows.</param>
internal sealed record AutostartSnapshot(IReadOnlyList<AutostartEntry> Entries, bool IsChecking);

/// <summary>
/// Reads every source, then the files they name: version resources at once, signatures after,
/// since verifying hundreds of files (catalog lookups for Windows' own) takes a few seconds.
/// Signatures are kept between scans while a file's size and time stay the same, so a rescan
/// after a change is quick. One scan at a time, on a background thread.
/// </summary>
internal sealed class AutostartScanner
{
    private static readonly Func<IEnumerable<AutostartItem>>[] Sources =
    [
        SignInSources.Scan,
        TaskSources.Scan,
        ServiceSources.Scan,
        ExplorerSources.Scan,
        OfficeSources.Scan,
        SystemSources.Scan,
        WmiSubscriptions.Scan,
    ];

    /// <summary>Publish signatures in batches no further apart than this, so the list fills in as it goes.</summary>
    private static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(1.5);

    private readonly Dictionary<string, (long Length, DateTime Modified, Signature Signature)> _signatures = new(StringComparer.OrdinalIgnoreCase);

    public void Scan(Action<AutostartSnapshot> publish)
    {
        var items = Collect();
        var files = new Dictionary<string, AutostartFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in items.Select(i => i.Image).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            files[image] = Read(image);

        var pending = files.Values.Where(f => f.Signature.State == SignatureState.Pending).Select(f => f.Path).ToList();
        publish(Build(items, files, pending.Count > 0));
        if (pending.Count == 0)
            return;

        using var checker = new SignatureChecker();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < pending.Count; i++)
        {
            var file = files[pending[i]];
            Signature signature;
            try
            {
                signature = checker.Check(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                signature = new Signature(SignatureState.Unreadable);
            }

            files[file.Path] = file with { Signature = signature };
            if (TryStamp(file.Path) is { } stamp)
                _signatures[file.Path] = (stamp.Length, stamp.Modified, signature);

            if (clock.Elapsed > PublishEvery && i < pending.Count - 1)
            {
                publish(Build(items, files, isChecking: true));
                clock.Restart();
            }
        }

        publish(Build(items, files, isChecking: false));
    }

    private static List<AutostartItem> Collect()
    {
        var items = new List<AutostartItem>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Sources)
        {
            try
            {
                foreach (var item in source())
                {
                    // Two entries can share a name in one place (a list with a repeated item): keep both.
                    var unique = item;
                    for (var n = 2; !ids.Add(unique.Id); n++)
                        unique = item with { Id = $"{item.Id}#{n}" };
                    items.Add(unique);
                }
            }
            catch (Exception ex)
            {
                // One unreadable source mustn't empty the page.
                Debug.WriteLine($"Autostart source failed: {ex}");
            }
        }

        return items;
    }

    private static AutostartSnapshot Build(List<AutostartItem> items, Dictionary<string, AutostartFile> files, bool isChecking) =>
        new([.. items.Select(i => new AutostartEntry(i, i.Image is { } image ? Packaged(i, files[image]) : null))], isChecking);

    /// <summary>A packaged app's file without a signature of its own is signed by its package.</summary>
    private static AutostartFile Packaged(AutostartItem item, AutostartFile file) =>
        item.PackageSigner is { } signer && file is { Exists: true, Signature.State: SignatureState.Unsigned }
            ? file with { Signature = new Signature(SignatureState.Signed, signer) { ByPackage = true } }
            : file;

    private AutostartFile Read(string path)
    {
        if (TryStamp(path) is not { } stamp)
            return new AutostartFile(path, false, null, null, null, null, null, Signature.None);

        string? company = null, description = null, product = null, version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = Blank(info.CompanyName);
            description = Blank(info.FileDescription);
            product = Blank(info.ProductName);
            version = Blank(info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        var signature = _signatures.TryGetValue(path, out var known) && known.Length == stamp.Length && known.Modified == stamp.Modified
            ? known.Signature
            : Signature.Pending;
        return new AutostartFile(path, true, company, description, product, version, stamp.Modified.ToLocalTime(), signature);
    }

    private static (long Length, DateTime Modified)? TryStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
