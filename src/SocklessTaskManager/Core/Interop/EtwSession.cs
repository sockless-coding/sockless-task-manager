using System.Runtime.InteropServices;

namespace Sockless.TaskManager.Core.Interop;

/// <summary>A manifest provider to enable on an <see cref="EtwSession"/>.</summary>
internal readonly record struct EtwProvider(Guid Id, byte Level, ulong Keywords);

/// <summary>
/// A real-time ETW session of our own, read on a background thread. Subclasses enable providers
/// with <see cref="Start"/> and get each event's payload in <see cref="OnEvent"/>. Starting a session
/// needs administrator rights. Events arrive at most a second late (the flush timer).
/// Shared by Processes (process starts and exits) and Network (traffic, DNS lookups).
/// </summary>
internal abstract unsafe partial class EtwSession : IDisposable
{
    public const byte LevelInformation = 4;
    public const byte LevelVerbose = 5;

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
    private const int PropBufferSizeKb = 0x30;
    private const int PropMinimumBuffers = 0x34;
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

    private readonly string _name;
    private readonly uint _bufferKb;
    private GCHandle _self;
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private nint _loggerName;
    private Thread? _thread;
    private volatile bool _isRunning;

    /// <param name="name">The session's name, unique to this app and purpose.</param>
    /// <param name="bufferKb">Size of each session buffer; busy providers (network traffic) want more than the default.</param>
    protected EtwSession(string name, uint bufferKb = 0)
    {
        _name = name;
        _bufferKb = bufferKb;
    }

    /// <summary>False once the session is gone: stopped from outside, or by a second copy of this app.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Starts the session with these providers and begins reading it. False if ETW refuses.</summary>
    protected bool Start(ReadOnlySpan<EtwProvider> providers)
    {
        try
        {
            return StartCore(providers);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>One event, on the trace thread. Keep it short; exceptions are swallowed and the event dropped.</summary>
    protected abstract void OnEvent(in Guid provider, ushort id, byte version, ReadOnlySpan<byte> data);

    private bool StartCore(ReadOnlySpan<EtwProvider> providers)
    {
        var properties = (byte*)NativeMemory.AllocZeroed(PropertiesTotal);
        try
        {
            fixed (char* name = _name)
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

            foreach (var p in providers)
            {
                var id = p.Id;
                if (EnableTraceEx2(_session, &id, EnableProvider, p.Level, p.Keywords, 0, 0, null) != 0)
                    return false;
            }
        }
        finally
        {
            NativeMemory.Free(properties);
        }

        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(_name);
        var logFile = (byte*)NativeMemory.AllocZeroed(LogFileSize);
        try
        {
            *(nint*)(logFile + LogFileLoggerName) = _loggerName;
            *(uint*)(logFile + LogFileProcessTraceMode) = ProcessTraceRealTime | ProcessTraceEventRecord;
            *(nint*)(logFile + LogFileEventRecordCallback) = (nint)(delegate* unmanaged<byte*, void>)&OnRecord;
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
        _thread = new Thread(Run) { IsBackground = true, Name = _name };
        _thread.Start();
        return true;
    }

    private void FillProperties(byte* properties)
    {
        new Span<byte>(properties, PropertiesTotal).Clear();
        *(uint*)(properties + WnodeBufferSize) = PropertiesTotal;
        *(uint*)(properties + WnodeClientContext) = ClientContextQpc;
        *(uint*)(properties + WnodeFlags) = WnodeFlagTracedGuid;
        if (_bufferKb > 0)
        {
            *(uint*)(properties + PropBufferSizeKb) = _bufferKb;
            *(uint*)(properties + PropMinimumBuffers) = 4;
        }

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
    private static void OnRecord(byte* record)
    {
        try
        {
            var context = *(nint*)(record + RecordUserContext);
            if (context == 0 || GCHandle.FromIntPtr(context).Target is not EtwSession session)
                return;

            var data = new ReadOnlySpan<byte>(*(byte**)(record + RecordUserData), *(ushort*)(record + RecordUserDataLength));
            session.OnEvent(in *(Guid*)(record + RecordProviderId), *(ushort*)(record + RecordEventId), record[RecordEventVersion], data);
        }
        catch (Exception)
        {
            // Never let an exception unwind into ETW. A malformed event is dropped.
        }
    }

    public virtual void Dispose()
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

/// <summary>
/// Microsoft-Windows-Kernel-Process's ProcessStart event, read the same way by every slice that
/// traces it.
/// </summary>
internal static class KernelProcessEvents
{
    public static readonly Guid Provider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    public const ulong KeywordProcess = 0x10;
    public const ushort ProcessStart = 1;
    public const ushort ProcessStop = 2;

    /// <summary>
    /// ProcessStart. v0: id, create time, parent, session, image. v1–v2 add flags before the image.
    /// v3+ add sequence numbers after the ids, and elevation and a SID before the image.
    /// </summary>
    /// <param name="imagePath">The kernel's path (<c>\Device\HarddiskVolume3\…</c>), or null.</param>
    public static bool TryReadStart(ReadOnlySpan<byte> data, byte version, out int id, out long created, out int parent, out int session, out string? imagePath)
    {
        int image;
        if (version >= 3)
        {
            if (data.Length < 50)
            {
                (id, created, parent, session, imagePath) = (0, 0, 0, 0, null);
                return false;
            }

            id = BitConverter.ToInt32(data);
            created = BitConverter.ToInt64(data[12..]);
            parent = BitConverter.ToInt32(data[20..]);
            session = BitConverter.ToInt32(data[32..]);
            image = 48 + 8 + 4 * data[49];
        }
        else
        {
            if (data.Length < 20)
            {
                (id, created, parent, session, imagePath) = (0, 0, 0, 0, null);
                return false;
            }

            id = BitConverter.ToInt32(data);
            created = BitConverter.ToInt64(data[4..]);
            parent = BitConverter.ToInt32(data[12..]);
            session = BitConverter.ToInt32(data[16..]);
            image = version == 0 ? 20 : 24;
        }

        imagePath = image < data.Length ? Utf16(data[image..]) : null;
        return true;
    }

    /// <summary>A null-terminated UTF-16 string at the start of <paramref name="data"/>.</summary>
    public static string? Utf16(ReadOnlySpan<byte> data) => Utf16(data, out _);

    /// <param name="consumed">Bytes read, terminator included.</param>
    public static string? Utf16(ReadOnlySpan<byte> data, out int consumed)
    {
        var chars = MemoryMarshal.Cast<byte, char>(data[..(data.Length & ~1)]);
        var end = chars.IndexOf('\0');
        var text = end >= 0 ? chars[..end] : chars;
        consumed = (end >= 0 ? end + 1 : chars.Length) * 2;
        return text.IsEmpty ? null : new string(text);
    }
}
