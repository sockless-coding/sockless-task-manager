using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Performance.Cpu;

/// <summary>Static processor facts: name, base speed, topology and caches.</summary>
internal sealed partial record CpuInfo(
    string Name,
    double BaseMhz,
    int Sockets,
    int Cores,
    int LogicalProcessors,
    long L1Bytes,
    long L2Bytes,
    long L3Bytes,
    bool VirtualizationEnabled)
{
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;
    private const uint PfVirtFirmwareEnabled = 21;

    public static CpuInfo Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processor";
        var baseMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0;

        var (sockets, cores, l1, l2, l3) = ReadTopology();

        return new CpuInfo(
            name,
            baseMhz,
            sockets,
            cores,
            Environment.ProcessorCount,
            l1,
            l2,
            l3,
            IsProcessorFeaturePresent(PfVirtFirmwareEnabled));
    }

    /// <summary>Parses SYSTEM_LOGICAL_PROCESSOR_INFORMATION (32 bytes per entry on 64-bit).</summary>
    private static unsafe (int Sockets, int Cores, long L1, long L2, long L3) ReadTopology()
    {
        uint length = 0;
        GetLogicalProcessorInformation(null, ref length);
        if (length == 0)
            return (0, 0, 0, 0, 0);

        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (!GetLogicalProcessorInformation(p, ref length))
                return (0, 0, 0, 0, 0);
        }

        int sockets = 0, cores = 0;
        long l1 = 0, l2 = 0, l3 = 0;
        var entrySize = 2 * IntPtr.Size + 16;
        for (var offset = 0; offset + entrySize <= length; offset += entrySize)
        {
            var relationship = BitConverter.ToInt32(buffer, offset + IntPtr.Size);
            var union = offset + 2 * IntPtr.Size;
            switch (relationship)
            {
                case RelationProcessorCore:
                    cores++;
                    break;
                case RelationProcessorPackage:
                    sockets++;
                    break;
                case RelationCache:
                    var level = buffer[union];
                    var size = BitConverter.ToUInt32(buffer, union + 4);
                    if (level == 1) l1 += size;
                    else if (level == 2) l2 += size;
                    else if (level == 3) l3 += size;
                    break;
            }
        }

        return (sockets, cores, l1, l2, l3);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetLogicalProcessorInformation(byte* buffer, ref uint returnedLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessorFeaturePresent(uint feature);
}
