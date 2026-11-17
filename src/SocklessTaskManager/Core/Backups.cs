using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Sockless.TaskManager.Core;

/// <summary>
/// What a removal saves first, and where: .reg files in Registry Editor's own format (so opening
/// one puts the entry back), named by time and what they hold, in a folder under
/// %LOCALAPPDATA%\Sockless Task Manager. Shared by Autostart (entries) and Apps (uninstall entries
/// left behind), which also open Registry Editor at a key.
/// </summary>
internal static class Backups
{
    /// <summary>%LOCALAPPDATA%\Sockless Task Manager\<paramref name="name"/>; not created until something is saved.</summary>
    public static string Folder(string name) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", name);

    /// <summary>A new file's path in <paramref name="folder"/> (created): the time, then <paramref name="name"/> made safe, never an existing file.</summary>
    public static string NewFile(string folder, string name, string extension = "")
    {
        Directory.CreateDirectory(folder);
        return Unique(Path.Combine(folder, $"{Stamp()} {Safe(name)}{extension}"));
    }

    /// <summary>Saves an export as a .reg file in <paramref name="folder"/>; returns its path.</summary>
    public static string SaveReg(string folder, string name, string body)
    {
        var path = NewFile(folder, name, ".reg");
        File.WriteAllText(path, "Windows Registry Editor Version 5.00\r\n\r\n" + body, Encoding.Unicode);
        return path;
    }

    /// <summary>A key and everything under it, from the 64-bit view, in Registry Editor's export format.</summary>
    public static string ExportKey(RegistryHive hive, string path)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(path) ?? throw new IOException("It's no longer there.");
        var text = new StringBuilder();
        Export(key, $@"{LongHiveName(hive)}\{path}", text);
        return text.ToString();
    }

    /// <summary>One value, under its key's heading, in Registry Editor's export format.</summary>
    public static string ExportValue(RegistryHive hive, string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(path) ?? throw new IOException("It's no longer there.");
        return $"[{LongHiveName(hive)}\\{path}]\r\n{Line(key, name)}\r\n";
    }

    /// <summary>Registry Editor's name for the hive, for exports and its LastKey.</summary>
    public static string LongHiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        _ => hive.ToString(),
    };

    /// <summary>Opens Registry Editor at a key ("HKLM\Software\…"), through the LastKey it reopens at. Null, or what went wrong.</summary>
    public static string? OpenInRegistryEditor(string displayKey)
    {
        var full = displayKey
            .Replace(@"HKCU\", @"HKEY_CURRENT_USER\", StringComparison.Ordinal)
            .Replace(@"HKLM\", @"HKEY_LOCAL_MACHINE\", StringComparison.Ordinal)
            .Replace(@"HKCR\", @"HKEY_CLASSES_ROOT\", StringComparison.Ordinal);
        try
        {
            using (var applet = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                applet.SetValue("LastKey", $@"Computer\{full}");

            using var _ = Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException or IOException)
        {
            return "Couldn't open Registry Editor.";
        }
    }

    private static void Export(RegistryKey key, string fullPath, StringBuilder text)
    {
        text.Append('[').Append(fullPath).Append("]\r\n");
        foreach (var name in key.GetValueNames())
            text.Append(Line(key, name)).Append("\r\n");
        text.Append("\r\n");

        foreach (var name in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(name);
            if (child is not null)
                Export(child, $@"{fullPath}\{name}", text);
        }
    }

    /// <summary>One value in Registry Editor's export format.</summary>
    private static string Line(RegistryKey key, string name)
    {
        var label = name.Length == 0 ? "@" : $"\"{Escape(name)}\"";
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return key.GetValueKind(name) switch
        {
            RegistryValueKind.String => $"{label}=\"{Escape((string)value!)}\"",
            RegistryValueKind.DWord => $"{label}=dword:{(uint)(int)value!:x8}",
            RegistryValueKind.ExpandString => $"{label}=hex(2):{Hex(Encoding.Unicode.GetBytes((string)value! + "\0"))}",
            RegistryValueKind.MultiString => $"{label}=hex(7):{Hex(Encoding.Unicode.GetBytes(string.Concat(((string[])value!).Select(s => s + "\0")) + "\0"))}",
            RegistryValueKind.QWord => $"{label}=hex(b):{Hex(BitConverter.GetBytes((long)value!))}",
            _ => $"{label}=hex:{Hex(value as byte[] ?? [])}",
        };
    }

    private static string Escape(string text) => text.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Hex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return safe.Length > 80 ? safe[..80] : safe.Length == 0 ? "entry" : safe;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
