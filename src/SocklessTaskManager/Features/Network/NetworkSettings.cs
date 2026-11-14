namespace Sockless.TaskManager.Features.Network;

/// <summary>What the Network page records while it isn't on screen.</summary>
internal enum BackgroundRecording
{
    /// <summary>Nothing: the page monitors only while it's shown.</summary>
    Off,

    /// <summary>New connections of watched processes only.</summary>
    Watched,

    /// <summary>Everything, as if the page were always open.</summary>
    All,
}

/// <summary>
/// The Network page's preferences that outlive a session: background recording, watched processes,
/// reverse lookups. A few <c>key=value</c> lines under %LOCALAPPDATA%, like the Processes page's,
/// so there's no serializer to keep AOT-safe. A missing or unreadable file means the defaults.
/// </summary>
internal sealed class NetworkSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", "network.ini");

    public BackgroundRecording Background { get; set; } = BackgroundRecording.Watched;

    /// <summary>Image names (<c>chrome.exe</c>), compared without case.</summary>
    public HashSet<string> Watched { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ReverseLookups { get; set; } = true;

    /// <summary>Filter chips that are on, by key.</summary>
    public HashSet<string> Filters { get; } = new(StringComparer.Ordinal);

    /// <summary>The note offering a location database was closed.</summary>
    public bool GeoHintDismissed { get; set; }

    public static NetworkSettings Load()
    {
        var settings = new NetworkSettings();
        try
        {
            if (!File.Exists(FilePath))
                return settings;

            foreach (var line in File.ReadAllLines(FilePath))
            {
                var split = line.IndexOf('=');
                if (split <= 0)
                    continue;

                var value = line[(split + 1)..].Trim();
                switch (line[..split].Trim())
                {
                    case "background" when Enum.TryParse<BackgroundRecording>(value, ignoreCase: true, out var background):
                        settings.Background = background;
                        break;
                    case "watched":
                        foreach (var name in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            settings.Watched.Add(name);
                        break;
                    case "reverse":
                        settings.ReverseLookups = value != "false";
                        break;
                    case "filters":
                        foreach (var key in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            settings.Filters.Add(key);
                        break;
                    case "geohint":
                        settings.GeoHintDismissed = value == "dismissed";
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath,
            [
                $"background={Background}",
                $"watched={string.Join('|', Watched.Order(StringComparer.OrdinalIgnoreCase))}",
                $"reverse={(ReverseLookups ? "true" : "false")}",
                $"geohint={(GeoHintDismissed ? "dismissed" : "shown")}",
                $"filters={string.Join(',', Filters.Order(StringComparer.Ordinal))}",
            ]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience: losing them is not worth an error.
        }
    }
}
