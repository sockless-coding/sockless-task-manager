using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// Says when the uninstall registry or this user's package repository changes: an app installed
/// or uninstalled, here or anywhere else. Many uninstallers hand over to a copy of themselves and
/// exit at once, so this, not their exit, is how the list learns they've finished. Registry change
/// notifications on the thread pool: nothing runs until something changes.
/// </summary>
internal sealed partial class AppWatcher : IDisposable
{
    private const uint NotifyName = 0x1;
    private const uint NotifyLastSet = 0x4;

    /// <summary>The registration outlives the thread that made it (Windows 8 and later), so a pool thread can re-arm it.</summary>
    private const uint NotifyThreadAgnostic = 0x10000000;

    private readonly List<(RegistryKey Key, AutoResetEvent Signal, RegisteredWaitHandle Wait)> _watches = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="changed">Called on a pool thread, once per burst of changes or more.</param>
    public AppWatcher(IEnumerable<(RegistryHive Hive, string Path)> keys, Action changed)
    {
        foreach (var (hive, path) in keys)
        {
            var key = AppRegistry.Open(hive, path);
            if (key is null)
                continue;

            var signal = new AutoResetEvent(false);
            if (!Arm(key, signal))
            {
                signal.Dispose();
                key.Dispose();
                continue;
            }

            var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    Arm(key, signal);
                }

                changed();
            }, null, Timeout.Infinite, executeOnlyOnce: false);
            _watches.Add((key, signal, wait));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        foreach (var (key, signal, wait) in _watches)
        {
            wait.Unregister(null);
            key.Dispose();
            signal.Dispose();
        }

        _watches.Clear();
    }

    private static bool Arm(RegistryKey key, AutoResetEvent signal)
    {
        try
        {
            return RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), watchSubtree: 1, NotifyName | NotifyLastSet | NotifyThreadAgnostic,
                signal.SafeWaitHandle.DangerousGetHandle(), asynchronous: 1) == 0;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint filter, nint signal, int asynchronous);
}
