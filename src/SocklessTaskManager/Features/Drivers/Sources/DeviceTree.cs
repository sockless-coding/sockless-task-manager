using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>A device and the driver it runs on, as the configuration manager has it.</summary>
/// <param name="InstanceId">"PCI\VEN_10DE&amp;DEV_2684&amp;…\4&amp;…": stable across restarts.</param>
/// <param name="ClassTitle">Device Manager's group: "Display adapters".</param>
/// <param name="Inf">The installed INF's name in the driver store ("oem42.inf"); third-party drivers are oem*.inf.</param>
/// <param name="Service">The driver service that runs it, when it has one.</param>
/// <param name="Problem">Device Manager's problem code (the yellow mark); 0 when there's none.</param>
/// <param name="IsHidden">Device Manager hides it unless asked (software and system plumbing).</param>
internal sealed record DeviceInfo(
    string InstanceId,
    string Name,
    string ClassTitle,
    string? ClassGuid,
    string? Manufacturer,
    string? Provider,
    string? Version,
    DateTime? Date,
    string? Inf,
    string? InfSection,
    string? Service,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? Location,
    string? Parent,
    bool IsPresent,
    bool IsStarted,
    bool IsHidden,
    uint Problem,
    uint ProblemStatus,
    DateTime? FirstInstalled,
    DateTime? LastArrival,
    DateTime? LastRemoval)
{
    /// <summary>Installed from a package that didn't ship with Windows.</summary>
    public bool IsThirdParty => Inf is { } inf && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase);

    public bool HasDriver => Inf is not null || Service is not null;
}

/// <summary>
/// Every device Windows knows, connected or not, through CfgMgr32: one list call, then a few
/// property reads per device. A few hundred devices read in well under a second; it runs when the
/// page scans and after a device change, never per tick.
/// </summary>
internal static unsafe partial class DeviceTree
{
    private const uint CrSuccess = 0;
    private const uint CrBufferSmall = 0x1A;
    private const uint LocatePhantom = 0x1;

    private const uint TypeString = 0x12;
    private const uint TypeStringList = 0x2012;
    private const uint TypeFileTime = 0x10;
    private const uint TypeUInt32 = 0x7;

    private const uint NodeStarted = 0x8;
    private const uint NodeNoShowInDeviceManager = 0x40000000;

