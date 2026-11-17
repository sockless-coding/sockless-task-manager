using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>
/// Says when any device is enumerated, started or removed, through CM_Register_Notification for
/// every device instance. Windows calls back on its own thread pool, and only when something
/// happens: nothing runs in between. A burst (a dock with ten devices) arrives as many calls;
/// <see cref="DriverMonitor"/> waits for it to settle.
/// </summary>
internal sealed unsafe partial class DeviceWatcher : IDisposable
{
    private const uint FilterAllDeviceInstances = 0x2;
    private const uint FilterTypeDeviceInstance = 2;
    private const int FilterSize = 416;

    private readonly Action<string> _changed;
    private GCHandle _self;
    private nint _registration;

    /// <param name="changed">Called on a system thread with the device's instance id.</param>
    public DeviceWatcher(Action<string> changed)
    {
        _changed = changed;
        _self = GCHandle.Alloc(this);

        // CM_NOTIFY_FILTER: cbSize, Flags, FilterType, Reserved, then a 400-byte union (unused for "all devices").
        var filter = stackalloc byte[FilterSize];
        new Span<byte>(filter, FilterSize).Clear();
        *(uint*)filter = FilterSize;
        *(uint*)(filter + 4) = FilterAllDeviceInstances;
        *(uint*)(filter + 8) = FilterTypeDeviceInstance;

        nint registration;
        if (CM_Register_Notification(filter, GCHandle.ToIntPtr(_self), &Callback, &registration) == 0)
            _registration = registration;
        else
            _self.Free();
    }

    public bool IsWatching => _registration != 0;

    public void Dispose()
    {
        if (_registration == 0)
            return;

        // Waits for callbacks in flight, so the handle outlives every call.
        CM_Unregister_Notification(_registration);
        _registration = 0;
        _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Callback(nint notification, nint context, uint action, byte* data, uint size)
    {
        try
        {
            // CM_NOTIFY_EVENT_DATA: FilterType, Reserved, then the instance id for device instance events.
            if (context != 0 && size > 8 && GCHandle.FromIntPtr(context).Target is DeviceWatcher watcher)
                watcher._changed(new string((char*)(data + 8)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Device notification failed: {ex}");
        }

        return 0;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(byte* filter, nint context, delegate* unmanaged[Stdcall]<nint, nint, uint, byte*, uint, uint> callback, nint* registration);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint registration);
}
