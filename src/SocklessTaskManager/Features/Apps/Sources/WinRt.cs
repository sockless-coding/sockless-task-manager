using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Apps;

/// <summary>
/// Windows Runtime by vtable, for the package manager (Native AOT has no built-in COM or WinRT
/// projection). Slots and IIDs are from the Windows SDK headers (windows.management.deployment.h,
/// windows.applicationmodel.h); every interface starts with IInspectable's six slots.
/// </summary>
internal static unsafe partial class WinRt
{
    private const uint CoinitMultithreaded = 0x0;

    /// <summary>Runs on a new thread in the multithreaded apartment, so the caller's apartment never matters.</summary>
    public static T OnThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var hr = CoInitializeEx(0, CoinitMultithreaded);
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (hr >= 0)
                    CoUninitialize();
            }
        })
        {
            IsBackground = true,
            Name = "Apps WinRT",
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("Windows Runtime call failed.", failure);
        return result;
    }

    /// <summary>An instance of a runtime class by its name (its default interface). Null if it can't be made.</summary>
    public static nint Activate(string className)
    {
        var name = Create(className);
        try
        {
            nint instance;
            return RoActivateInstance(name, &instance) >= 0 ? instance : 0;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    /// <summary>QueryInterface; 0 when the object doesn't have it (an older Windows).</summary>
    public static nint Query(nint unknown, in Guid iid)
    {
        if (unknown == 0)
            return 0;

        nint result;
        fixed (Guid* id = &iid)
            return ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)unknown)[0])(unknown, id, &result) >= 0 ? result : 0;
    }

    public static void Release(nint unknown)
    {
        if (unknown != 0)
            ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);
    }

    public static nint Create(string text)
    {
        nint result;
        fixed (char* p = text)
            return WindowsCreateString(p, (uint)text.Length, &result) >= 0 ? result : 0;
    }

    /// <summary>The HSTRING's text, then frees it.</summary>
    public static string Take(nint hstring)
    {
        if (hstring == 0)
            return string.Empty;

        try
        {
            uint length;
            var buffer = WindowsGetStringRawBuffer(hstring, &length);
            return buffer is null ? string.Empty : new string(buffer, 0, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>A string getter at <paramref name="slot"/>; null when it fails.</summary>
    public static string? GetString(nint instance, int slot)
    {
        if (instance == 0)
            return null;

        nint value;
        return ((delegate* unmanaged<nint, nint*, int>)(*(nint**)instance)[slot])(instance, &value) >= 0 ? Take(value) : null;
    }

    /// <summary>A boolean getter (a byte in the ABI); null when it fails.</summary>
    public static bool? GetBool(nint instance, int slot)
    {
        if (instance == 0)
            return null;

        byte value;
        return ((delegate* unmanaged<nint, byte*, int>)(*(nint**)instance)[slot])(instance, &value) >= 0 ? value != 0 : null;
    }

    /// <summary>A 32-bit (enum) getter; null when it fails.</summary>
    public static int? GetInt(nint instance, int slot)
    {
        if (instance == 0)
            return null;

        int value;
        return ((delegate* unmanaged<nint, int*, int>)(*(nint**)instance)[slot])(instance, &value) >= 0 ? value : null;
    }

    /// <summary>An object getter; 0 when it fails. The caller releases it.</summary>
    public static nint GetObject(nint instance, int slot)
    {
        if (instance == 0)
            return 0;

        nint value;
        return ((delegate* unmanaged<nint, nint*, int>)(*(nint**)instance)[slot])(instance, &value) >= 0 ? value : 0;
    }

    /// <summary>
    /// Waits for an IAsyncOperation(WithProgress) by polling IAsyncInfo, which needs no completion
    /// delegate. Returns the operation's error code: 0 when it completed.
    /// </summary>
    public static int Wait(nint operation, TimeSpan timeout)
    {
        // IAsyncInfo: get_Id 6, get_Status 7, get_ErrorCode 8. Status: 0 started, 1 completed, 2 cancelled, 3 error.
        var info = Query(operation, IidAsyncInfo);
        if (info == 0)
            return unchecked((int)0x80004002);

        try
        {
            var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                var status = GetInt(info, 7) ?? 3;
                if (status == 1)
                    return 0;
                if (status == 2)
                    return unchecked((int)0x800704C7);
                if (status == 3)
                    return GetInt(info, 8) ?? unchecked((int)0x80004005);
                if (Environment.TickCount64 > until)
                    return unchecked((int)0x800705B4);
                Thread.Sleep(100);
            }
        }
        finally
        {
            Release(info);
        }
    }

    public static readonly Guid IidAsyncInfo = new("00000036-0000-0000-c000-000000000046");

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint className, nint* instance);

    [LibraryImport("combase.dll")]
    private static partial int WindowsCreateString(char* text, uint length, nint* result);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial char* WindowsGetStringRawBuffer(nint hstring, uint* length);
}
