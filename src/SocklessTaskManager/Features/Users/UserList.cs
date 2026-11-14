using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Users;

/// <summary>What a row stands for: a session and its user's processes, or an account and all of its processes.</summary>
/// <param name="Hidden">Processes in the session whose owner Windows won't name without elevation.</param>
internal sealed record UserGroup(string Key, Session? Session, Account? Account, List<UserProcess> Processes, int Hidden);

/// <summary>
/// The Users page's state. Signed in: one row per session someone is signed in to, with that
/// user's processes in it. All accounts: one row per account that runs a process, services and
/// Windows itself included. Samples only while the page is on screen.
/// </summary>
public sealed class UserList : ObservableObject
{
    private const string UnreadableKey = "unreadable";

    /// <summary>The details list keeps its order between re-sorts, so it doesn't reshuffle under the pointer.</summary>
    private const long ResortMilliseconds = 5000;

    // Sampling thread.
    private readonly UsersSampler _sampler = new();

    // Shared between threads.
    private volatile bool _isShown;
    private UsersSnapshot? _pending;

    // UI thread.
    private readonly Dictionary<string, UserRow> _rows = [];
    private readonly Dictionary<(int, long), UserProcessLine> _lines = [];
    private UsersSnapshot? _latest;
    private bool _isSignedInView = true;
    private string _summary = string.Empty;
    private string _hint = string.Empty;
    private UserRow? _selected;
    private UserDetails? _details;
    private Confirmation? _confirmation;
    private long _rankedAt;
    private bool _isEmpty;

    internal UserList()
    {
    }

    /// <summary>Set by the view; the page samples only while it's shown.</summary>
    public bool IsShown
    {
        get => _isShown;
        set => _isShown = value;
    }

    public ObservableCollection<UserRow> Rows { get; } = [];

    public bool IsSignedInView
    {
        get => _isSignedInView;
        set
        {
            if (Set(ref _isSignedInView, value))
            {
                OnPropertyChanged(nameof(IsAllAccountsView));
                OnPropertyChanged(nameof(NameHeader));
                OnPropertyChanged(nameof(StatusHeader));
                OnPropertyChanged(nameof(SessionHeader));
                OnPropertyChanged(nameof(SinceHeader));
                OnPropertyChanged(nameof(EmptyText));
                Select(null);
                Render();
            }
        }
    }

    public bool IsAllAccountsView
    {
        get => !_isSignedInView;
        set => IsSignedInView = !value;
    }

    public string NameHeader => _isSignedInView ? "User" : "Account";

    public string StatusHeader => _isSignedInView ? "Status" : "Type";

    public string SessionHeader => _isSignedInView ? "Session" : "Sessions";

    public string SinceHeader => _isSignedInView ? "Signed in" : "Running since";

    public string EmptyText => _isSignedInView ? "Nobody is signed in." : "No processes to list.";

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>What's missing without administrator rights; empty when nothing is.</summary>
    public string Hint
    {
        get => _hint;
        private set
        {
            if (Set(ref _hint, value))
                OnPropertyChanged(nameof(HasHint));
        }
    }

    public bool HasHint => _hint.Length > 0;

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    public UserDetails? Details
    {
        get => _details;
        private set
        {
            if (Set(ref _details, value))
                OnPropertyChanged(nameof(HasDetails));
        }
    }

    public bool HasDetails => _details is not null;

    public Confirmation? Confirmation
    {
        get => _confirmation;
        private set
        {
            if (Set(ref _confirmation, value))
                OnPropertyChanged(nameof(HasConfirmation));
        }
    }

    public bool HasConfirmation => _confirmation is not null;

    /// <summary>Esc: the dialog first, then the details pane. False when there was nothing to close.</summary>
    public bool Dismiss()
    {
        if (_confirmation is not null)
        {
            Confirmation = null;
            return true;
        }

        if (_details is null)
            return false;

        Select(null);
        return true;
    }

    internal void Sample()
    {
        if (!_isShown)
            return;

        var snapshot = _sampler.Sample();
        Volatile.Write(ref _pending, snapshot);
    }

