using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Features.Autostart;

/// <summary>A registered scheduled task as the library has it.</summary>
/// <param name="Path">"\Microsoft\Windows\Defrag\ScheduledDefrag".</param>
/// <param name="Xml">The task's definition, which says what it runs and when.</param>
internal sealed record ScheduledTask(string Path, bool Enabled, string Xml);

/// <summary>
/// The Task Scheduler library through its COM interfaces, called by vtable (Native AOT has no
/// built-in COM). Unlike the Tasks folder and the TaskCache key, which only administrators can
/// read, the service shows each user the tasks they may see. Every call initialises COM on its own
/// thread (multithreaded), so it works from the thread pool.
/// </summary>
internal static unsafe partial class TaskScheduler
{
    private const int TaskEnumHidden = 1;
    private const ushort VtI4 = 3;
    private const ushort VariantTrue = 0xFFFF;

    private static readonly Guid ClsidTaskScheduler = new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd");
    private static readonly Guid IidTaskService = new("2faba4c7-4da9-4013-9697-20cc3fd40f85");

    /// <summary>Every task this user can read, hidden ones included. Empty when the service isn't there.</summary>
    public static List<ScheduledTask> List()
    {
        var tasks = new List<ScheduledTask>();
        WithService(service =>
        {
            if (GetFolder(service, @"\") is { } root)
            {
                try
                {
                    Walk(root, tasks, 0);
                }
                finally
                {
                    Com.Release(root);
                }
            }

            return 0;
        });
        return tasks;
    }

    /// <summary>Null on success, else what went wrong.</summary>
    public static string? SetEnabled(string path, bool enabled) => WithTask(path, task =>
    {
        // IRegisteredTask: IDispatch (0-6), get_Name 7, get_Path 8, get_State 9, get_Enabled 10, put_Enabled 11.
        var putEnabled = (delegate* unmanaged<nint, short, int>)(*(nint**)task)[11];
        return putEnabled(task, enabled ? unchecked((short)VariantTrue) : (short)0);
    });

    /// <summary>Null on success, else what went wrong.</summary>
    public static string? Delete(string path)
    {
        var split = path.LastIndexOf('\\');
        var folderPath = split <= 0 ? @"\" : path[..split];
        var name = path[(split + 1)..];
        var result = WithService(service =>
        {
            if (GetFolder(service, folderPath) is not { } folder)
                return unchecked((int)0x80070002);

            try
            {
                // ITaskFolder: DeleteTask = 15.
                var deleteTask = (delegate* unmanaged<nint, nint, int, int>)(*(nint**)folder)[15];
                var bstr = Com.Bstr(name);
                try
                {
                    return deleteTask(folder, bstr, 0);
                }
                finally
                {
                    Com.SysFreeString(bstr);
                }
            }
            finally
            {
                Com.Release(folder);
            }
        });
        return Com.Describe(result);
    }

    private static string? WithTask(string path, Func<nint, int> action)
    {
        var result = WithService(service =>
        {
            if (GetFolder(service, @"\") is not { } root)
                return unchecked((int)0x80070002);

            try
            {
                // ITaskFolder: GetTask = 13.
                var getTask = (delegate* unmanaged<nint, nint, nint*, int>)(*(nint**)root)[13];
                var bstr = Com.Bstr(path);
                nint task;
                int hr;
                try
                {
                    hr = getTask(root, bstr, &task);
                }
                finally
                {
                    Com.SysFreeString(bstr);
                }

                if (hr < 0)
                    return hr;

                try
                {
                    return action(task);
                }
                finally
                {
                    Com.Release(task);
                }
            }
            finally
            {
                Com.Release(root);
            }
        });
        return Com.Describe(result);
    }

    private static int WithService(Func<nint, int> action) => Com.OnThread(() =>
    {
        var clsid = ClsidTaskScheduler;
        var iid = IidTaskService;
        nint service;
        var hr = Com.CoCreateInstance(&clsid, 0, Com.ClsctxInprocServer, &iid, &service);
        if (hr < 0)
            return hr;

        try
        {
            // ITaskService: GetFolder 7, GetRunningTasks 8, NewTask 9, Connect 10 (four VARIANTs: this computer, as this user).
            var connect = (delegate* unmanaged<nint, Variant, Variant, Variant, Variant, int>)(*(nint**)service)[10];
            hr = connect(service, default, default, default, default);
            return hr < 0 ? hr : action(service);
        }
        finally
        {
            Com.Release(service);
        }
    });

    private static nint? GetFolder(nint service, string path)
    {
        var getFolder = (delegate* unmanaged<nint, nint, nint*, int>)(*(nint**)service)[7];
        var bstr = Com.Bstr(path);
        try
        {
            nint folder;
            return getFolder(service, bstr, &folder) >= 0 ? folder : null;
        }
        finally
        {
            Com.SysFreeString(bstr);
        }
    }

    private static void Walk(nint folder, List<ScheduledTask> tasks, int depth)
    {
        // ITaskFolder: get_Name 7, get_Path 8, GetFolder 9, GetFolders 10, CreateFolder 11, DeleteFolder 12, GetTask 13, GetTasks 14.
        var vtable = *(nint**)folder;
        var getTasks = (delegate* unmanaged<nint, int, nint*, int>)vtable[14];
        nint collection;
        if (getTasks(folder, TaskEnumHidden, &collection) >= 0)
        {
            try
            {
                foreach (var task in Items(collection))
                {
                    try
                    {
                        // IRegisteredTask: get_Path 8, get_Enabled 10, get_Xml 20.
                        var taskVtable = *(nint**)task;
                        var getPath = (delegate* unmanaged<nint, nint*, int>)taskVtable[8];
                        var getEnabled = (delegate* unmanaged<nint, short*, int>)taskVtable[10];
                        var getXml = (delegate* unmanaged<nint, nint*, int>)taskVtable[20];

                        nint path, xml;
                        short enabled;
                        if (getPath(task, &path) < 0)
                            continue;

                        var pathText = Com.TakeBstr(path);
                        var xmlText = getXml(task, &xml) >= 0 ? Com.TakeBstr(xml) : string.Empty;
                        var isEnabled = getEnabled(task, &enabled) >= 0 && enabled != 0;
                        tasks.Add(new ScheduledTask(pathText, isEnabled, xmlText));
                    }
                    finally
                    {
                        Com.Release(task);
                    }
                }
            }
            finally
            {
                Com.Release(collection);
            }
        }

        if (depth > 16)
            return;

        var getFolders = (delegate* unmanaged<nint, int, nint*, int>)vtable[10];
        nint folders;
        if (getFolders(folder, 0, &folders) < 0)
            return;

        try
        {
            foreach (var child in Items(folders))
            {
                try
                {
                    Walk(child, tasks, depth + 1);
                }
                finally
                {
                    Com.Release(child);
                }
            }
        }
        finally
        {
            Com.Release(folders);
        }
    }

    /// <summary>A Task Scheduler collection's items (get_Count 7, get_Item 8, indexed from 1). The caller releases each.</summary>
    private static List<nint> Items(nint collection)
    {
        var vtable = *(nint**)collection;
        var getCount = (delegate* unmanaged<nint, int*, int>)vtable[7];
        var getItem = (delegate* unmanaged<nint, Variant, nint*, int>)vtable[8];

        var items = new List<nint>();
        int count;
        if (getCount(collection, &count) < 0)
            return items;

        for (var i = 1; i <= count; i++)
        {
            nint item;
            if (getItem(collection, new Variant { Type = VtI4, Value = i }, &item) >= 0)
                items.Add(item);
        }

        return items;
    }
}

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

/// <summary>COM by vtable, for the Task Scheduler and WMI readers.</summary>
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
            Name = "Autostart COM",
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
