namespace Sockless.TaskManager.Shell;

/// <summary>
/// The window's preferences that outlive a session (and a restart as administrator): always on top.
/// A few <c>key=value</c> lines under %LOCALAPPDATA%, so there's no serializer to keep AOT-safe.
/// A missing or unreadable file just means the defaults.
/// </summary>
internal sealed class ShellSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", "window.ini");

    public bool AlwaysOnTop { get; set; }

    public static ShellSettings Load()
    {
        var settings = new ShellSettings();
        try
        {
            if (!File.Exists(FilePath))
                return settings;

            foreach (var line in File.ReadAllLines(FilePath))
            {
                var split = line.IndexOf('=');
                if (split <= 0)
                    continue;

                if (line[..split].Trim() == "topmost")
                    settings.AlwaysOnTop = line[(split + 1)..].Trim() == "true";
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
            File.WriteAllLines(FilePath, [$"topmost={(AlwaysOnTop ? "true" : "false")}"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience: losing them is not worth an error.
        }
    }
}