    internal void Apply()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } snapshot)
            return;

        _latest = snapshot;
        Render();
    }

    private void Render()
    {
        if (_latest is not { } snapshot)
            return;

        var groups = _isSignedInView ? BySession(snapshot) : ByAccount(snapshot);
        var wanted = new List<UserRow>(groups.Count);
        var keys = new HashSet<string>(groups.Count);
        foreach (var group in groups)
        {
            keys.Add(group.Key);
            if (!_rows.TryGetValue(group.Key, out var row))
                _rows[group.Key] = row = new UserRow(group.Key, Select);

            row.Group = group;
            Fill(row, group, snapshot);
            wanted.Add(row);
        }

        foreach (var key in _rows.Keys.Where(k => !keys.Contains(k)).ToList())
            _rows.Remove(key);

        Reconcile(Rows, wanted);
        IsEmpty = Rows.Count == 0;
        Summary = SummaryOf(snapshot, groups);
        Hint = HintOf(snapshot);

        if (_selected is not null && !_rows.ContainsKey(_selected.Key))
            Select(null);
        else if (_selected?.Group is { } selected && _details is { } details)
            FillDetails(details, selected, snapshot);
    }

    // ---------- Grouping ----------

    private static List<UserGroup> BySession(UsersSnapshot snapshot)
    {
        var groups = new List<UserGroup>(snapshot.Sessions.Count);
        foreach (var session in snapshot.Sessions.OrderBy(s => s.Id))
        {
            var processes = new List<UserProcess>();
            var hidden = 0;
            foreach (var p in snapshot.Processes)
            {
                if (p.SessionId != session.Id)
                    continue;
                if (p.Owner is null)
                    hidden++;
                else if (p.Owner.Kind == AccountKind.Person)
                    processes.Add(p);
            }

            groups.Add(new UserGroup($"session:{session.Id}", session, null, processes, hidden));
        }

        return groups;
    }

    private static List<UserGroup> ByAccount(UsersSnapshot snapshot)
    {
        var bySid = new Dictionary<string, (Account? Account, List<UserProcess> Processes)>();
        foreach (var p in snapshot.Processes)
        {
            var key = p.Owner?.Sid ?? UnreadableKey;
            if (!bySid.TryGetValue(key, out var entry))
                bySid[key] = entry = (p.Owner, []);
            entry.Processes.Add(p);
        }

        return bySid
            .OrderBy(e => Rank(e.Value.Account, snapshot))
            .ThenBy(e => e.Value.Account?.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new UserGroup($"account:{e.Key}", null, e.Value.Account, e.Value.Processes, 0))
            .ToList();
    }

    /// <summary>People first (signed in before not), then Windows, services, the per-session helpers, and the unreadable last.</summary>
    private static int Rank(Account? account, UsersSnapshot snapshot) => account?.Kind switch
    {
        null => 9,
        AccountKind.Person => IsSignedIn(account, snapshot) ? 0 : 1,
        AccountKind.System => 2,
        AccountKind.Service => 3,
        AccountKind.AppPool => 4,
        AccountKind.WindowManager => 5,
        AccountKind.FontDriver => 6,
        _ => 7,
    };

    private static bool IsSignedIn(Account account, UsersSnapshot snapshot) =>
        snapshot.Sessions.Any(s =>
            string.Equals(s.UserName, account.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Domain, account.Domain, StringComparison.OrdinalIgnoreCase));

    // ---------- Rows ----------

    private static void Fill(UserRow row, UserGroup group, UsersSnapshot snapshot)
    {
        if (group.Session is { } session)
        {
            row.Name = session.UserName;
            row.Caption = session.Id == snapshot.OwnSession ? $"{session.Domain} · You" : session.Domain;
            row.StatusText = StateText(session.State);
            row.IsSuccess = session.State == SessionState.Active;
            row.SessionText = $"{session.Id.ToString(CultureInfo.CurrentCulture)} · {(session.IsRemote ? "Remote" : "Console")}";
            row.SinceText = session.SignedIn is { } signedIn ? When(signedIn) : "—";
        }
        else if (group.Account is { } account)
        {
            var signedIn = account.Kind == AccountKind.Person && IsSignedIn(account, snapshot);
            row.Name = account.Name;
            row.Caption = account.Domain ?? string.Empty;
            row.StatusText = signedIn ? "Signed in" : KindWord(account.Kind);
            row.IsSuccess = signedIn;
            row.SessionText = SessionList(group.Processes);
            row.SinceText = Earliest(group.Processes);
        }
        else
        {
            row.Name = "Not readable";
            row.Caption = Elevation.IsElevated ? "Windows doesn't say who runs these" : "Needs administrator rights";
            row.StatusText = "Unknown";
            row.IsSuccess = false;
            row.SessionText = SessionList(group.Processes);
            row.SinceText = Earliest(group.Processes);
        }

        row.ProcessesText = Format.Number(group.Processes.Count);
        row.CpuText = CpuText(group.Processes);
        row.MemoryText = Format.Bytes(group.Processes.Sum(p => (double)p.Memory));
    }

    private static string SummaryOf(UsersSnapshot snapshot, List<UserGroup> groups)
    {
        if (groups.Count == 0)
            return string.Empty;

        if (groups[0].Session is not null)
        {
            var people = snapshot.Sessions.Count;
            var active = snapshot.Sessions.Count(s => s.State == SessionState.Active);
            var signedIn = people == 1 ? "1 user signed in" : $"{Format.Number(people)} users signed in";
            return people == active ? signedIn : $"{signedIn}, {Format.Number(active)} active";
        }

        var accounts = groups.Count(g => g.Account is not null);
        return $"{Format.Number(accounts)} accounts run {Format.Number(snapshot.Processes.Count)} processes";
    }

    private string HintOf(UsersSnapshot snapshot)
    {
        if (Elevation.IsElevated)
            return string.Empty;

        if (_isSignedInView)
        {
            return snapshot.Sessions.Any(s => s.Id != snapshot.OwnSession)
                ? "Windows only says who runs your own processes, so other users' process counts are missing. Run as administrator to see them."
                : string.Empty;
        }

        var unreadable = snapshot.Processes.Count(p => p.Owner is null);
        return unreadable > 0
            ? $"Windows only says who runs your own processes, so {Format.Number(unreadable)} other users' and services' processes are listed as not readable. Run as administrator to see who runs them."
            : string.Empty;
    }

    // ---------- Details ----------

    private void Select(UserRow? row)
    {
        if (row is not null && ReferenceEquals(row, _selected))
            row = null;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = row;
        _lines.Clear();
        _rankedAt = 0;
        if (row?.Group is not { } group || _latest is not { } snapshot)
        {
            _selected = null;
            Details = null;
            return;
        }

        row.IsSelected = true;
        var details = new UserDetails(row.Key, group.Session is not null, () => Select(null), Disconnect, AskSignOut);
        FillDetails(details, group, snapshot);
        Details = details;
    }

    private void FillDetails(UserDetails details, UserGroup group, UsersSnapshot snapshot)
    {
        var row = _rows[group.Key];
        details.Name = row.Name;
        details.StatusText = row.StatusText;
        details.IsSuccess = row.IsSuccess;

        var facts = new List<UserFact>();
        if (group.Session is { } session)
        {
            details.Subtitle = session.FullName;
            facts.Add(new UserFact("Session", session.Id.ToString(CultureInfo.CurrentCulture)));
            facts.Add(new UserFact("Connection", session.IsRemote
                ? session.Client is { } client ? $"Remote desktop from {client}" : "Remote desktop"
                : "This computer's screen"));
            facts.Add(new UserFact("Window station", session.Station));
            if (session.SignedIn is { } signedIn)
                facts.Add(new UserFact("Signed in", $"{signedIn.ToString("g", CultureInfo.CurrentCulture)} ({Ago(DateTime.Now - signedIn)} ago)"));
            if (session.Disconnected is { } disconnected)
                facts.Add(new UserFact("Disconnected", $"{disconnected.ToString("g", CultureInfo.CurrentCulture)} ({Ago(DateTime.Now - disconnected)} ago)"));
            if (session.State != SessionState.Disconnected
                && (session.Id == snapshot.OwnSession ? Sessions.OwnIdle() ?? session.Idle : session.Idle) is { } idle)
                facts.Add(new UserFact("Idle", idle < TimeSpan.FromMinutes(1) ? "Under a minute" : Ago(idle)));

            details.CanDisconnect = session.State is SessionState.Active or SessionState.Locked;
            details.CanManage = session.Id == snapshot.OwnSession || Elevation.IsElevated;
            details.HiddenText = group.Hidden > 0
                ? $"{Format.Number(group.Hidden)} more processes in this session can't be read{(Elevation.IsElevated ? "." : " without administrator rights.")}"
                : string.Empty;
        }
        else if (group.Account is { } account)
        {
            details.Subtitle = account.FullName;
            facts.Add(new UserFact("Type", KindDescription(account.Kind)));
            facts.Add(new UserFact("SID", account.Sid));
            facts.Add(new UserFact(group.Processes.Select(p => p.SessionId).Distinct().Count() == 1 ? "Session" : "Sessions",
                SessionList(group.Processes)));
            details.HiddenText = string.Empty;
        }
        else
        {
            details.Subtitle = row.Caption;
            facts.Add(new UserFact("Sessions", SessionList(group.Processes)));
            details.HiddenText = Elevation.IsElevated
                ? string.Empty
                : "Other users' and services' processes: Windows names their owners to administrators only.";
        }

        details.Facts = facts;
        details.ProcessesTitle = $"Processes · {Format.Number(group.Processes.Count)}";
        FillProcesses(details, group.Processes);
    }

    /// <summary>Largest in memory first, re-sorted every few seconds; new processes join at the end until then.</summary>
    private void FillProcesses(UserDetails details, List<UserProcess> processes)
    {
        var current = new Dictionary<(int, long), UserProcessLine>(processes.Count);
        foreach (var p in processes)
        {
            var key = (p.Id, p.CreateTime);
            if (!_lines.TryGetValue(key, out var line))
                line = new UserProcessLine(p.Name, p.Id);

            line.Memory = p.Memory;
            line.CpuText = p.Cpu is { } cpu ? Format.Percent(cpu) : "—";
            line.MemoryText = Format.Bytes(p.Memory);
            current[key] = line;
        }

        _lines.Clear();
        foreach (var (key, line) in current)
            _lines[key] = line;

        var now = Environment.TickCount64;
        List<UserProcessLine> wanted;
        if (now - _rankedAt >= ResortMilliseconds)
        {
            _rankedAt = now;
            wanted = [.. current.Values.OrderByDescending(l => l.Memory).ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        else
        {
            var lines = current.Values.ToHashSet();
            wanted = [.. details.Processes.Where(lines.Contains)];
            var listed = wanted.ToHashSet();
            wanted.AddRange(current.Values.Where(l => !listed.Contains(l)));
        }

        Reconcile(details.Processes, wanted);
    }

    // ---------- Actions ----------

    private void Disconnect()
    {
        if (_details?.Key is not { } key || _selected?.Group?.Session is not { } session)
            return;

        Run(key, $"Disconnecting {session.UserName}…", () => Sessions.Disconnect(session.Id, out var error) ? null : Refusal("disconnect", session, error));
    }

    private void AskSignOut()
    {
        if (_details?.Key is not { } key || _selected?.Group?.Session is not { } session || _latest is not { } snapshot)
            return;

        var own = session.Id == snapshot.OwnSession;
        Confirmation = new Confirmation(
            $"Sign out {session.UserName}?",
            $"Windows closes every app {session.UserName} has open in session {session.Id.ToString(CultureInfo.CurrentCulture)}. Anything they haven't saved is lost.",
            own ? "This is your own session: this app closes too." : null,
            "Sign out",
            () =>
            {
                Confirmation = null;
                Run(key, $"Signing out {session.UserName}…", () => Sessions.SignOut(session.Id, out var error) ? null : Refusal("sign out", session, error));
            },
            () => Confirmation = null);
    }

    /// <summary>Off the UI thread: WTS calls go through the Remote Desktop service.</summary>
    private void Run(string key, string progress, Func<string?> action)
    {
        if (_details is { } details)
        {
            details.Message = progress;
            details.MessageIsError = false;
        }

        _ = Task.Run(() =>
        {
            string? failure;
            try
            {
                failure = action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Session action failed: {ex}");
                failure = "Windows couldn't do that.";
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_details is not { } shown || shown.Key != key)
                    return;

                shown.Message = failure ?? string.Empty;
                shown.MessageIsError = failure is not null;
            });
        });
    }

    private static string Refusal(string verb, Session session, int error) => error == 5
        ? $"Windows refused to {verb} {session.UserName}: that needs administrator rights."
        : $"Windows couldn't {verb} {session.UserName} (error {error.ToString(CultureInfo.InvariantCulture)}).";

    // ---------- Text ----------

    private static string StateText(SessionState state) => state switch
    {
        SessionState.Active => "Active",
        SessionState.Locked => "Locked",
        SessionState.Disconnected => "Disconnected",
        _ => "Connecting…",
    };

    private static string KindWord(AccountKind kind) => kind switch
    {
        AccountKind.Person => "User",
        AccountKind.System => "System",
        AccountKind.Service => "Service",
        AccountKind.WindowManager => "Window manager",
        AccountKind.FontDriver => "Font driver",
        AccountKind.AppPool => "App pool",
        _ => "Other",
    };

    private static string KindDescription(AccountKind kind) => kind switch
    {
        AccountKind.Person => "A person's account",
        AccountKind.System => "Windows itself (LocalSystem)",
        AccountKind.Service => "An account services run as",
        AccountKind.WindowManager => "Desktop Window Manager, one per session",
        AccountKind.FontDriver => "The font driver host, one per session",
        AccountKind.AppPool => "An IIS application pool",
        _ => "Another kind of account",
    };

    private static string CpuText(List<UserProcess> processes)
    {
        if (processes.Count == 0)
            return Format.Percent(0);
        if (processes.All(p => p.Cpu is null))
            return "—";
        return Format.Percent(processes.Sum(p => p.Cpu ?? 0));
    }

    private static string SessionList(List<UserProcess> processes) =>
        processes.Count == 0
            ? "—"
            : string.Join(", ", processes.Select(p => p.SessionId).Distinct().Order().Select(id => id.ToString(CultureInfo.CurrentCulture)));

    private static string Earliest(List<UserProcess> processes)
    {
        var earliest = processes.Where(p => p.CreateTime > 0).Select(p => p.CreateTime).DefaultIfEmpty(0).Min();
        return earliest > 0 ? When(DateTime.FromFileTimeUtc(earliest).ToLocalTime()) : "—";
    }

    /// <summary>The time for today, the date and time before that.</summary>
    private static string When(DateTime local) =>
        local.ToString(local.Date == DateTime.Today ? "t" : "g", CultureInfo.CurrentCulture);

    /// <summary>"3 h 12 min", "2 d 4 h", "5 min".</summary>
    private static string Ago(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays} d {span.Hours} h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours} h {span.Minutes} min";
        return $"{Math.Max(0, (int)span.TotalMinutes)} min";
    }

    /// <summary>Moves, inserts and removes so <paramref name="items"/> matches <paramref name="wanted"/>, keeping rows that stay.</summary>
    private static void Reconcile<T>(ObservableCollection<T> items, List<T> wanted) where T : class
    {
        var keep = wanted.ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(items[i]))
                items.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], wanted[i]))
                continue;

            var at = items.IndexOf(wanted[i]);
            if (at >= 0)
                items.Move(at, i);
            else
                items.Insert(i, wanted[i]);
        }
    }
}
