using System.Diagnostics;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>Names one process for its whole life: ids are reused, an id plus its start time is not.</summary>
internal readonly record struct ProcessKey(int Id, long CreateTime);

/// <param name="Parent">The process that started this one, if it is still running.</param>
/// <param name="Path">Executable path, where the process can be opened.</param>
/// <param name="Cpu">Share of all logical processors since the last sample, 0–100.</param>
/// <param name="Memory">Private working set, as Task Manager's "Memory" column.</param>
/// <param name="IoRate">Bytes per second of disk, network and device I/O since the last sample.</param>
internal sealed record ProcessReading(
    ProcessKey Key,
    ProcessKey? Parent,
    string Name,
    string? Path,
    int SessionId,
    double Cpu,
    ulong Memory,
    double IoRate,
    int Threads);

/// <summary>One sampling pass. Built on the sampling thread, then only read.</summary>
internal sealed class ProcessSnapshot
{
    public ProcessSnapshot(List<ProcessReading> processes, int threads, bool hasRates)
    {
        Processes = processes;
        Threads = threads;
        HasRates = hasRates;
        var byKey = new Dictionary<ProcessKey, ProcessReading>(processes.Count);
        foreach (var process in processes)
            byKey[process.Key] = process;
        ByKey = byKey;
    }

    public IReadOnlyList<ProcessReading> Processes { get; }

    public IReadOnlyDictionary<ProcessKey, ProcessReading> ByKey { get; }

    public int Threads { get; }

    /// <summary>False for the first reading, which has nothing to take CPU and I/O rates against.</summary>
    public bool HasRates { get; }
}

/// <summary>
/// Turns successive <see cref="SystemProcesses"/> readings into per-process rates. Paths are
/// looked up once per process, when it is first seen. Sampling thread only.
/// </summary>
internal sealed class ProcessSampler
{
    private readonly Dictionary<ProcessKey, string?> _paths = [];
    private byte[] _buffer = NtSystem.Buffer(1024 * 1024);
    private Dictionary<ProcessKey, Counters> _previous = [];
    private long _previousTimestamp;

    public ProcessSnapshot Read()
    {
        var processes = SystemProcesses.Read(ref _buffer);
        var now = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_previousTimestamp, now);
        _previousTimestamp = now;

        // CPU times are in 100 ns ticks, as are TimeSpan ticks.
        var capacity = elapsed.Ticks * (double)Environment.ProcessorCount;
        var seconds = elapsed.TotalSeconds;

        var created = new Dictionary<int, long>(processes.Count);
        foreach (var p in processes)
            created[p.Id] = p.CreateTime;

        var current = new Dictionary<ProcessKey, Counters>(processes.Count);
        var readings = new List<ProcessReading>(processes.Count);
        var threads = 0;
        foreach (var p in processes)
        {
            var key = new ProcessKey(p.Id, p.CreateTime);
            current[key] = new Counters(p.CpuTime, p.IoBytes);
            threads += p.Threads;

            double cpu = 0, io = 0;
            if (capacity > 0 && _previous.TryGetValue(key, out var before))
            {
                cpu = Math.Clamp((p.CpuTime - before.CpuTime) / capacity * 100, 0, 100);
                io = p.IoBytes >= before.IoBytes ? (p.IoBytes - before.IoBytes) / seconds : 0;
            }

            // The parent id is only a hint: if that process exited, the id may now belong to a
            // younger, unrelated one.
            ProcessKey? parent = p.ParentId != p.Id
                                 && created.TryGetValue(p.ParentId, out var parentCreated)
                                 && parentCreated <= p.CreateTime
                ? new ProcessKey(p.ParentId, parentCreated)
                : null;

            if (!_paths.TryGetValue(key, out var path))
                _paths[key] = path = ProcessImage.Path(p.Id);

            readings.Add(new ProcessReading(key, parent, p.Name, path, p.SessionId, cpu, p.PrivateWorkingSet, io, p.Threads));
        }

        foreach (var key in _paths.Keys)
        {
            if (!current.ContainsKey(key))
                _paths.Remove(key);
        }

        _previous = current;
        return new ProcessSnapshot(readings, threads, capacity > 0);
    }

    private readonly record struct Counters(long CpuTime, ulong IoBytes);
}
