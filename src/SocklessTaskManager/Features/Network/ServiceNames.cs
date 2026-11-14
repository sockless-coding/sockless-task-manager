using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// Which services run in which process, so a socket owned by svchost.exe can say "DNS Client".
/// One <c>EnumServicesStatusExW</c> call over the running Win32 services; needs no elevation.
/// </summary>
internal static unsafe partial class ServiceNames
{
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceActive = 0x1;
    private const uint ErrorMoreData = 234;

    // ENUM_SERVICE_STATUS_PROCESSW (x64): service name, display name, then SERVICE_STATUS_PROCESS.
    private const int EntrySize = 56;
    private const int EntryDisplayName = 8;
    private const int EntryProcessId = 16 + 28;

    /// <summary>Display names of the running services, by process id.</summary>
    public static Dictionary<int, List<string>> Read()
    {
        var byProcess = new Dictionary<int, List<string>>();
        nint manager;
        try
        {
            manager = OpenSCManager(null, null, ScManagerEnumerateService);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return byProcess;
        }

        if (manager == 0)
            return byProcess;

        try
        {
            uint resume = 0;
            var buffer = new byte[64 * 1024];
            while (true)
            {
                bool done;
                uint needed, returned;
                fixed (byte* b = buffer)
                {
                    done = EnumServicesStatusEx(manager, ScEnumProcessInfo, ServiceWin32, ServiceActive, b, (uint)buffer.Length,
                        out needed, out returned, ref resume, null);
                    var error = done ? 0 : Marshal.GetLastPInvokeError();
                    for (var i = 0; i < returned; i++)
                    {
                        var entry = b + i * EntrySize;
                        var id = *(int*)(entry + EntryProcessId);
                        var display = *(char**)(entry + EntryDisplayName);
                        if (id == 0 || display is null)
                            continue;

                        if (!byProcess.TryGetValue(id, out var names))
                            byProcess[id] = names = [];
                        names.Add(new string(display));
                    }

                    if (!done && error != ErrorMoreData)
                        break;
                }

                if (done)
                    break;
                if (returned == 0)
                    buffer = new byte[Math.Max(needed, (uint)buffer.Length * 2)];
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        foreach (var names in byProcess.Values)
            names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return byProcess;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint OpenSCManager(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(nint manager, int infoLevel, uint serviceType, uint serviceState, byte* services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, char* groupName);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
