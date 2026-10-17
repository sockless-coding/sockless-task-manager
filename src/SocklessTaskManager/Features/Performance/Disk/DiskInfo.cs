using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sockless.TaskManager.Features.Performance.Disk;

/// <summary>
/// Static facts about a physical disk, read with storage IOCTLs that need no admin rights
/// (the handle is opened with zero access).
/// </summary>
internal sealed partial record DiskInfo(string Model, long SizeBytes, bool? IsSsd, string Bus, bool Removable)
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    public string TypeLabel
    {
        get
        {
            var kind = Removable ? "Removable" : IsSsd switch { true => "SSD", false => "HDD", null => "Disk" };
            return Bus.Length > 0 ? $"{kind} ({Bus})" : kind;
        }
    }

    public static DiskInfo Read(int index)
    {
        using var handle = CreateFile($@"\\.\PhysicalDrive{index}", 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
            return new DiskInfo($"Disk {index}", 0, null, string.Empty, false);

        var (model, bus, removable) = ReadDescriptor(handle);
        return new DiskInfo(
            model.Length > 0 ? model : $"Disk {index}",
            ReadSize(handle),
            ReadIncursSeekPenalty(handle) is { } penalty ? !penalty : null,
            bus,
            removable);
    }

    private static unsafe (string Model, string Bus, bool Removable) ReadDescriptor(SafeFileHandle handle)
    {
        var query = stackalloc int[3] { StorageDeviceProperty, 0, 0 };
        var buffer = new byte[1024];
        fixed (byte* p = buffer)
        {
            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, p, (uint)buffer.Length, out _, 0))
                return (string.Empty, string.Empty, false);
        }

        // STORAGE_DEVICE_DESCRIPTOR: RemovableMedia @10, VendorIdOffset @12, ProductIdOffset @16, BusType @28.
        var removable = buffer[10] != 0;
        var vendor = ReadAnsi(buffer, BitConverter.ToInt32(buffer, 12));
        var product = ReadAnsi(buffer, BitConverter.ToInt32(buffer, 16));
        var model = vendor.Length > 0 && !product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
            ? $"{vendor} {product}"
            : product;

        var bus = BitConverter.ToInt32(buffer, 28) switch
        {
            1 => "SCSI",
            3 => "ATA",
            7 => "USB",
            8 => "RAID",
            10 => "SAS",
            11 => "SATA",
            12 => "SD",
            14 or 15 => "Virtual",
            17 => "NVMe",
            _ => string.Empty,
        };

        return (model, bus, removable);
    }

    private static unsafe bool? ReadIncursSeekPenalty(SafeFileHandle handle)
    {
        var query = stackalloc int[3] { StorageDeviceSeekPenaltyProperty, 0, 0 };
        var result = stackalloc byte[12];
        // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, IncursSeekPenalty @8.
        return DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, result, 12, out _, 0)
            ? result[8] != 0
            : null;
    }

    private static unsafe long ReadSize(SafeFileHandle handle)
    {
        var geometry = stackalloc byte[256];
        // DISK_GEOMETRY_EX: a 24-byte DISK_GEOMETRY, then DiskSize.
        return DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, geometry, 256, out _, 0)
            ? *(long*)(geometry + 24)
            : 0;
    }

    private static string ReadAnsi(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
            return string.Empty;

        var end = Array.IndexOf(buffer, (byte)0, offset);
        return Encoding.ASCII.GetString(buffer, offset, (end < 0 ? buffer.Length : end) - offset).Trim();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inSize, void* outBuffer, uint outSize, out uint returned, nint overlapped);
}
