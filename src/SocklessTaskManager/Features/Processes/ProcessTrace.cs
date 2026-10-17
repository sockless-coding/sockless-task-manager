using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>A process that ended, as the kernel reported it. Times are FILETIME (UTC).</summary>
/// <param name="Name">The file name, or null if only the stop was seen and it carried none.</param>
/// <param name="Path">Full executable path, known only if the start was seen.</param>
/// <param name="SessionId">-1 if only the stop was seen.</param>
internal readonly record struct TracedExit(
    ProcessKey Key,
    int ParentId,
    int SessionId,
    string? Name,
    string? Path,
    long ExitTime,
    uint ExitCode,
    ulong PeakCommit);

/// <summary>
/// Every process start and stop, from a real-time ETW session on the Microsoft-Windows-Kernel-Process
/// provider. Unlike sampling once a second, this sees processes that live for a few milliseconds,
/// and costs next to nothing while nothing starts. Starting the session needs administrator rights,
/// so it only runs elevated. Events arrive on the trace thread, at most a second late (the flush
/// timer); exits are queued for the UI thread.
/// </summary>
internal sealed unsafe partial class ProcessTrace : IDisposable
{
    private const string SessionName = "Sockless Task Manager process trace";
    private static readonly Guid KernelProcessProvider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private const ulong KeywordProcess = 0x10;
    private const byte LevelInformation = 4;

    private const uint ErrorAlreadyExists = 183;
    private const uint ControlStop = 1;
    private const uint EnableProvider = 1;
    private const ulong InvalidTrace = ulong.MaxValue;

    // EVENT_TRACE_PROPERTIES (x64), with the session name stored after it.
    private const int PropertiesSize = 0x78;
    private const int PropertiesTotal = PropertiesSize + 1024;
    private const int WnodeBufferSize = 0x00;
    private const int WnodeClientContext = 0x28;
    private const int WnodeFlags = 0x2C;
    private const int PropLogFileMode = 0x40;
    private const int PropFlushTimer = 0x44;
    private const int PropLoggerNameOffset = 0x74;
    private const uint WnodeFlagTracedGuid = 0x00020000;
    private const uint RealTimeMode = 0x00000100;
    private const uint ClientContextQpc = 1;

    // EVENT_TRACE_LOGFILEW (x64).
    private const int LogFileSize = 0x1C0;
    private const int LogFileLoggerName = 0x08;
    private const int LogFileProcessTraceMode = 0x1C;
    private const int LogFileEventRecordCallback = 0x1A8;
    private const int LogFileContext = 0x1B8;
    private const uint ProcessTraceRealTime = 0x00000100;
    private const uint ProcessTraceEventRecord = 0x10000000;

    // EVENT_RECORD (x64).
    private const int RecordProviderId = 0x18;
    private const int RecordEventId = 0x28;
    private const int RecordEventVersion = 0x2A;
    private const int RecordUserDataLength = 0x56;
    private const int RecordUserData = 0x60;
    private const int RecordUserContext = 0x68;

    private const ushort EventProcessStart = 1;
    private const ushort EventProcessStop = 2;

    private readonly ConcurrentQueue<TracedExit> _exits = new();
    private readonly Dictionary<ProcessKey, Started> _starts = [];
    private readonly List<(string Device, string Drive)> _devices = DeviceMap();
    private GCHandle _self;
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private nint _loggerName;
    private Thread? _thread;
    private volatile bool _isRunning;

    private ProcessTrace()
    {
    }

