using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Performance.Gpu;

/// <summary>
/// A hardware display adapter from DXGI. Its LUID is what ties it to the "GPU Engine" and
/// "GPU Adapter Memory" performance counter instances.
/// </summary>
internal sealed partial record GpuAdapter(
    int Index,
    string Name,
    uint LuidLow,
    int LuidHigh,
    ulong DedicatedBytes,
    ulong SharedBytes,
    string DriverVersion)
{
    private const int DxgiAdapterFlagSoftware = 2;
    private const uint MicrosoftVendorId = 0x1414;

    /// <summary>The LUID as it appears in counter instance names: luid_0x00000000_0x0000D1F2.</summary>
    public string CounterLuid => $"luid_0x{LuidHigh:X8}_0x{LuidLow:X8}";

    public static unsafe List<GpuAdapter> Enumerate()
    {
        var adapters = new List<GpuAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(&iid, out var factory) < 0)
            return adapters;

        try
        {
            // IDXGIFactory1 vtable: IUnknown (0-2), IDXGIObject (3-6), IDXGIFactory (7-11), EnumAdapters1 = 12.
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[12];
            var seen = new HashSet<long>();

            for (uint i = 0; ; i++)
            {
                nint adapter;
                if (enumAdapters1(factory, i, &adapter) < 0)
                    break; // DXGI_ERROR_NOT_FOUND ends the list.

                try
                {
                    // IDXGIAdapter1 vtable: IDXGIAdapter ends at 9, GetDesc1 = 10.
                    var getDesc1 = (delegate* unmanaged<nint, AdapterDesc1*, int>)(*(nint**)adapter)[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) < 0)
                        continue;

                    var luid = ((long)desc.LuidHigh << 32) | desc.LuidLow;
                    var isSoftware = (desc.Flags & DxgiAdapterFlagSoftware) != 0 || desc.VendorId == MicrosoftVendorId;
                    if (isSoftware || !seen.Add(luid))
                        continue;

                    adapters.Add(new GpuAdapter(
                        adapters.Count,
                        new string(desc.Description).TrimEnd('\0').Trim(),
                        desc.LuidLow,
                        desc.LuidHigh,
                        desc.DedicatedVideoMemory,
                        desc.SharedSystemMemory,
                        ReadDriverVersion(luid)));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return adapters;
    }

    /// <summary>DirectX keeps one key per adapter with its LUID and a packed driver version.</summary>
    private static string ReadDriverVersion(long luid)
    {
        using var directX = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\DirectX");
        if (directX is null)
            return string.Empty;

        foreach (var name in directX.GetSubKeyNames())
        {
            using var key = directX.OpenSubKey(name);
            if (key?.GetValue("AdapterLuid") is long keyLuid && keyLuid == luid
                && key.GetValue("DriverVersion") is long version)
            {
                return $"{(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";
            }
        }

        return string.Empty;
    }

    private static unsafe void Release(nint unknown) =>
        ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [LibraryImport("dxgi.dll")]
    private static unsafe partial int CreateDXGIFactory1(Guid* riid, out nint factory);
}
