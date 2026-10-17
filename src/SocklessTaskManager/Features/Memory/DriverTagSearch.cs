using System.Buffers.Binary;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>
/// Finds which loaded drivers could own a pool tag by looking for the tag's four bytes in
/// their code, as <c>poolmon -g</c> does. Drivers pass the tag as a constant, so only the
/// executable sections are searched; that keeps a tag such as <c>File</c> from matching every
/// driver that merely contains the word. Reads every driver image, so it only runs on
/// request, off the sampling thread.
/// </summary>
internal static class DriverTagSearch
{
    private const int ChunkSize = 1024 * 1024;
    private const uint CodeSection = 0x00000020;
    private const uint ExecutableSection = 0x20000000;

    public static List<KernelModule> Find(uint tag)
    {
        var buffer = NtSystem.Buffer(128 * 1024);
        var modules = KernelModules.Read(ref buffer);
        var chunk = new byte[ChunkSize + 3];
        var found = new List<KernelModule>();

        // Allocations may carry the "protected" high bit in the tag constant.
        Span<byte> plain = stackalloc byte[4];
        Span<byte> protectedTag = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(plain, tag & 0x7FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(protectedTag, tag | 0x80000000);

        foreach (var module in modules.DistinctBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(module.Path) && CodeContains(module.Path, chunk, plain, protectedTag))
                    found.Add(module);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return found;
    }

    private static bool CodeContains(string path, byte[] chunk, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess);
        foreach (var (start, length) in CodeSections(file, chunk))
        {
            file.Position = start;
            var remaining = length;

            // Keep the last three bytes of each chunk so a tag split across chunks still matches.
            var carried = 0;
            while (remaining > 0)
            {
                var read = file.Read(chunk, carried, (int)Math.Min(ChunkSize, remaining));
                if (read == 0)
                    break;
                remaining -= read;

                var span = chunk.AsSpan(0, carried + read);
                if (span.IndexOf(first) >= 0 || span.IndexOf(second) >= 0)
                    return true;

                carried = Math.Min(3, span.Length);
                span[^carried..].CopyTo(chunk);
            }
        }

        return false;
    }

    /// <summary>File ranges of the PE image's code sections (.text, PAGE, INIT…).</summary>
    private static List<(long Start, long Length)> CodeSections(FileStream file, byte[] scratch)
    {
        var sections = new List<(long, long)>();
        var headers = scratch.AsSpan(0, file.Read(scratch, 0, 4096));
        if (headers.Length < 0x40 || headers[0] != 'M' || headers[1] != 'Z')
            return sections;

        var pe = BinaryPrimitives.ReadInt32LittleEndian(headers[0x3C..]);
        if (pe <= 0 || pe + 24 > headers.Length || BinaryPrimitives.ReadUInt32LittleEndian(headers[pe..]) != 0x00004550)
            return sections;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(headers[(pe + 6)..]);
        var optionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(headers[(pe + 20)..]);
        var table = pe + 24 + optionalHeader;
        for (var i = 0; i < count && table + (i + 1) * 40 <= headers.Length; i++)
        {
            var section = headers.Slice(table + i * 40, 40);
            var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(section[36..]);
            if ((characteristics & (CodeSection | ExecutableSection)) == 0)
                continue;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);
            if (size > 0 && offset + (long)size <= file.Length)
                sections.Add((offset, size));
        }

        return sections;
    }
}
