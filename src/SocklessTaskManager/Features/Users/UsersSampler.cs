using System.Diagnostics;
using System.Runtime.InteropServices;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Users;

/// <summary>One running process and who it runs as.</summary>
/// <param name="Owner">Null where the token can't be read: another user's or a system process, without elevation.</param>
/// <param name="Cpu">Percent of the whole machine since the last sample; null on the first.</param>
/// <param name="CreateTime">FILETIME (UTC).</param>
internal readonly record struct UserProcess(
    int Id,
    string Name,
    int SessionId,
    Account? Owner,
    double? Cpu,
    ulong Memory,
    long CreateTime);

/// <param name="OwnSession">The session this app runs in.</param>
internal sealed record UsersSnapshot(IReadOnlyList<Session> Sessions, IReadOnlyList<UserProcess> Processes, int OwnSession);

/// <summary>
/// Reads the sessions and every process with its owner, off the UI thread. Each process's token is
/// read once, the first time it's seen (one open per new process); the process list itself is one
/// <c>SystemProcessInformation</c> call.
/// </summary>
internal sealed partial class UsersSampler
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int SystemProcessId = 4;
    private const string LocalSystemSid = "S-1-5-18";

    private static readonly int OwnSession = Process.GetCurrentProcess().SessionId;

    private readonly Dictionary<string, Account> _accounts = [];
    private byte[] _buffer = NtSystem.Buffer(1024 * 1024);
    private Dictionary<(int Id, long CreateTime), Seen> _seen = [];
    private long _previousTimestamp;

    public UsersSnapshot Sample()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var elapsed = _previousTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_previousTimestamp, timestamp);
        var capacity = elapsed.Ticks * (double)Environment.ProcessorCount;
        _previousTimestamp = timestamp;

        var processes = SystemProcesses.Read(ref _buffer);
        var seen = new Dictionary<(int, long), Seen>(processes.Count);
        var readings = new List<UserProcess>(processes.Count);
        foreach (var p in processes)
        {
            var key = (p.Id, p.CreateTime);
            double? cpu = null;
            if (_seen.TryGetValue(key, out var before))
            {
                if (capacity > 0)
                    cpu = Math.Clamp((p.CpuTime - before.CpuTime) / capacity * 100, 0, 100);
            }
            else
            {
                before = new Seen(Owner(p.Id, p.ParentId), 0);
            }

            seen[key] = before with { CpuTime = p.CpuTime };
            readings.Add(new UserProcess(p.Id, p.Name, p.SessionId, before.Owner, cpu, p.PrivateWorkingSet, p.CreateTime));
        }

        _seen = seen;
        return new UsersSnapshot(Sessions.Read(), readings, OwnSession);
    }

    private Account? Owner(int id, int parentId)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)id);
        if (process != 0)
        {
            try
            {
                if (Accounts.Owner(process, _accounts) is { } account)
                    return account;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // The kernel's own processes (System, Registry, Memory Compression, Secure System) can't
        // be opened even elevated. They run as LocalSystem.
        if (id == SystemProcessId || parentId == SystemProcessId)
            return _accounts.GetValueOrDefault(LocalSystemSid) ?? new Account(LocalSystemSid, "SYSTEM", "NT AUTHORITY", AccountKind.System);

        return null;
    }

    private readonly record struct Seen(Account? Owner, long CpuTime);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
