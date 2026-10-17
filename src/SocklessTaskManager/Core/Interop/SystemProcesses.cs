using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>One process as <c>SystemProcessInformation</c> reports it. Times are 100 ns ticks.</summary>
/// <param name="ParentId">The creator's id. The creator may have exited and its id been reused: compare <see cref="CreateTime"/>.</param>
/// <param name="CreateTime">FILETIME (UTC). With <see cref="Id"/> it names one process for its whole life.</param>
/// <param name="CpuTime">User plus kernel time used so far.</param>
/// <param name="PrivateWorkingSet">RAM only this process uses; what Task Manager shows as "Memory".</param>
/// <param name="WorkingSet">Everything the process has in RAM, including pages shared with others.</param>
/// <param name="Commit">Private bytes the process has committed, in RAM or in the page file.</param>
/// <param name="IoBytes">Bytes read, written and otherwise transferred (disk, network and device I/O) so far.</param>
/// <param name="BasePriority">The priority class as a thread priority: 4 idle, 6 below normal, 8 normal, 10 above normal, 13 high, 24 real-time.</param>
/// <param name="IsSuspended">Every thread is suspended: suspended by a debugger or a task manager, or a Store app in the background.</param>
internal readonly record struct SystemProcess(
    int Id,
    int ParentId,
    string Name,
    long CreateTime,
    long CpuTime,
    int Threads,
    int Handles,
    int SessionId,
    ulong PrivateWorkingSet,
    ulong WorkingSet,
    ulong Commit,
    ulong IoBytes,
    int BasePriority,
    bool IsSuspended);

/// <summary>
/// Every process from one <c>SystemProcessInformation</c> call. Unlike opening each process,
/// this sees protected and elevated processes too, and costs about a millisecond.
/// </summary>
internal static class SystemProcesses
{
    // SYSTEM_PROCESS_INFORMATION (x64).
    private const int NextEntryOffset = 0x00;
    private const int NumberOfThreads = 0x04;
    private const int WorkingSetPrivateSize = 0x08;
    private const int CreateTime = 0x20;
    private const int UserTime = 0x28;
    private const int KernelTime = 0x30;
    private const int ImageNameLength = 0x38;
    private const int ImageNameBuffer = 0x40;
    private const int BasePriority = 0x48;
    private const int UniqueProcessId = 0x50;
    private const int InheritedFromUniqueProcessId = 0x58;
    private const int HandleCount = 0x60;
    private const int SessionId = 0x64;
    private const int WorkingSetSize = 0x90;
    private const int PagefileUsage = 0xB8;
    private const int ReadTransferCount = 0xE8;
    private const int WriteTransferCount = 0xF0;
    private const int OtherTransferCount = 0xF8;
    private const int MinimumEntry = 0x100;

    // SYSTEM_THREAD_INFORMATION (x64), an array right after each process entry.
    private const int ThreadSize = 0x50;
    private const int ThreadState = 0x44;
    private const int ThreadWaitReason = 0x48;
    private const int StateWaiting = 5;
    private const int WaitReasonSuspended = 5;

    /// <summary>Reads every process but the idle process (pid 0), which has no name and no memory of its own.</summary>
    /// <param name="buffer">A buffer from <see cref="NtSystem.Buffer"/>; grown as needed.</param>
    public static List<SystemProcess> Read(ref byte[] buffer)
    {
        var processes = new List<SystemProcess>(400);
        var length = NtSystem.Query(NtSystem.ProcessInformation, ref buffer);
        var all = buffer.AsSpan(0, length);

        var offset = 0;
        while (length > 0 && offset + MinimumEntry <= length)
        {
            var e = all[offset..];
            var id = (int)BinaryPrimitives.ReadInt64LittleEndian(e[UniqueProcessId..]);
            var next = BinaryPrimitives.ReadInt32LittleEndian(e[NextEntryOffset..]);

            if (id != 0)
            {
                processes.Add(new SystemProcess(
                    id,
                    (int)BinaryPrimitives.ReadInt64LittleEndian(e[InheritedFromUniqueProcessId..]),
                    ReadName(buffer, length, e),
                    BinaryPrimitives.ReadInt64LittleEndian(e[CreateTime..]),
                    BinaryPrimitives.ReadInt64LittleEndian(e[UserTime..]) + BinaryPrimitives.ReadInt64LittleEndian(e[KernelTime..]),
                    BinaryPrimitives.ReadInt32LittleEndian(e[NumberOfThreads..]),
                    BinaryPrimitives.ReadInt32LittleEndian(e[HandleCount..]),
                    BinaryPrimitives.ReadInt32LittleEndian(e[SessionId..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[WorkingSetPrivateSize..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[WorkingSetSize..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[PagefileUsage..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(e[ReadTransferCount..])
                    + BinaryPrimitives.ReadUInt64LittleEndian(e[WriteTransferCount..])
                    + BinaryPrimitives.ReadUInt64LittleEndian(e[OtherTransferCount..]),
                    BinaryPrimitives.ReadInt32LittleEndian(e[BasePriority..]),
                    AllThreadsSuspended(next > 0 ? e[..Math.Min(next, e.Length)] : e)));
            }

            if (next <= 0)
                break;
            offset += next;
        }

        return processes;
    }

    private static bool AllThreadsSuspended(ReadOnlySpan<byte> entry)
    {
        var threads = BinaryPrimitives.ReadInt32LittleEndian(entry[NumberOfThreads..]);
        if (threads <= 0 || MinimumEntry + threads * ThreadSize > entry.Length)
            return false;

        for (var i = 0; i < threads; i++)
        {
            var thread = entry[(MinimumEntry + i * ThreadSize)..];
            if (BinaryPrimitives.ReadInt32LittleEndian(thread[ThreadState..]) != StateWaiting
                || BinaryPrimitives.ReadInt32LittleEndian(thread[ThreadWaitReason..]) != WaitReasonSuspended)
                return false;
        }

        return true;
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
