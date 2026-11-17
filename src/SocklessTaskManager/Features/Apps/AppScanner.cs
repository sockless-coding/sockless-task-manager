using System.Diagnostics;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <param name="IsReading">More sources are still being read; another snapshot follows.</param>
internal sealed record AppSnapshot(IReadOnlyList<AppItem> Items, bool IsReading);

/// <summary>
/// Reads every source in order of how quickly it answers, publishing after each so the list fills
/// in: the uninstall registry and package managers at once, packages in a second or two, optional
/// features (DISM, administrators only) last, after several seconds. DISM's answer is kept between
/// scans and read again only when asked, since it's slow and loads a lot. One scan at a time.
/// </summary>
internal sealed class AppScanner
{
    private List<AppItem>? _features;

    /// <param name="readFeatures">Read optional features again, rather than reuse the last answer.</param>
    public void Scan(Action<AppSnapshot> publish, bool readFeatures)
    {
        var items = new List<AppItem>(600);
        Add(items, InstalledPrograms.Scan);
        Add(items, PackageManagers.Scan);

        // Until they're read again, the features last read stand in.
        var known = _features ?? [];
        publish(new AppSnapshot([.. items, .. known], IsReading: true));

        Add(items, PackagedApps.Scan);
        if (!Elevation.IsElevated || (_features is not null && !readFeatures))
        {
            publish(new AppSnapshot([.. items, .. known], IsReading: false));
            return;
        }

        publish(new AppSnapshot([.. items, .. known], IsReading: true));
        var read = new List<AppItem>();
        Add(read, OptionalFeatures.Scan);
        _features = read;
        publish(new AppSnapshot([.. items, .. read], IsReading: false));
    }

    private static void Add(List<AppItem> items, Func<IEnumerable<AppItem>> source)
    {
        try
        {
            items.AddRange(source());
        }
        catch (Exception ex)
        {
            // One unreadable source mustn't empty the page.
            Debug.WriteLine($"Apps source failed: {ex}");
        }
    }
}
