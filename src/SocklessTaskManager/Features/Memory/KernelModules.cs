using System.Buffers.Binary;
using System.Text;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>A loaded driver (or the kernel itself) and the size of its image.</summary>
internal readonly record struct KernelModule(string Name, string Path, ulong ImageBytes);

/// <summary>
/// Loaded kernel modules from <c>SystemModuleInformation</c>. Without elevation the base
/// addresses are zeroed, but names, paths and image sizes are all there.
/// </summary>
internal static class KernelModules
{
    // RTL_PROCESS_MODULE_INFORMATION (x64), after an 8-byte count.
    private const int EntrySize = 0x128;
    private const int ImageSize = 0x18;
    private const int OffsetToFileName = 0x26;
    private const int FullPathName = 0x28;
    private const int PathLength = 256;

    public static List<KernelModule> Read(ref byte[] buffer)
    {
        var modules = new List<KernelModule>();
        var length = NtSystem.Query(NtSystem.ModuleInformation, ref buffer);
        if (length < 8)
            return modules;

        var all = buffer.AsSpan(0, length);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(all);
        for (var i = 0; i < count && 8 + (i + 1) * EntrySize <= length; i++)
        {
            var e = all.Slice(8 + i * EntrySize, EntrySize);
            var path = e.Slice(FullPathName, PathLength);
            var end = path.IndexOf((byte)0);
            if (end >= 0)
                path = path[..end];

            var nameStart = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(e[OffsetToFileName..]), path.Length);
            modules.Add(new KernelModule(
                Encoding.Latin1.GetString(path[nameStart..]),
                ToWin32Path(Encoding.Latin1.GetString(path)),
                BinaryPrimitives.ReadUInt32LittleEndian(e[ImageSize..])));
        }

        return modules;
    }

    /// <summary>"\SystemRoot\system32\drivers\x.sys" and "\??\C:\…" to paths the file APIs accept.</summary>
    private static string ToWin32Path(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.Join(windows, path[12..]);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];
        if (path.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.Join(windows, path[9..]);
        if (!path.StartsWith('\\') && !System.IO.Path.IsPathRooted(path))
            return System.IO.Path.Join(windows, path);
        return path;
    }
}
