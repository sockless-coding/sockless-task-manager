using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Reading the registry for the scan: the 64-bit view always (WOW6432Node paths are named where
/// they matter), errors as "nothing there", and the key Sockless moves turned-off entries into
/// skipped wherever subkeys are listed.
/// </summary>
internal static partial class AutostartRegistry
{
    /// <summary>Where a turned-off value or key waits, beside where it was. Windows doesn't look in it.</summary>
    public const string DisabledKey = "Sockless disabled";

    public static RegistryKey? Open(RegistryHive hive, string path, bool writable = false)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.OpenSubKey(path, writable);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string Display(RegistryHive hive, string path) => $@"{HiveName(hive)}\{path}";

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        _ => hive.ToString(),
    };

    /// <summary>Registry Editor's name for the hive, for its LastKey.</summary>
    public static string LongHiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        _ => hive.ToString(),
    };

    public static string[] ValueNames(RegistryKey key)
    {
        try
        {
            return key.GetValueNames();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>The subkeys, without the one turned-off entries wait in.</summary>
    public static string[] SubKeyNames(RegistryKey key)
    {
        try
        {
            return [.. key.GetSubKeyNames().Where(n => !n.Equals(DisabledKey, StringComparison.OrdinalIgnoreCase))];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>A value as text, unexpanded: what the registration says. Multi-strings are joined with spaces.</summary>
    public static string? Text(RegistryKey key, string? name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                string text => text,
                string[] lines => string.Join(" ", lines.Where(l => l.Length > 0)),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string[] Lines(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
            {
                string[] lines => [.. lines.Where(l => l.Trim().Length > 0)],
                string text when text.Trim().Length > 0 => [text],
                _ => [],
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public static int? Number(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) is int number ? number : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static byte[]? Binary(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) as byte[];
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file a COM class runs: its InprocServer32 (a DLL) or LocalServer32 (a program), from the
    /// 64-bit machine-wide classes, or the user's own, which take precedence.
    /// </summary>
    public static string? ClassServer(string clsid)
    {
        if (!clsid.StartsWith('{'))
            clsid = "{" + clsid.Trim('{', '}') + "}";

        foreach (var hive in (ReadOnlySpan<RegistryHive>)[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            foreach (var server in (ReadOnlySpan<string>)["InprocServer32", "LocalServer32"])
            {
                using var key = Open(hive, $@"Software\Classes\CLSID\{clsid}\{server}");
                if (key is not null && Text(key, null) is { Length: > 0 } path)
                    return path;
            }
        }

        return null;
    }

    /// <summary>A COM class's name, from the default value of its CLSID key.</summary>
    public static string? ClassName(string clsid)
    {
        if (!clsid.StartsWith('{'))
            clsid = "{" + clsid.Trim('{', '}') + "}";

        foreach (var hive in (ReadOnlySpan<RegistryHive>)[RegistryHive.CurrentUser, RegistryHive.LocalMachine])
        {
            using var key = Open(hive, $@"Software\Classes\CLSID\{clsid}");
            if (key is not null && Text(key, null) is { Length: > 0 } name)
                return name;
        }

        return null;
    }

    /// <summary>"@%SystemRoot%\system32\foo.dll,-101" and "@{Package?ms-resource://…}" as the text they name; other text as is.</summary>
    public static unsafe string Indirect(string text)
    {
        if (!text.StartsWith('@'))
            return text;

        var buffer = stackalloc char[1024];
        if (SHLoadIndirectString(text, buffer, 1024, 0) >= 0)
            return new string(buffer);

        // Driver packages write "@oem12.inf,%Name%;Fallback text": the text after the semicolon is what to show.
        var fallback = text.LastIndexOf(';');
        return fallback >= 0 && fallback < text.Length - 1 ? text[(fallback + 1)..] : text;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* output, uint size, nint reserved);
}
