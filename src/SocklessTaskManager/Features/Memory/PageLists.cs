using System.Buffers.Binary;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>
/// The physical page lists (<c>SYSTEM_MEMORY_LIST_INFORMATION</c>): what isn't actively in use
/// is free, zeroed, on standby (cached, by priority) or modified (dirty, waiting to be written).
/// </summary>
internal sealed record PageLists(
    ulong ZeroedBytes,
    ulong FreeBytes,
    ulong ModifiedBytes,
    ulong ModifiedNoWriteBytes,
    ulong ModifiedForPageFileBytes,
    ulong BadBytes,
    ulong[] StandbyByPriority)
{
    private const int Size = 0xB0;

    public static readonly PageLists Empty = new(0, 0, 0, 0, 0, 0, new ulong[8]);

    public ulong StandbyBytes
    {
        get
        {
            ulong sum = 0;
            foreach (var bytes in StandbyByPriority)
                sum += bytes;
            return sum;
        }
    }

    /// <summary>Free and zeroed, as Task Manager counts "Free".</summary>
    public ulong UnusedBytes => ZeroedBytes + FreeBytes;

    public ulong AllModifiedBytes => ModifiedBytes + ModifiedNoWriteBytes;

    public static PageLists Read(ref byte[] buffer)
    {
        var length = NtSystem.Query(NtSystem.MemoryListInformation, ref buffer);
        if (length < Size)
            return Empty;

        var lists = buffer;
        var page = (ulong)Environment.SystemPageSize;
        ulong Pages(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(lists.AsSpan(offset, 8)) * page;

        var standby = new ulong[8];
        for (var i = 0; i < standby.Length; i++)
            standby[i] = Pages(0x28 + i * 8);

        return new PageLists(
            ZeroedBytes: Pages(0x00),
            FreeBytes: Pages(0x08),
            ModifiedBytes: Pages(0x10),
            ModifiedNoWriteBytes: Pages(0x18),
            ModifiedForPageFileBytes: Pages(0xA8),
            BadBytes: Pages(0x20),
            StandbyByPriority: standby);
    }
}
