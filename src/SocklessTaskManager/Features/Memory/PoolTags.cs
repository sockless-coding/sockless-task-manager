using System.Buffers.Binary;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>
/// Kernel pool usage by tag, as poolmon shows it. Drivers label each allocation with a
/// four-character tag, so this is how pool memory is traced back to a driver.
/// </summary>
/// <param name="Value">The tag as the kernel stores it; these are the bytes to look for in driver files.</param>
internal readonly record struct PoolTag(
    string Tag,
    uint Value,
    ulong PagedBytes,
    ulong NonPagedBytes,
    long PagedAllocations,
    long NonPagedAllocations);

internal sealed class PoolTags
{
    // SYSTEM_POOLTAG (x64): tag, paged allocs/frees, paged used, non-paged allocs/frees, non-paged used.
    private const int EntrySize = 0x28;

    private readonly Dictionary<uint, string> _names = [];
    private byte[] _buffer = NtSystem.Buffer(256 * 1024);

    /// <summary>Sampling thread only.</summary>
    public List<PoolTag> Read()
    {
        var tags = new List<PoolTag>();
        var length = NtSystem.Query(NtSystem.PoolTagInformation, ref _buffer);
        if (length < 8)
            return tags;

        var all = _buffer.AsSpan(0, length);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(all);
        for (var i = 0; i < count && 8 + (i + 1) * EntrySize <= length; i++)
        {
            var e = all.Slice(8 + i * EntrySize, EntrySize);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(e);
            tags.Add(new PoolTag(
                Name(value),
                value,
                BinaryPrimitives.ReadUInt64LittleEndian(e[0x10..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[0x20..]),
                (long)BinaryPrimitives.ReadUInt32LittleEndian(e[0x04..]) - BinaryPrimitives.ReadUInt32LittleEndian(e[0x08..]),
                (long)BinaryPrimitives.ReadUInt32LittleEndian(e[0x18..]) - BinaryPrimitives.ReadUInt32LittleEndian(e[0x1C..])));
        }

        return tags;
    }

    private string Name(uint value) =>
        _names.TryGetValue(value, out var name) ? name : _names[value] = Decode(value);

    /// <summary>The four tag bytes as text; the protected-allocation high bit is not part of the name.</summary>
    public static string Decode(uint value)
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            var b = (byte)(value >> (i * 8));
            if (i == 3)
                b &= 0x7F;
            chars[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }

        return new string(chars).TrimEnd();
    }
}
