using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>Reading the registry for the scan: the 64-bit view (WOW6432Node paths are named), errors as "nothing there".</summary>
internal static partial class AppRegistry
{
    public static RegistryKey? Open(RegistryHive hive, string path)
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

    public static string Display(RegistryHive hive, string path) => $@"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\{path}";

    public static string[] SubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public static RegistryKey? OpenSubKey(RegistryKey key, string name)
    {
        try
        {
            return key.OpenSubKey(name);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>A value as trimmed text, or null when missing or blank. Expandable strings are expanded.</summary>
    public static string? Text(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                string text when text.Trim().Length > 0 => text.Trim().TrimEnd('\0'),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>A DWORD, or a string holding a number (some installers write "1").</summary>
    public static int? Number(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) switch
            {
                int number => number,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static bool Has(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>When the key was last written: Programs and Features' install date when an installer gives none.</summary>
    public static unsafe DateTime? LastWritten(RegistryKey key)
    {
        try
        {
            long time;
            return RegQueryInfoKeyW(key.Handle.DangerousGetHandle(), null, null, 0, null, null, null, null, null, null, null, &time) == 0 && time > 0
                ? DateTime.FromFileTimeUtc(time).ToLocalTime()
                : null;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [LibraryImport("advapi32.dll")]
    private static unsafe partial int RegQueryInfoKeyW(nint key, char* className, uint* classLength, nint reserved, uint* subKeys,
        uint* maxSubKeyLength, uint* maxClassLength, uint* values, uint* maxValueNameLength, uint* maxValueLength,
        uint* securityDescriptor, long* lastWriteTime);
}
