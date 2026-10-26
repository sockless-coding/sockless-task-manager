using System.Diagnostics;
using Sockless.TaskManager.Core.Interop;
using Sockless.TaskManager.Features.Processes.Dotnet;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>Names one process for its whole life: ids are reused, an id plus its start time is not.</summary>
internal readonly record struct ProcessKey(int Id, long CreateTime);

/// <param name="Parent">The process that started this one, if it is still running.</param>
/// <param name="Path">Executable path, where the process can be opened.</param>
/// <param name="User">The owning account without its domain, where the process's token can be read.</param>
/// <param name="Cpu">Share of all logical processors since the last sample, 0–100.</param>
/// <param name="Memory">Private working set, as Task Manager's "Memory" column.</param>
/// <param name="IoRate">Bytes per second of disk, network and device I/O since the last sample.</param>
/// <param name="BasePriority">The priority class as a thread priority (8 is normal).</param>
/// <param name="ParentId">The creator's id, which may now belong to another process; <paramref name="Parent"/> is the checked link.</param>
/// <param name="CommandLine">Only read while something shows it (<see cref="ProcessSampler.WantsCommandLine"/>).</param>
/// <param name="Description">Only read while something shows it (<see cref="ProcessSampler.WantsDescription"/>).</param>
/// <param name="IsDotnet">Has a .NET diagnostics port: a .NET (Core) runtime with diagnostics on.</param>
internal sealed record ProcessReading(
    ProcessKey Key,
    ProcessKey? Parent,
    string Name,
    string? Path,
    string? User,
    int SessionId,
    double Cpu,
    ulong Memory,
    double IoRate,
    int Threads,
    int Handles,
    ulong WorkingSet,
    ulong Commit,
    ulong IoBytes,
    int BasePriority,
    bool IsSuspended,
    int ParentId,
    string? CommandLine,
    string? Description,
    bool IsDotnet);

/// <summary>One sampling pass. Built on the sampling thread, then only read.</summary>
internal sealed class ProcessSnapshot
{
    public ProcessSnapshot(List<ProcessReading> processes, int threads, bool hasRates)
    {
        Processes = processes;
        Threads = threads;
        HasRates = hasRates;
        TakenAt = Environment.TickCount64;
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

    /// <summary><see cref="Environment.TickCount64"/> when the snapshot was taken.</summary>
    public long TakenAt { get; }
}

/// <summary>
/// Turns successive <see cref="SystemProcesses"/> readings into per-process rates. Path and owner
/// are looked up once per process, when it is first seen. Command lines and file descriptions
/// are looked up only while a column or filter needs them, a few per tick, so turning one on
/// with hundreds of processes running doesn't stall a sample. .NET processes are found by listing
/// the diagnostics pipes every few seconds, not by asking each process. Sampling thread only.
/// </summary>
internal sealed class ProcessSampler
{
    private const int CommandLinesPerSample = 64;
    private const int DescriptionsPerSample = 24;

    /// <summary>A runtime opens its pipe just after it starts; this soon is soon enough to notice.</summary>
    private const long DotnetListInterval = 3000;

    private readonly Dictionary<ProcessKey, Extra> _extras = [];
    private readonly Dictionary<string, string?> _accounts = [];
    private readonly Dictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private byte[] _buffer = NtSystem.Buffer(1024 * 1024);
    private Dictionary<ProcessKey, Counters> _previous = [];
    private long _previousTimestamp;
    private HashSet<int> _dotnet = [];
    private long _dotnetListedAt = -DotnetListInterval;

    /// <summary>Set from the UI thread when a column or filter shows command lines.</summary>
    public volatile bool WantsCommandLine;

    /// <summary>Set from the UI thread when a column or filter shows file descriptions.</summary>
    public volatile bool WantsDescription;

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

        var wantsCommandLine = WantsCommandLine;
        var wantsDescription = WantsDescription;
        var commandLines = CommandLinesPerSample;
        var descriptions = DescriptionsPerSample;

        if (Environment.TickCount64 - _dotnetListedAt >= DotnetListInterval)
        {
            _dotnet = DiagnosticsPort.ListProcesses();
            _dotnetListedAt = Environment.TickCount64;
        }

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

            if (!_extras.TryGetValue(key, out var extra))
                _extras[key] = extra = FirstSight(p.Id);

            if (wantsCommandLine && !extra.HasCommandLine && commandLines > 0)
            {
                commandLines--;
                extra.CommandLine = ReadCommandLine(p.Id);
                extra.HasCommandLine = true;
            }

            if (wantsDescription && !extra.HasDescription && extra.Path is { } path)
            {
                // Executables are shared (dozens of svchost.exe), so descriptions are cached by path.
                if (!_descriptions.TryGetValue(path, out var description) && descriptions > 0)
                {
                    descriptions--;
                    _descriptions[path] = description = ProcessFacts.VersionInfo(path).Description;
                }

                if (_descriptions.ContainsKey(path))
                {
                    extra.Description = description;
                    extra.HasDescription = true;
                }
            }

            readings.Add(new ProcessReading(
                key, parent, p.Name, extra.Path, extra.User, p.SessionId, cpu, p.PrivateWorkingSet, io, p.Threads,
                p.Handles, p.WorkingSet, p.Commit, p.IoBytes, p.BasePriority, p.IsSuspended, p.ParentId,
                wantsCommandLine ? extra.CommandLine : null,
                wantsDescription ? extra.Description : null,
                _dotnet.Contains(p.Id)));
        }

        foreach (var key in _extras.Keys)
        {
            if (!current.ContainsKey(key))
                _extras.Remove(key);
        }

        _previous = current;
        return new ProcessSnapshot(readings, threads, capacity > 0);
    }

    /// <summary>Path and owner, from one handle.</summary>
    private Extra FirstSight(int id)
    {
        var extra = new Extra();
        var process = ProcessNative.OpenProcess(ProcessNative.QueryLimitedInformation, false, (uint)id);
        if (process == 0)
            return extra;

        try
        {
            extra.Path = ProcessImage.Path(process);
            extra.User = ProcessFacts.Owner(process, _accounts);
        }
        finally
        {
            ProcessNative.CloseHandle(process);
        }

        return extra;
    }

    private static string? ReadCommandLine(int id)
    {
        var process = ProcessNative.OpenProcess(ProcessNative.QueryLimitedInformation, false, (uint)id);
        if (process == 0)
            return null;

        try
        {
            return ProcessFacts.CommandLine(process);
        }
        finally
        {
            ProcessNative.CloseHandle(process);
        }
    }

    private readonly record struct Counters(long CpuTime, ulong IoBytes);

    /// <summary>What is looked up about a process rather than sampled, kept for its lifetime.</summary>
    private sealed class Extra
    {
        public string? Path;
        public string? User;
        public string? CommandLine;
        public bool HasCommandLine;
        public string? Description;
        public bool HasDescription;
    }
}
