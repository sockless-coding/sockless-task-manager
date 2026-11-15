using System.Buffers.Binary;
using System.Text;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>
/// Reads a shortcut (.lnk, MS-SHLLINK) by offset: where it points and its arguments. IShellLink
/// would need built-in COM interop, which Native AOT doesn't have. Shortcuts that point at a shell
/// item rather than a file (a packaged app, a Control Panel page) have no target path.
/// </summary>
internal static class ShellLink
{
    private const uint HasTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint EnvironmentBlock = 0xA0000001;

    /// <summary>Null when the file isn't a shortcut, or can't be read.</summary>
    public static (string? Target, string Arguments)? Read(string path)
    {
        byte[] data;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is < 76 or > 1 << 20)
                return null;
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return Parse(data, Path.GetDirectoryName(path) ?? string.Empty);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (string? Target, string Arguments)? Parse(ReadOnlySpan<byte> data, string folder)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x4C)
            return null;

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        var unicode = (flags & IsUnicode) != 0;
        var at = 76;

        if ((flags & HasTargetIdList) != 0)
            at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);

        string? linkPath = null;
        if ((flags & HasLinkInfo) != 0)
        {
            var info = data[at..];
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(info);
            var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
            var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
            if ((infoFlags & 1) != 0)
            {
                string? basePath = null, suffix = null;
                if (headerSize >= 0x24)
                {
                    var baseUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[28..]);
                    var suffixUnicode = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[32..]);
                    if (baseUnicode > 0)
                        basePath = Utf16Z(info[baseUnicode..size]);
                    if (suffixUnicode > 0)
                        suffix = Utf16Z(info[suffixUnicode..size]);
                }

                if (basePath is null)
                {
                    var baseOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
                    var suffixOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(info[24..]);
                    basePath = AnsiZ(info[baseOffset..size]);
                    suffix = suffixOffset > 0 ? AnsiZ(info[suffixOffset..size]) : null;
                }

                linkPath = basePath + suffix;
            }

            at += size;
        }

        string? relative = null, arguments = null;
        foreach (var (bit, keep) in (ReadOnlySpan<(uint, int)>)[(HasName, 0), (HasRelativePath, 1), (HasWorkingDir, 0), (HasArguments, 2), (HasIconLocation, 0)])
        {
            if ((flags & bit) == 0)
                continue;

            var count = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            at += 2;
            var bytes = unicode ? count * 2 : count;
            var text = unicode ? Encoding.Unicode.GetString(data.Slice(at, bytes)) : Encoding.Latin1.GetString(data.Slice(at, bytes));
            at += bytes;
            if (keep == 1)
                relative = text;
            else if (keep == 2)
                arguments = text;
        }

        // Extra data: a target with environment variables (%ProgramFiles%…) is the most portable form.
        string? expandable = null;
        while (at + 8 <= data.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            if (size < 8 || at + size > data.Length)
                break;

            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]) == EnvironmentBlock && size >= 0x314)
            {
                expandable = Utf16Z(data.Slice(at + 8 + 260, 520));
                if (expandable.Length == 0)
                    expandable = AnsiZ(data.Slice(at + 8, 260));
            }

            at += size;
        }

        var target = expandable is { Length: > 0 } ? Environment.ExpandEnvironmentVariables(expandable)
            : linkPath is { Length: > 0 } ? linkPath
            : relative is { Length: > 0 } ? Path.GetFullPath(Path.Join(folder, relative))
            : null;
        return (target, arguments ?? string.Empty);
    }

    private static string Utf16Z(ReadOnlySpan<byte> bytes)
    {
        var chars = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(bytes[..(bytes.Length & ~1)]);
        var end = chars.IndexOf('\0');
        return new string(end >= 0 ? chars[..end] : chars);
    }

    private static string AnsiZ(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end >= 0 ? bytes[..end] : bytes);
    }
}
