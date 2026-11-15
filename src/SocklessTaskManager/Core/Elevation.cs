using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Sockless.TaskManager.Core;

/// <summary>Running as administrator: needed to see and act on other users' processes and sessions, and to trace every process start and exit.</summary>
internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    private const string PageArgument = "--page";

    public static bool IsElevated { get; } = Environment.IsPrivilegedProcess;

    /// <summary>Title of the page on screen, kept by the shell: the elevated copy opens on it.</summary>
    public static string? CurrentPage { get; set; }

    /// <summary>The page a restart asked for (<c>--page &lt;title&gt;</c>), if any.</summary>
    public static string? StartPage(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == PageArgument)
                return args[i + 1];
        }

        return null;
    }

    /// <summary>Starts an elevated copy on the same page and closes this one. Does nothing if the user declines the UAC prompt.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is not { } path)
            return;

        var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" };
        if (CurrentPage is { } page)
        {
            start.ArgumentList.Add(PageArgument);
            start.ArgumentList.Add(page);
        }

        try
        {
            using var _ = Process.Start(start);
        }
        catch (Win32Exception ex)
        {
            // Declined at the UAC prompt (ErrorCancelled), or Windows refused: keep running as we are.
            Debug.WriteLineIf(ex.NativeErrorCode != ErrorCancelled, $"Restart as administrator failed: {ex}");
            return;
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
