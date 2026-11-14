using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Sockless.TaskManager.Core.Interop;

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
internal sealed unsafe class ProcessTrace : EtwSession
{
    private const string SessionName = "Sockless Task Manager process trace";

    private readonly ConcurrentQueue<TracedExit> _exits = new();
    private readonly Dictionary<ProcessKey, Started> _starts = [];
    private readonly List<(string Device, string Drive)> _devices = DeviceMap();

    private ProcessTrace()
        : base(SessionName)
    {
    }

    /// <summary>Starts the session, or returns null without administrator rights or if ETW refuses.</summary>
    public static ProcessTrace? TryStart()
    {
        if (!Environment.IsPrivilegedProcess)
            return null;

        var trace = new ProcessTrace();
        if (trace.Start([new EtwProvider(KernelProcessEvents.Provider, LevelInformation, KernelProcessEvents.KeywordProcess)]))
            return trace;

        trace.Dispose();
        return null;
    }

    public bool TryDequeue(out TracedExit exit) => _exits.TryDequeue(out exit);

    protected override void OnEvent(in Guid provider, ushort id, byte version, ReadOnlySpan<byte> data)
    {
        if (provider != KernelProcessEvents.Provider)
            return;

        switch (id)
        {
            case KernelProcessEvents.ProcessStart:
                OnStart(data, version);
                break;
            case KernelProcessEvents.ProcessStop:
                OnStop(data, version);
                break;
        }
    }

    private void OnStart(ReadOnlySpan<byte> data, byte version)
    {
        if (KernelProcessEvents.TryReadStart(data, version, out var id, out var created, out var parent, out var session, out var image))
            _starts[new ProcessKey(id, created)] = new Started(parent, session, DosPath(image));
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

    private readonly record struct Started(int ParentId, int SessionId, string? Path);
}
