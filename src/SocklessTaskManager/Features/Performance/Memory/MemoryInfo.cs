using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Performance.Memory;

/// <summary>
/// Static memory facts. Installed size comes from firmware; speed, slots, form factor and type
/// come from the SMBIOS "Memory Device" (type 17) records, read without WMI.
/// </summary>
internal sealed partial record MemoryInfo(
    ulong InstalledBytes,
    int SpeedMts,
    int SlotsUsed,
    int SlotsTotal,
    string FormFactor,
    string Type)
{
    private const uint Rsmb = 0x52534D42; // 'RSMB'

    public static MemoryInfo Read()
    {
        GetPhysicallyInstalledSystemMemory(out var installedKb);

        int speed = 0, used = 0, total = 0;
        string formFactor = string.Empty, type = string.Empty;

        foreach (var device in ReadMemoryDevices())
        {
            total++;
            if (!device.Populated)
                continue;

            used++;
            speed = Math.Max(speed, device.SpeedMts);
            formFactor = device.FormFactor;
            type = device.Type;
        }

        return new MemoryInfo(installedKb * 1024, speed, used, total, formFactor, type);
    }

    private readonly record struct MemoryDevice(bool Populated, int SpeedMts, string FormFactor, string Type);

    private static unsafe List<MemoryDevice> ReadMemoryDevices()
    {
        var devices = new List<MemoryDevice>();
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0)
            return devices;

        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (GetSystemFirmwareTable(Rsmb, 0, p, size) != size)
                return devices;
        }

        // RawSMBIOSData: 4 version bytes, a 4-byte length, then the structure table.
        var table = buffer.AsSpan(8, (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)), size - 8));
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var structType = table[offset];
            var length = table[offset + 1];
            if (length < 4 || offset + length > table.Length)
                break;

            if (structType == 17 && length >= 0x17)
                devices.Add(ParseMemoryDevice(table.Slice(offset, length)));

            if (structType == 127)
                break;

            // Skip the formatted area, then the string set, which ends with a double null.
            var next = offset + length;
            while (next + 1 < table.Length && (table[next] != 0 || table[next + 1] != 0))
                next++;
            offset = next + 2;
        }

        return devices;
    }

    private static MemoryDevice ParseMemoryDevice(ReadOnlySpan<byte> s)
    {
        var sizeField = BinaryPrimitives.ReadUInt16LittleEndian(s[0x0C..]);
        var populated = sizeField is not 0 and not 0xFFFF;

        var speed = BinaryPrimitives.ReadUInt16LittleEndian(s[0x15..]);
        if (s.Length >= 0x22)
        {
            var configured = BinaryPrimitives.ReadUInt16LittleEndian(s[0x20..]);
            if (configured is not 0 and not 0xFFFF)
                speed = configured;
        }

        var formFactor = s[0x0E] switch
        {
            0x09 => "DIMM",
            0x0D => "SODIMM",
            0x0B => "Row of chips",
            0x0F => "FB-DIMM",
            _ => "Other",
        };

        var type = s[0x12] switch
        {
            0x18 => "DDR3",
            0x1A => "DDR4",
            0x1B => "LPDDR",
            0x1C => "LPDDR2",
            0x1D => "LPDDR3",
            0x1E => "LPDDR4",
            0x22 => "DDR5",
            0x23 => "LPDDR5",
            _ => string.Empty,
        };

        return new MemoryDevice(populated, speed == 0xFFFF ? 0 : speed, formFactor, type);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint bufferSize);
}