    /// <summary>False once the session is gone: stopped from outside, or by a second copy of this app.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Starts the session, or returns null without administrator rights or if ETW refuses.</summary>
    public static ProcessTrace? TryStart()
    {
        if (!Environment.IsPrivilegedProcess)
            return null;

        var trace = new ProcessTrace();
        try
        {
            if (trace.Start())
                return trace;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        trace.Dispose();
        return null;
    }

    public bool TryDequeue(out TracedExit exit) => _exits.TryDequeue(out exit);

    private bool Start()
    {
        var properties = (byte*)NativeMemory.AllocZeroed(PropertiesTotal);
        try
        {
            fixed (char* name = SessionName)
            {
                FillProperties(properties);
                var status = StartTrace(out _session, name, properties);
                if (status == ErrorAlreadyExists)
                {
                    // Left over from a copy of the app that didn't shut down cleanly.
                    FillProperties(properties);
                    ControlTrace(0, name, properties, ControlStop);
                    FillProperties(properties);
                    status = StartTrace(out _session, name, properties);
                }

                if (status != 0)
                {
                    _session = 0;
                    return false;
                }
            }

            var provider = KernelProcessProvider;
            if (EnableTraceEx2(_session, &provider, EnableProvider, LevelInformation, KeywordProcess, 0, 0, null) != 0)
                return false;
        }
        finally
        {
            NativeMemory.Free(properties);
        }

        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(SessionName);
        var logFile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
        try
        {
            *(nint*)(logFile + LogFileLoggerName) = _loggerName;
            *(uint*)(logFile + LogFileProcessTraceMode) = ProcessTraceRealTime | ProcessTraceEventRecord;
            *(nint*)(logFile + LogFileEventRecordCallback) = (nint)(delegate* unmanaged<byte*, void>)&OnEvent;
            *(nint*)(logFile + LogFileContext) = GCHandle.ToIntPtr(_self);
            _trace = OpenTrace(logFile);
        }
        finally
        {
            NativeMemory.Free(logFile);
        }

        if (_trace == InvalidTrace)
            return false;

        _isRunning = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Process trace" };
        _thread.Start();
        return true;
    }

    private static void FillProperties(byte* properties)
    {
        new Span<byte>(properties, PropertiesTotal).Clear();
        *(uint*)(properties + WnodeBufferSize) = PropertiesTotal;
        *(uint*)(properties + WnodeClientContext) = ClientContextQpc;
        *(uint*)(properties + WnodeFlags) = WnodeFlagTracedGuid;
        *(uint*)(properties + PropLogFileMode) = RealTimeMode;
        *(uint*)(properties + PropFlushTimer) = 1;
        *(uint*)(properties + PropLoggerNameOffset) = PropertiesSize;
    }

    private void Run()
    {
        var trace = _trace;
        ProcessTraceEvents(&trace, 1, null, null);
        _isRunning = false;
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(byte* record)
    {
        try
        {
            var context = *(nint*)(record + RecordUserContext);
            if (context == 0 || GCHandle.FromIntPtr(context).Target is not ProcessTrace trace)
                return;
            if (*(Guid*)(record + RecordProviderId) != KernelProcessProvider)
                return;

            var data = new ReadOnlySpan<byte>(*(byte**)(record + RecordUserData), *(ushort*)(record + RecordUserDataLength));
            var version = record[RecordEventVersion];
            switch (*(ushort*)(record + RecordEventId))
            {
                case EventProcessStart:
                    trace.OnStart(data, version);
                    break;
                case EventProcessStop:
                    trace.OnStop(data, version);
                    break;
            }
        }
        catch (Exception)
        {
            // Never let an exception unwind into ETW. A malformed event is dropped.
        }
    }

    /// <summary>
    /// ProcessStart. v0: id, create time, parent, session, image. v1–v2 add flags before the image.
    /// v3+ add sequence numbers after the ids, and elevation and a SID before the image.
    /// </summary>
    private void OnStart(ReadOnlySpan<byte> data, byte version)
    {
        int id, parent, session, image;
        long created;
        if (version >= 3)
        {
            if (data.Length < 50)
                return;
            id = BinaryPrimitives.ReadInt32LittleEndian(data);
            created = BinaryPrimitives.ReadInt64LittleEndian(data[12..]);
            parent = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
            session = BinaryPrimitives.ReadInt32LittleEndian(data[32..]);
            image = 48 + 8 + 4 * data[49];
        }
        else
        {
            id = BinaryPrimitives.ReadInt32LittleEndian(data);
            created = BinaryPrimitives.ReadInt64LittleEndian(data[4..]);
            parent = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
            session = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
            image = version == 0 ? 20 : 24;
        }

        var path = image < data.Length ? DosPath(Utf16(data[image..])) : null;
        _starts[new ProcessKey(id, created)] = new Started(parent, session, path);
    }

    /// <summary>
    /// ProcessStop. v0–v1: id, create time, exit time, exit code, …, peak commit, then (later) an
    /// ANSI image name of up to 15 characters. v2 adds a sequence number after the id.
    /// </summary>
    private void OnStop(ReadOnlySpan<byte> data, byte version)
    {
        var shift = version >= 2 ? 8 : 0;
        if (data.Length < 48 + shift)
            return;

        var id = BinaryPrimitives.ReadInt32LittleEndian(data);
        var key = new ProcessKey(id, BinaryPrimitives.ReadInt64LittleEndian(data[(4 + shift)..]));
        var exitTime = BinaryPrimitives.ReadInt64LittleEndian(data[(12 + shift)..]);
        var exitCode = BinaryPrimitives.ReadUInt32LittleEndian(data[(20 + shift)..]);
        var peakCommit = BinaryPrimitives.ReadUInt64LittleEndian(data[(40 + shift)..]);

        if (_starts.Remove(key, out var start))
        {
            var name = start.Path is null ? null : System.IO.Path.GetFileName(start.Path);
            _exits.Enqueue(new TracedExit(key, start.ParentId, start.SessionId, name, start.Path, exitTime, exitCode, peakCommit));
        }
        else
        {
            var nameAt = version switch { 0 => 48, 1 => 76, _ => 84 };
            var name = nameAt < data.Length ? Ansi(data[nameAt..]) : null;
            _exits.Enqueue(new TracedExit(key, 0, -1, name, null, exitTime, exitCode, peakCommit));
        }
    }

    private static string? Utf16(ReadOnlySpan<byte> data)
    {
        var chars = MemoryMarshal.Cast<byte, char>(data[..(data.Length & ~1)]);
        var end = chars.IndexOf('\0');
        var text = end >= 0 ? chars[..end] : chars;
        return text.IsEmpty ? null : new string(text);
    }

    private static string? Ansi(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        var text = end >= 0 ? data[..end] : data;
        return text.IsEmpty ? null : Encoding.Latin1.GetString(text);
    }

    /// <summary>The kernel reports <c>\Device\HarddiskVolume3\…</c>; show <c>C:\…</c>.</summary>
    private string? DosPath(string? path)
    {
        if (path is null)
            return null;

        foreach (var (device, drive) in _devices)
        {
            if (path.Length > device.Length && path[device.Length] == '\\' && path.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return string.Concat(drive, path.AsSpan(device.Length));
        }

        const string Network = @"\Device\Mup\";
        return path.StartsWith(Network, StringComparison.OrdinalIgnoreCase) ? string.Concat(@"\\", path.AsSpan(Network.Length)) : path;
    }

    private static List<(string, string)> DeviceMap()
    {
        var map = new List<(string, string)>();
        var target = stackalloc char[1024];
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var drive = $"{letter}:";
            fixed (char* name = drive)
            {
                if (ProcessNative.QueryDosDevice(name, target, 1024) > 0)
                    map.Add((new string(target), drive));
            }
        }

        return map;
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            var properties = (byte*)NativeMemory.AllocZeroed(PropertiesTotal);
            FillProperties(properties);
            ControlTrace(_session, null, properties, ControlStop);
            NativeMemory.Free(properties);
            _session = 0;
        }

        if (_trace != InvalidTrace)
        {
            CloseTrace(_trace);
            _trace = InvalidTrace;
        }

        // Callbacks hold the handle until ProcessTrace returns; if it never does, leak it rather than free it under them.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(2)))
        {
            if (_self.IsAllocated)
                _self.Free();
            if (_loggerName != 0)
                Marshal.FreeHGlobal(_loggerName);
            _loggerName = 0;
        }

        _isRunning = false;
    }

    private readonly record struct Started(int ParentId, int SessionId, string? Path);

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW")]
    private static partial uint StartTrace(out ulong session, char* name, void* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW")]
    private static partial uint ControlTrace(ulong session, char* name, void* properties, uint control);

    [LibraryImport("advapi32.dll")]
    private static partial uint EnableTraceEx2(ulong session, Guid* provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, void* parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    private static partial ulong OpenTrace(void* logFile);

    [LibraryImport("advapi32.dll", EntryPoint = "ProcessTrace")]
    private static partial uint ProcessTraceEvents(ulong* traces, uint count, void* start, void* end);

    [LibraryImport("advapi32.dll")]
    private static partial uint CloseTrace(ulong trace);
}
