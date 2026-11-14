using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Sockless.TaskManager.Core;

/// <summary>Running as administrator: needed to see and act on other users' processes and sessions, and to trace every process start and exit.</summary>
internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated { get; } = Environment.IsPrivilegedProcess;

    /// <summary>Starts an elevated copy and closes this one. Does nothing if the user declines the UAC prompt.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is not { } path)
            return;

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
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
