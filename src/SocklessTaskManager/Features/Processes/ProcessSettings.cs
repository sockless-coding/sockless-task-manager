namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// The Processes page's preferences that outlive a session: which columns are shown and the sort.
/// A few <c>key=value</c> lines under %LOCALAPPDATA%, so there's no serializer to keep AOT-safe.
/// A missing or unreadable file just means the defaults.
/// </summary>
internal sealed class ProcessSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", "processes.ini");

    /// <summary>Null until saved once: use each column's default.</summary>
    public HashSet<ProcessColumn>? Columns { get; set; }

    public ProcessColumn? SortColumn { get; set; }

    public bool SortDescending { get; set; }

    public static ProcessSettings Load()
    {
        var settings = new ProcessSettings();
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
                    case "columns":
                        settings.Columns = [];
                        foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (Enum.TryParse<ProcessColumn>(name, out var column))
                                settings.Columns.Add(column);
                        }

                        break;
                    case "sort" when Enum.TryParse<ProcessColumn>(value, out var sort):
                        settings.SortColumn = sort;
                        break;
                    case "descending":
                        settings.SortDescending = value == "true";
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
                $"columns={string.Join(',', Columns ?? [])}",
                $"sort={SortColumn}",
                $"descending={(SortDescending ? "true" : "false")}",
            ]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience: losing them is not worth an error.
        }
    }
}
