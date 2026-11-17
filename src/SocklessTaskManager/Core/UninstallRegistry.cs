using System.Security;
using Microsoft.Win32;

namespace Sockless.TaskManager.Core;

/// <summary>
/// The uninstall registry, where setup programs and Windows Installer register what they installed:
/// for everyone (HKLM, 64- and 32-bit) and for this user (HKCU). Shared by Apps (everything
/// installed) and Drivers (the package a driver came with, and vendors' update tools). Read in the
/// 64-bit view, with unreadable keys taken as "nothing there".
/// </summary>
internal static class UninstallRegistry
{
    public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Uninstall32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>The lists, with the architecture their entries are for (null: this user's, either).</summary>
    public static readonly (RegistryHive Hive, string Path, string? Architecture)[] Lists =
    [
        (RegistryHive.LocalMachine, UninstallPath, "64-bit"),
        (RegistryHive.LocalMachine, Uninstall32Path, "32-bit"),
        (RegistryHive.CurrentUser, UninstallPath, null),
        (RegistryHive.CurrentUser, Uninstall32Path, "32-bit"),
    ];

    /// <summary>Every entry's key, one at a time: each is disposed when the caller moves to the next.</summary>
    public static IEnumerable<(RegistryHive Hive, string ListPath, string KeyName, RegistryKey Key, string? Architecture)> Entries()
    {
        foreach (var (hive, path, architecture) in Lists)
        {
            using var list = Open(hive, path);
            if (list is null)
                continue;

            string[] names;
            try
            {
                names = list.GetSubKeyNames();
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var name in names)
            {
                RegistryKey? key;
                try
                {
                    key = list.OpenSubKey(name);
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                if (key is null)
                    continue;

                using (key)
                    yield return (hive, path, name, key, architecture);
            }
        }
    }

    /// <summary>A value as trimmed text, or null when missing, blank or unreadable.</summary>
    public static string? Text(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) is string text && text.Trim().TrimEnd('\0') is { Length: > 0 } trimmed ? trimmed : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static RegistryKey? Open(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