    private static readonly DevPropKey FriendlyName = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 14);
    private static readonly DevPropKey DeviceDesc = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 2);
    private static readonly DevPropKey HardwareIds = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 3);
    private static readonly DevPropKey CompatibleIds = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 4);
    private static readonly DevPropKey ServiceKey = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 6);
    private static readonly DevPropKey ClassName = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 9);
    private static readonly DevPropKey ClassGuidKey = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 10);
    private static readonly DevPropKey ManufacturerKey = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 13);
    private static readonly DevPropKey LocationInfo = Key(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0, 15);
    private static readonly DevPropKey ParentKey = Key(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x08, 0xa5, 0xa7, 8);
    private static readonly DevPropKey ProblemStatusKey = Key(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x08, 0xa5, 0xa7, 12);
    private static readonly DevPropKey BusReportedDesc = Key(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2, 4);
    private static readonly DevPropKey FirstInstallDate = Key(0x83da6326, 0x97a6, 0x4088, 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29, 101);
    private static readonly DevPropKey LastArrivalDate = Key(0x83da6326, 0x97a6, 0x4088, 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29, 102);
    private static readonly DevPropKey LastRemovalDate = Key(0x83da6326, 0x97a6, 0x4088, 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29, 103);
    private static readonly DevPropKey DriverDate = Key(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 2);
    private static readonly DevPropKey DriverVersion = Key(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 3);
    private static readonly DevPropKey DriverInfPath = Key(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 5);
    private static readonly DevPropKey DriverInfSection = Key(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 6);
    private static readonly DevPropKey DriverProvider = Key(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6, 9);
    private static readonly DevPropKey ClassTitleKey = Key(0x259abffc, 0x50a7, 0x47ce, 0xaf, 0x08, 0x68, 0xc9, 0xa7, 0xd7, 0x33, 0x66, 2);

    /// <summary>Every device, connected or not. Empty if the configuration manager can't be read.</summary>
    public static List<DeviceInfo> Read()
    {
        var devices = new List<DeviceInfo>(600);
        var classTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[1024];
        foreach (var id in InstanceIds())
        {
            if (ReadOne(id, classTitles, ref buffer) is { } device)
                devices.Add(device);
        }

        return devices;
    }

    /// <summary>One device, or null when Windows no longer knows it.</summary>
    public static DeviceInfo? Read(string instanceId)
    {
        var buffer = new byte[1024];
        return ReadOne(instanceId, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ref buffer);
    }

    private static List<string> InstanceIds()
    {
        var ids = new List<string>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            uint length;
            if (CM_Get_Device_ID_List_SizeW(&length, null, 0) != CrSuccess || length == 0)
                return ids;

            var list = new char[length];
            uint result;
            fixed (char* p = list)
                result = CM_Get_Device_ID_ListW(null, p, length, 0);

            // Devices arrive between the two calls: ask again.
            if (result == CrBufferSmall)
                continue;
            if (result != CrSuccess)
                return ids;

            foreach (var id in new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                ids.Add(id);
            return ids;
        }

        return ids;
    }

    private static DeviceInfo? ReadOne(string instanceId, Dictionary<string, string> classTitles, ref byte[] buffer)
    {
        uint node;
        fixed (char* id = instanceId)
        {
            if (CM_Locate_DevNodeW(&node, id, LocatePhantom) != CrSuccess)
                return null;
        }

        uint status = 0, problem = 0;
        var isPresent = CM_Get_DevNode_Status(&status, &problem, node, 0) == CrSuccess;
        if (!isPresent)
            status = problem = 0;

        var name = Text(node, FriendlyName, ref buffer) ?? Text(node, DeviceDesc, ref buffer) ?? Text(node, BusReportedDesc, ref buffer) ?? instanceId;
        var classGuid = ClassId(node, ClassGuidKey, ref buffer);
        var className = Text(node, ClassName, ref buffer);
        var classTitle = ClassTitle(classGuid, className, classTitles);

        return new DeviceInfo(
            instanceId,
            name,
            classTitle,
            classGuid,
            Text(node, ManufacturerKey, ref buffer),
            Text(node, DriverProvider, ref buffer),
            Text(node, DriverVersion, ref buffer),
            Time(node, DriverDate, ref buffer),
            Text(node, DriverInfPath, ref buffer),
            Text(node, DriverInfSection, ref buffer),
            Text(node, ServiceKey, ref buffer),
            TextList(node, HardwareIds, ref buffer),
            TextList(node, CompatibleIds, ref buffer),
            Text(node, LocationInfo, ref buffer),
            Text(node, ParentKey, ref buffer),
            isPresent,
            (status & NodeStarted) != 0,
            (status & NodeNoShowInDeviceManager) != 0,
            problem,
            isPresent && problem != 0 ? Number(node, ProblemStatusKey, ref buffer) ?? 0 : 0,
            Time(node, FirstInstallDate, ref buffer),
            Time(node, LastArrivalDate, ref buffer),
            Time(node, LastRemovalDate, ref buffer));
    }

    /// <summary>The class's display name ("Display adapters"), read once per class.</summary>
    private static string ClassTitle(string? classGuid, string? className, Dictionary<string, string> cache)
    {
        if (classGuid is null)
            return className ?? "Other devices";
        if (cache.TryGetValue(classGuid, out var known))
            return known;

        string? title = null;
        if (System.Guid.TryParse(classGuid, out var guid))
        {
            var key = ClassTitleKey;
            uint type, size = 512;
            var text = stackalloc char[256];
            if (CM_Get_Class_PropertyW(&guid, &key, &type, (byte*)text, &size, 0) == CrSuccess && type == TypeString && size >= 2)
                title = Trim(new string(text, 0, (int)(size / 2)));
        }

        title ??= className ?? "Other devices";
        cache[classGuid] = title;
        return title;
    }

    private static uint Property(uint node, DevPropKey key, ref byte[] buffer, out uint type)
    {
        while (true)
        {
            var size = (uint)buffer.Length;
            uint result;
            fixed (byte* p = buffer)
            {
                uint t;
                result = CM_Get_DevNode_PropertyW(node, &key, &t, p, &size, 0);
                type = t;
            }

            if (result == CrBufferSmall && size > buffer.Length && size < 1 << 20)
            {
                buffer = new byte[size];
                continue;
            }

            return result == CrSuccess ? size : 0;
        }
    }

    private static string? Text(uint node, DevPropKey key, ref byte[] buffer)
    {
        var size = Property(node, key, ref buffer, out var type);
        if (size < 2 || type != TypeString)
            return null;

        return Trim(System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size));
    }

    private static string[] TextList(uint node, DevPropKey key, ref byte[] buffer)
    {
        var size = Property(node, key, ref buffer, out var type);
        if (size < 2 || type != TypeStringList)
            return [];

        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static DateTime? Time(uint node, DevPropKey key, ref byte[] buffer)
    {
        var size = Property(node, key, ref buffer, out var type);
        if (size != 8 || type != TypeFileTime)
            return null;

        var ticks = BitConverter.ToInt64(buffer, 0);
        return ticks > 0 && ticks < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(ticks) : null;
    }

    private static uint? Number(uint node, DevPropKey key, ref byte[] buffer)
    {
        var size = Property(node, key, ref buffer, out _);
        return size == 4 ? BitConverter.ToUInt32(buffer, 0) : null;
    }

    private static string? ClassId(uint node, DevPropKey key, ref byte[] buffer)
    {
        var size = Property(node, key, ref buffer, out _);
        return size == 16 ? new Guid(buffer.AsSpan(0, 16)).ToString("B") : null;
    }

    private static string? Trim(string text)
    {
        var trimmed = text.TrimEnd('\0').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static DevPropKey Key(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g, byte h, byte i, byte j, byte k, uint pid) =>
        new() { Fmtid = new Guid(a, b, c, d, e, f, g, h, i, j, k), Pid = pid };

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_ID_List_SizeW(uint* length, char* filter, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_ID_ListW(char* filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Locate_DevNodeW(uint* node, char* instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(uint* status, uint* problem, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint node, DevPropKey* key, uint* type, byte* buffer, uint* size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Class_PropertyW(Guid* classGuid, DevPropKey* key, uint* type, byte* buffer, uint* size, uint flags);
}
