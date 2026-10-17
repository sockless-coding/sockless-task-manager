using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Memory;

/// <param name="PrivateWorkingSet">RAM only this process uses; what Task Manager shows as "Memory".</param>
/// <param name="WorkingSet">Everything the process has in RAM, including pages shared with others.</param>
/// <param name="Commit">Private bytes the process has committed, in RAM or in the page file.</param>
internal readonly record struct ProcessUsage(int Id, string Name, ulong PrivateWorkingSet, ulong WorkingSet, ulong Commit);

/// <summary>
/// Every process's memory counters from one <c>SystemProcessInformation</c> call. Unlike
/// opening each process, this sees protected and elevated processes too.
/// </summary>
internal static class ProcessMemory
{
    /// <summary>The kernel process that holds compressed pages. Its working set is the compression store.</summary>
    public const string CompressionName = "Memory Compression";

    // SYSTEM_PROCESS_INFORMATION (x64).
    private const int NextEntryOffset = 0x00;
    private const int WorkingSetPrivateSize = 0x08;
    private const int ImageNameLength = 0x38;
    private const int ImageNameBuffer = 0x40;
    private const int UniqueProcessId = 0x50;
    private const int WorkingSetSize = 0x90;
    private const int PagefileUsage = 0xB8;
    private const int MinimumEntry = 0xC0;

    public static List<ProcessUsage> Read(ref byte[] buffer)
    {
        var processes = new List<ProcessUsage>(400);
        var length = NtSystem.Query(NtSystem.ProcessInformation, ref buffer);
        var all = buffer.AsSpan(0, length);

        var offset = 0;
        while (length > 0 && offset + MinimumEntry <= length)
        {
            var e = all[offset..];
            var id = (int)BinaryPrimitives.ReadInt64LittleEndian(e[UniqueProcessId..]);

            // The idle process (pid 0) has no name and no memory of its own.
            if (id != 0)
            {
                processes.Add(new ProcessUsage(
                    id,
                    ReadName(buffer, length, e),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[WorkingSetPrivateSize..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[WorkingSetSize..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[PagefileUsage..])));
            }

            var next = BinaryPrimitives.ReadInt32LittleEndian(e[NextEntryOffset..]);
            if (next <= 0)
                break;
            offset += next;
        }

        return processes;
    }

    private static string ReadName(byte[] buffer, int length, ReadOnlySpan<byte> entry)
    {
        var bytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[ImageNameLength..]);
        var pointer = (nint)BinaryPrimitives.ReadInt64LittleEndian(entry[ImageNameBuffer..]);
        var start = NtSystem.OffsetOf(buffer, length, pointer);
        if (bytes == 0 || start < 0 || start + bytes > length)
            return "Unknown";

        return new string(MemoryMarshal.Cast<byte, char>(buffer.AsSpan(start, bytes)));
    }
}
