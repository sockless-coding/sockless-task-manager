using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>A VARIANT: 24 bytes on x64. Only empty and VT_I4 ones are made here.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Variant
{
    public ushort Type;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public nint Value;
    public nint Extra;
}

/// <summary>COM by vtable (Native AOT has no built-in COM): Autostart's Task Scheduler and WMI readers, and Windows Update for Drivers.</summary>
internal static unsafe partial class Com
{
    public const uint ClsctxInprocServer = 0x1;
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
            Name = "COM",
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("COM call failed.", failure);
        return result;
    }

    public static void Release(nint unknown)
    {
        if (unknown != 0)
            ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);
    }

    public static nint Bstr(string text)
    {
        fixed (char* p = text)
            return SysAllocString(p);
    }

    public static string TakeBstr(nint bstr)
    {
        if (bstr == 0)
            return string.Empty;

        try
        {
            return new string((char*)bstr);
        }
        finally
        {
            SysFreeString(bstr);
        }
    }

    /// <summary>Null for success; otherwise the reason in words.</summary>
    public static string? Describe(int hr) => hr switch
    {
        >= 0 => null,
        unchecked((int)0x80070005) => "Windows refused: it needs administrator rights.",
        unchecked((int)0x80070002) or unchecked((int)0x80070003) => "It's no longer there.",
        _ => $"Windows couldn't do that (0x{hr:X8}).",
    };

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [LibraryImport("ole32.dll")]
    public static partial int CoSetProxyBlanket(nint proxy, uint authentication, uint authorization, nint serverPrincipal,
        uint authenticationLevel, uint impersonationLevel, nint identity, uint capabilities);

    [LibraryImport("oleaut32.dll")]
    public static partial nint SysAllocString(char* text);

    [LibraryImport("oleaut32.dll")]
    public static partial void SysFreeString(nint bstr);

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(Variant* variant);
}
