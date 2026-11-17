using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// Uninstalls, changes and repairs apps, each the way it was installed: its uninstaller or
/// msiexec, the package manager, DISM, Scoop or Chocolatey. Removing an uninstall entry saves it
/// as a .reg file first. Off the UI thread: these wait for what they start to finish.
/// </summary>
internal static class AppActions
{
    private const int ErrorCancelled = 1223;

    public static string BackupFolder { get; } = Backups.Folder("Apps backups");

    public static AppOutcome Run(AppAction action) => action switch
    {
        CommandAction command => RunCommand(command),
        PackageRemoval package => PackagedApps.Remove(package.FullName),
        CapabilityRemoval capability => OptionalFeatures.RemoveCapability(capability.Name),
        FeatureRemoval feature => OptionalFeatures.DisableFeature(feature.Name),
        _ => new AppOutcome("This can't be done from here."),
    };

    /// <summary>Saves the entry as a .reg file, then deletes it.</summary>
    public static AppOutcome RemoveEntry(EntryRemoval entry, string name)
    {
        try
        {
            var backup = Backups.SaveReg(BackupFolder, name, Backups.ExportKey(entry.Hive, entry.Key));
            using var root = RegistryKey.OpenBaseKey(entry.Hive, RegistryView.Registry64);
            root.DeleteSubKeyTree(entry.Key, throwOnMissingSubKey: false);
            return new AppOutcome(null, Backup: backup);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return new AppOutcome("Windows refused: removing an entry for everyone needs administrator rights.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Debug.WriteLine($"Removing an uninstall entry failed: {ex}");
            return new AppOutcome("Windows couldn't remove the entry.");
        }
    }

    /// <summary>Starts it (asking for administrator rights when it's marked to) and waits for it to exit.</summary>
    private static AppOutcome RunCommand(CommandAction command)
    {
        var start = new ProcessStartInfo(command.Program, command.Arguments) { UseShellExecute = true };
        if (Path.GetDirectoryName(command.Program) is { } folder && Directory.Exists(folder))
            start.WorkingDirectory = folder;
        if (command.Elevate && !Elevation.IsElevated)
            start.Verb = "runas";

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return AppOutcome.Done;

            process.WaitForExit();
            return command.IsMsi ? Msi(process.ExitCode) : AppOutcome.Done;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new AppOutcome("Cancelled at the administrator prompt.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3)
        {
            return new AppOutcome($"{Path.GetFileName(command.Program)} isn't there, so it can't be run.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Starting {command.CommandLine} failed: {ex}");
            return new AppOutcome($"Windows couldn't start {Path.GetFileName(command.Program)}.");
        }
    }

    /// <summary>What msiexec's exit code means.</summary>
    private static AppOutcome Msi(int code) => code switch
    {
        0 => AppOutcome.Done,
        1641 or 3010 => new AppOutcome(null, NeedsRestart: true),
        1602 => new AppOutcome("Cancelled."),
        1605 => new AppOutcome("Windows Installer says this product isn't installed: its entry may be left over. Remove the entry if it stays."),
        1618 => new AppOutcome("Another installation is in progress. Try again when it's finished."),
        1612 => new AppOutcome("Windows Installer can't find the original installation files it needs. Reinstall it from its setup file, then try again."),
        1603 => new AppOutcome("Windows Installer stopped with an error (1603). The program's own log, or Event Viewer → Application, says why."),
        _ => new AppOutcome($"Windows Installer stopped with error {code}."),
    };
}
