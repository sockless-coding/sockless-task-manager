using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Turns entries off and on, and removes them. Off is always undoable: Windows' own switch where
/// there is one (StartupApproved, a task's Enabled, a service's startup type, a packaged task's
/// State), else the value or key moved aside into a "Sockless disabled" key. Removing saves what
/// goes first: a .reg file, the task's XML, or the file itself, in the backups folder.
/// Off the UI thread; each call returns null or what went wrong.
/// </summary>
internal static partial class AutostartChanges
{
    private const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string NeedsAdmin = "Windows refused: changing this needs administrator rights.";

    public static string BackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sockless Task Manager", "Autostart backups");

    public static string? SetEnabled(AutostartTarget target, bool enabled)
    {
        try
        {
            return target switch
            {
                ApprovedSwitch s => Approve(s, enabled),
                PackagedTaskSwitch s => SetPackagedTask(s, enabled),
                TaskTarget t => TaskScheduler.SetEnabled(t.Path, enabled),
                ServiceTarget s => SetService(s, enabled),
                MovedValueTarget m => enabled
                    ? MoveValue(m.Hive, $@"{m.Key}\{AutostartRegistry.DisabledKey}", m.Key, m.ValueName)
                    : MoveValue(m.Hive, m.Key, $@"{m.Key}\{AutostartRegistry.DisabledKey}", m.ValueName),
                MovedKeyTarget m => enabled
                    ? MoveKey(m.Hive, $@"{m.Parent}\{AutostartRegistry.DisabledKey}", m.Parent, m.Name)
                    : MoveKey(m.Hive, m.Parent, $@"{m.Parent}\{AutostartRegistry.DisabledKey}", m.Name),
                OfficeAddinSwitch o => SetOfficeAddin(o, enabled),
                _ => "This can't be turned off from here.",
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return NeedsAdmin;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine($"Autostart change failed: {ex}");
            return "Windows couldn't make that change.";
        }
    }

    /// <summary>
    /// Removes the entry; <paramref name="backup"/> is where what was removed was saved. A Run value's
    /// or Startup file's StartupApproved switch (<paramref name="toggle"/>) goes with it.
    /// </summary>
    public static string? Remove(AutostartTarget target, string name, out string? backup, AutostartTarget? toggle = null)
    {
        var failure = RemoveTarget(target, name, out backup);
        if (failure is null && toggle is ApprovedSwitch approved)
        {
            try
            {
                using var key = AutostartRegistry.Open(approved.Hive, $@"{ApprovedRoot}\{approved.List}", writable: true);
                key?.DeleteValue(approved.ValueName, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
            }
        }

        return failure;
    }

    private static string? RemoveTarget(AutostartTarget target, string name, out string? backup)
    {
        backup = null;
        try
        {
            switch (target)
            {
                case RegistryValueTarget v:
                    backup = SaveReg(name, ExportValue(v.Hive, v.Key, v.ValueName));
                    using (var key = AutostartRegistry.Open(v.Hive, v.Key, writable: true) ?? throw new UnauthorizedAccessException())
                        key.DeleteValue(v.ValueName, throwOnMissingValue: false);
                    DropEmptyDisabledKey(v.Hive, v.Key);
                    return null;

                case RegistryKeyTarget k:
                    backup = SaveReg(name, ExportKey(k.Hive, k.Key));
                    using (var root = RegistryKey.OpenBaseKey(k.Hive, RegistryView.Registry64))
                        root.DeleteSubKeyTree(k.Key, throwOnMissingSubKey: false);
                    return null;

                case ListItemTarget l:
                    backup = SaveReg(name, ExportValue(l.Hive, l.Key, l.ValueName));
                    return RemoveListItem(l);

                case FileTarget f:
                    Directory.CreateDirectory(BackupFolder);
                    backup = Unique(Path.Combine(BackupFolder, $"{Stamp()} {Path.GetFileName(f.Path)}"));
                    File.Move(f.Path, backup);
                    return null;

                case TaskTarget t:
                    if (t.Xml.Length > 0)
                    {
                        Directory.CreateDirectory(BackupFolder);
                        backup = Unique(Path.Combine(BackupFolder, $"{Stamp()} {Safe(name)}.xml"));
                        File.WriteAllText(backup, t.Xml, Encoding.Unicode);
                    }

                    return TaskScheduler.Delete(t.Path);

                case WmiTarget w:
                    return WmiSubscriptions.Delete(w);

                default:
                    return "This can't be removed from here.";
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return NeedsAdmin;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine($"Autostart removal failed: {ex}");
            return "Windows couldn't remove it.";
        }
    }

    /// <summary>Opens Registry Editor at the key, through the LastKey it reopens at.</summary>
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

    // ---------- Switches ----------

    /// <summary>Task Manager's switch: 02 and twelve zeros for on; 03 and the time it was turned off for off.</summary>
    private static string? Approve(ApprovedSwitch s, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(s.Hive, RegistryView.Registry64);
        using var key = root.CreateSubKey($@"{ApprovedRoot}\{s.List}", writable: true);
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
            BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        key.SetValue(s.ValueName, data, RegistryValueKind.Binary);
        return null;
    }

    private static string? SetPackagedTask(PackagedTaskSwitch s, bool enabled)
    {
        using var key = AutostartRegistry.Open(RegistryHive.CurrentUser, s.KeyPath, writable: true);
        if (key is null)
            return "The app's startup task is no longer there.";

        // 2 is on; 1 is "turned off by the user", which the app may not turn back on by itself.
        key.SetValue("State", enabled ? 2 : 1, RegistryValueKind.DWord);
        return null;
    }

    private static string? SetOfficeAddin(OfficeAddinSwitch o, bool enabled)
    {
        using var key = AutostartRegistry.Open(o.Hive, o.Key, writable: true);
        if (key is null)
            return "The add-in is no longer registered.";

        var current = AutostartRegistry.Number(key, "LoadBehavior") ?? 0;
        if (enabled)
        {
            var was = AutostartRegistry.Number(key, OfficeSources.RememberedValue) ?? 3;
            key.SetValue("LoadBehavior", was, RegistryValueKind.DWord);
            key.DeleteValue(OfficeSources.RememberedValue, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(OfficeSources.RememberedValue, current, RegistryValueKind.DWord);
            key.SetValue("LoadBehavior", 0, RegistryValueKind.DWord);
        }

        return null;
    }

    private static string? MoveValue(RegistryHive hive, string from, string to, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var source = root.OpenSubKey(from, writable: true);
        if (source?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
            return "It's no longer there.";

        var kind = source.GetValueKind(name);
        using (var target = root.CreateSubKey(to, writable: true))
            target.SetValue(name, value, kind);
        source.DeleteValue(name);
        DropEmptyDisabledKey(hive, from);
        return null;
    }

    private static string? MoveKey(RegistryHive hive, string fromParent, string toParent, string name)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using (var source = root.OpenSubKey($@"{fromParent}\{name}"))
        {
            if (source is null)
                return "It's no longer there.";

            using var target = root.CreateSubKey($@"{toParent}\{name}", writable: true);
            CopyTree(source, target);
        }

        root.DeleteSubKeyTree($@"{fromParent}\{name}");
        DropEmptyDisabledKey(hive, fromParent);
        return null;
    }

    private static void CopyTree(RegistryKey source, RegistryKey target)
    {
        foreach (var name in source.GetValueNames())
            target.SetValue(name, source.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, source.GetValueKind(name));

        foreach (var name in source.GetSubKeyNames())
        {
            using var child = source.OpenSubKey(name);
            using var copy = target.CreateSubKey(name, writable: true);
            if (child is not null)
                CopyTree(child, copy);
        }
    }

    /// <summary>The "Sockless disabled" key goes once nothing waits in it.</summary>
    private static void DropEmptyDisabledKey(RegistryHive hive, string path)
    {
        if (!path.EndsWith('\\' + AutostartRegistry.DisabledKey, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using (var key = root.OpenSubKey(path))
            {
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
                    return;
            }

            root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
        }
    }

    private static string? RemoveListItem(ListItemTarget l)
    {
        using var key = AutostartRegistry.Open(l.Hive, l.Key, writable: true);
        if (key?.GetValue(l.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
            return "It's no longer there.";

        var kind = key.GetValueKind(l.ValueName);
        static bool Same(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
        switch (value)
        {
            case string[] lines:
                key.SetValue(l.ValueName, lines.Where(line => !Same(line, l.Item)).ToArray(), RegistryValueKind.MultiString);
                return null;
            case string text:
                var kept = text.Split(l.Separator).Where(part => part.Trim().Length > 0 && !Same(part, l.Item));
                var joined = string.Join(l.Separator == ',' ? "," : l.Separator.ToString(), kept);
                // Userinit keeps its trailing comma, as Windows writes it.
                if (l.Separator == ',' && text.TrimEnd().EndsWith(',') && joined.Length > 0)
                    joined += ",";
                key.SetValue(l.ValueName, joined, kind);
                return null;
            default:
                return "Windows couldn't change that list.";
        }
    }

    // ---------- Services ----------

    private const uint ScManagerConnect = 0x1;
    private const uint ServiceChangeConfig = 0x2;
    private const uint NoChange = 0xFFFFFFFF;
    private const uint DelayedAutoStartInfo = 3;

    private static unsafe string? SetService(ServiceTarget s, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (!enabled)
        {
            // Remember the startup type first: it's what turning it back on restores.
            using var remembered = root.CreateSubKey(ServiceSources.RememberedPath, writable: true);
            remembered.SetValue(s.Name, s.Start | (s.Delayed ? 1 << 8 : 0), RegistryValueKind.DWord);
        }

        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0)
            return ServiceFailure(Marshal.GetLastPInvokeError());

        try
        {
            var service = OpenServiceW(manager, s.Name, ServiceChangeConfig);
            if (service == 0)
                return ServiceFailure(Marshal.GetLastPInvokeError());

            try
            {
                var start = enabled ? (uint)s.Start : ServiceSources.Disabled;
                if (!ChangeServiceConfigW(service, NoChange, start, NoChange, null, null, 0, null, null, null, null))
                    return ServiceFailure(Marshal.GetLastPInvokeError());

                if (enabled && s.Start == ServiceSources.Automatic && s.Delayed)
                {
                    var delayed = 1;
                    ChangeServiceConfig2W(service, DelayedAutoStartInfo, &delayed);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        if (enabled)
        {
            using var remembered = root.OpenSubKey(ServiceSources.RememberedPath, writable: true);
            remembered?.DeleteValue(s.Name, throwOnMissingValue: false);
        }

        return null;
    }

    private static string ServiceFailure(int error) => error switch
    {
        5 => NeedsAdmin,
        1060 => "The service is no longer installed.",
        1072 => "The service is being uninstalled.",
        _ => $"Windows couldn't change the service (error {error}).",
    };

    // ---------- Backups ----------

    private static string? SaveReg(string name, string body)
    {
        Directory.CreateDirectory(BackupFolder);
        var path = Unique(Path.Combine(BackupFolder, $"{Stamp()} {Safe(name)}.reg"));
        File.WriteAllText(path, "Windows Registry Editor Version 5.00\r\n\r\n" + body, Encoding.Unicode);
        return path;
    }

    private static string ExportValue(RegistryHive hive, string path, string name)
    {
        using var key = AutostartRegistry.Open(hive, path) ?? throw new IOException("It's no longer there.");
        return $"[{AutostartRegistry.LongHiveName(hive)}\\{path}]\r\n{Line(key, name)}\r\n";
    }

    private static string ExportKey(RegistryHive hive, string path)
    {
        using var key = AutostartRegistry.Open(hive, path) ?? throw new IOException("It's no longer there.");
        var text = new StringBuilder();
        Export(key, $@"{AutostartRegistry.LongHiveName(hive)}\{path}", text);
        return text.ToString();
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

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint manager, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(nint service, uint type, uint start, uint errorControl, string? binaryPath,
        string? loadOrderGroup, nint tagId, string? dependencies, string? account, string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ChangeServiceConfig2W(nint service, uint level, void* info);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
