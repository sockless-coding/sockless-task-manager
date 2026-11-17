using System.Runtime.InteropServices;
using Microsoft.Win32;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>A kernel or file system driver as registered under the Services key.</summary>
/// <param name="Image">The .sys file, made an ordinary path.</param>
/// <param name="Start">0 boot, 1 system, 2 automatic, 3 on demand, 4 disabled.</param>
internal sealed record DriverService(string Name, string? DisplayName, string? Description, string? Image, int Start, bool IsFileSystem, string? Group);

/// <summary>
/// Driver services (types 1 and 2) from HKLM\SYSTEM\CurrentControlSet\Services, read straight from
/// the registry rather than through the service manager: every user can read it, and it's quick.
/// This is how a loaded module and a device find their driver's name and file.
/// </summary>
internal static partial class DriverServices
{
    private const int KernelDriver = 1;
    private const int FileSystemDriver = 2;

    public static Dictionary<string, DriverService> Read()
    {
        var services = new Dictionary<string, DriverService>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root is null)
            return services;

        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue("Type") is not int type || (type & (KernelDriver | FileSystemDriver)) == 0 || type > 0xF)
                    continue;

                var imagePath = key.GetValue("ImagePath") as string;
                var image = string.IsNullOrWhiteSpace(imagePath)
                    ? Path.Combine(Environment.SystemDirectory, "drivers", name + ".sys")
                    : CommandLine.ProgramPath(imagePath);

                services[name] = new DriverService(
                    name,
                    Resource(key.GetValue("DisplayName") as string),
                    Resource(key.GetValue("Description") as string),
                    image.Length > 0 ? image : null,
                    key.GetValue("Start") is int start ? start : 3,
                    (type & FileSystemDriver) != 0,
                    key.GetValue("Group") as string);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A key this user can't read: leave it out.
            }
        }

        return services;
    }

    /// <summary>
    /// "@%SystemRoot%\system32\drivers\x.sys,-101" as the text it names. Driver packages write
    /// "@oem12.inf,%Name%;Fallback", which only the fallback after the semicolon makes readable.
    /// </summary>
    private static unsafe string? Resource(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!text.StartsWith('@'))
            return text.Trim();

        var buffer = stackalloc char[1024];
        if (SHLoadIndirectString(text, buffer, 1024, 0) >= 0 && new string(buffer).Trim() is { Length: > 0 } loaded)
            return loaded;

        var fallback = text.LastIndexOf(';');
        return fallback > 0 && fallback < text.Length - 1 ? text[(fallback + 1)..].Trim() : null;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int SHLoadIndirectString(string source, char* output, uint size, nint reserved);
}
