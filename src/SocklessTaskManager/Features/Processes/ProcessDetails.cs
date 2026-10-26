using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Features.Processes.Dotnet;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>One label and value in the details pane. A value can be copied, or opened (the parent process).</summary>
public sealed class Fact : ObservableObject
{
    private string _value;

    internal Fact(string label, string value, ICommand? copy = null, ICommand? open = null, bool isLong = false)
    {
        Label = label;
        _value = value;
        CopyCommand = copy;
        OpenCommand = open;
        IsLong = isLong;
    }

    public string Label { get; }

    /// <summary>Paths and command lines: shown under the label at full width, rather than beside it.</summary>
    public bool IsLong { get; }

    public bool IsShort => !IsLong;

    public string Value
    {
        get => _value;
        internal set => Set(ref _value, value);
    }

    public ICommand? CopyCommand { get; }

    public bool CanCopy => CopyCommand is not null;

    public string CopyName => $"Copy {Label.ToLower(CultureInfo.CurrentCulture)}";

    public ICommand? OpenCommand { get; }

    public bool IsLink => OpenCommand is not null;

    public bool IsText => OpenCommand is null;
}

/// <summary>A priority class as the priority picker lists it.</summary>
public sealed class PriorityOption(uint value, string title)
{
    internal uint Value { get; } = value;

    public string Title { get; } = title;

    public override string ToString() => Title;
}

/// <summary>One logical processor in the affinity editor.</summary>
public sealed class ProcessorToggle : ObservableObject
{
    private readonly Action<ProcessorToggle> _changed;
    private bool _isChecked;

    internal ProcessorToggle(int index, bool isChecked, Action<ProcessorToggle> changed)
    {
        Index = index;
        _isChecked = isChecked;
        _changed = changed;
    }

    internal int Index { get; }

    public string Title => Index.ToString(CultureInfo.CurrentCulture);

    public string AccessibleName => $"Processor {Title}";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (Set(ref _isChecked, value))
                _changed(this);
        }
    }

    /// <summary>Puts the box back without applying anything (a refused change).</summary>
    internal void Show(bool isChecked)
    {
        if (_isChecked == isChecked)
            return;
        _isChecked = isChecked;
        OnPropertyChanged(nameof(IsChecked));
    }
}

/// <summary>
/// The details pane: everything about the selected process, live while it runs, and what can be
/// done to it. Opens on a running process (a table row) or an ended one (the Ended table); a
/// running process that exits stays on screen, marked as exited.
/// </summary>
public sealed class ProcessDetails : ObservableObject
{
    private const double BytesPerMegabyte = 1024 * 1024;

    private static readonly PriorityOption[] PriorityOptions =
    [
        new(ProcessControl.Realtime, "Real-time"),
        new(ProcessControl.High, "High"),
        new(ProcessControl.AboveNormal, "Above normal"),
        new(ProcessControl.Normal, "Normal"),
        new(ProcessControl.BelowNormal, "Below normal"),
        new(ProcessControl.Idle, "Low"),
    ];

    private readonly ProcessList _owner;
    private readonly Fact? _runningFor;
    private readonly Fact? _threads;
    private readonly Fact? _handles;
    private readonly Fact? _workingSet;
    private readonly Fact? _commit;
    private readonly Fact? _io;
    private ProcessFactsReading? _facts;
    private ManagedMemory? _managed;
    private EndedProcess? _ended;
    private string? _parent;
    private ProcessKey? _parentKey;
    private int _sessionId;
    private string _cpuText = "—";
    private string _memoryText = "—";
    private double _memoryMaximum = 1;
    private bool _isAlive;
    private bool _isSuspended;
    private bool _isFollowed;
    private bool _isCritical;
    private bool _unsafeToSuspend;
    private PriorityOption? _priority;
    private bool _isEditingAffinity;
    private ulong _affinity;
    private string _affinityText = string.Empty;
    private string? _notice;
    private bool _noticeIsError;
    private bool _offerElevation;
    private (string Text, ICommand Command)? _noticeAction;
    private bool _isDumping;

    /// <summary>A running process, from the latest sample.</summary>
    internal ProcessDetails(ProcessReading reading, string? parent, ProcessList owner)
        : this(reading.Key, reading.Name, reading.Path, parent, reading.SessionId, owner)
    {
        _parentKey = reading.Parent;
        _isAlive = true;
        _runningFor = new Fact("Running for", string.Empty);
        _threads = new Fact("Threads", string.Empty);
        _handles = new Fact("Handles", string.Empty);
        _workingSet = new Fact("Working set", string.Empty);
        _commit = new Fact("Commit", string.Empty);
        _io = new Fact("I/O so far", string.Empty);
        _managed = new ManagedMemory(reading.Key.Id, reading.Name, () => owner.OpenManaged(this), owner.CloseManaged);

        Update(reading);
        RebuildFacts();
    }

    /// <summary>A process that has already ended.</summary>
    internal ProcessDetails(EndedProcess ended, ProcessList owner)
        : this(ended.Key, ended.Name, ended.Path, ended.Parent, ended.SessionId, owner)
    {
        _ended = ended;
        RebuildFacts();
    }

    private ProcessDetails(ProcessKey key, string name, string? path, string? parent, int sessionId, ProcessList owner)
    {
        _owner = owner;
        Key = key;
        Name = name;
        Path = path;
        _parent = parent;
        _sessionId = sessionId;
        _unsafeToSuspend = ProcessFacts.IsUnsafeToSuspend(key, name, isCritical: false);

        CloseCommand = new RelayCommand(owner.CloseDetails);
        CopyPathCommand = new RelayCommand(() => owner.Copy(Path));
        EndCommand = new RelayCommand(() => owner.RequestEnd(Key, tree: false));
        EndTreeCommand = new RelayCommand(() => owner.RequestEnd(Key, tree: true));
        SuspendCommand = new RelayCommand(() => owner.SuspendOrResume(Key));
        FollowCommand = new RelayCommand(() => owner.Follow(Key));
        OpenLocationCommand = new RelayCommand(() => owner.OpenLocation(Path));
        DumpCommand = new RelayCommand(() => owner.CreateDump(Key));
        AllProcessorsCommand = new RelayCommand(SelectAllProcessors);
        DismissNoticeCommand = new RelayCommand(ClearNotice);
    }

    internal ProcessKey Key { get; }

    public string Name { get; }

    public string IdText => $"PID {Key.Id.ToString(CultureInfo.CurrentCulture)}";

    public string? Path { get; }

    public bool HasPath => Path is not null;

    /// <summary>The file's description and publisher: "Google Chrome · Google LLC".</summary>
    public string? Subtitle => _facts is null
        ? null
        : string.Join(" · ", new[] { _facts.Description, _facts.Company }.Where(s => s is not null));

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public ObservableCollection<Fact> Facts { get; } = [];

    /// <summary>The .NET runtime's view of its memory, while a .NET process runs.</summary>
    public ManagedMemory? Managed
    {
        get => _managed;
        private set => Set(ref _managed, value);
    }

    /// <summary>Share of all logical processors, 0–100, since the process was selected.</summary>
    public History Cpu { get; } = new();

    /// <summary>Private working set in MB.</summary>
    public History Memory { get; } = new();

    public bool HasGraphs => Cpu.Count > 0;

    public string CpuText
    {
        get => _cpuText;
        private set => Set(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => Set(ref _memoryText, value);
    }

    public double MemoryMaximum
    {
        get => _memoryMaximum;
        private set => Set(ref _memoryMaximum, value);
    }

    /// <summary>Still running (possibly suspended), so it can be acted on.</summary>
    public bool IsAlive
    {
        get => _isAlive;
        private set
        {
            if (Set(ref _isAlive, value))
                StatusChanged();
        }
    }

    public bool IsSuspended
    {
        get => _isSuspended;
        private set
        {
            if (Set(ref _isSuspended, value))
                StatusChanged();
        }
    }

    public string StatusText => !_isAlive ? "Exited" : _isSuspended ? "Suspended" : "Running";

    public bool IsRunningStatus => _isAlive && !_isSuspended;

    public string SuspendText => _isSuspended ? "Resume" : "Suspend";

    public Geometry? SuspendIcon => ThemeResource.Geometry(_isSuspended ? "IconPlay" : "IconPause");

    /// <summary>This app, the compositor and critical processes: suspended, nothing could resume them.</summary>
    public bool CanSuspend => _isAlive && (_isSuspended || !_unsafeToSuspend);

    public bool IsCritical => _isCritical;

    public bool IsFollowed
    {
        get => _isFollowed;
        internal set
        {
            if (Set(ref _isFollowed, value))
                OnPropertyChanged(nameof(FollowText));
        }
    }

    public string FollowText => _isFollowed ? "Following" : "Follow";

    public IReadOnlyList<PriorityOption> Priorities => PriorityOptions;

    public bool HasPriority => _isAlive && _priority is not null;

    public PriorityOption? Priority
    {
        get => _priority;
        set
        {
            if (value is null || ReferenceEquals(value, _priority))
                return;

            var before = _priority;
            _priority = value;
            OnPropertyChanged();
            if (before is not null)
                _owner.SetPriority(this, value.Value);
        }
    }

    public ObservableCollection<ProcessorToggle> Processors { get; } = [];

    public bool HasAffinity => _isAlive && Processors.Count > 0;

    public string AffinityText
    {
        get => _affinityText;
        private set => Set(ref _affinityText, value);
    }

    public bool IsEditingAffinity
    {
        get => _isEditingAffinity;
        set => Set(ref _isEditingAffinity, value);
    }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice is not null;

    public bool NoticeIsError
    {
        get => _noticeIsError;
        private set => Set(ref _noticeIsError, value);
    }

    /// <summary>The notice is about missing rights, and restarting elevated would help.</summary>
    public bool OfferElevation
    {
        get => _offerElevation;
        private set => Set(ref _offerElevation, value);
    }

    /// <summary>A link under the notice: "Show in folder" for a dump just written.</summary>
    public string? NoticeActionText => _noticeAction?.Text;

    public ICommand? NoticeActionCommand => _noticeAction?.Command;

    public bool HasNoticeAction => _noticeAction is not null;

    /// <summary>A dump is being written; another would only queue behind it.</summary>
    public bool IsDumping
    {
        get => _isDumping;
        internal set => Set(ref _isDumping, value);
    }

    public ICommand DumpCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand EndCommand { get; }

    public ICommand EndTreeCommand { get; }

    public ICommand SuspendCommand { get; }

    public ICommand FollowCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand AllProcessorsCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public ICommand DismissNoticeCommand { get; }

    internal void Update(ProcessReading reading)
    {
        if (!_isAlive)
            return;

        Cpu.Push(reading.Cpu);
        Memory.Push(reading.Memory / BytesPerMegabyte);
        if (Cpu.Count == 1)
            OnPropertyChanged(nameof(HasGraphs));

        CpuText = ProcessRow.CpuPercent(reading.Cpu);
        MemoryText = Format.Bytes(reading.Memory);
        MemoryMaximum = Format.NiceCeiling(Memory.Max(), 1);
        IsSuspended = reading.IsSuspended;

        var started = DateTime.FromFileTimeUtc(reading.Key.CreateTime);
        _runningFor!.Value = EndedProcess.Duration(DateTime.UtcNow - started);
        _threads!.Value = Format.Number(reading.Threads);
        _handles!.Value = Format.Number(reading.Handles);
        _workingSet!.Value = Format.Bytes(reading.WorkingSet);
        _commit!.Value = Format.Bytes(reading.Commit);
        _io!.Value = Format.Bytes(reading.IoBytes);
        _managed?.Apply();
    }

    /// <summary>Streams the .NET counters while the pane is on screen, so nothing runs in the process otherwise.</summary>
    internal void Watch(bool watch)
    {
        if (watch && _isAlive)
            _managed?.Start();
        else
            _managed?.Stop();
    }

    /// <summary>The process ended. The graphs freeze; <paramref name="ended"/> adds the exit, once it's known.</summary>
    internal void MarkExited(EndedProcess? ended)
    {
        var wasAlive = _isAlive;
        IsAlive = false;
        CpuText = "—";
        IsEditingAffinity = false;
        _managed?.Stop();
        Managed = null;

        if (ended is not null && !ReferenceEquals(ended, _ended))
            _ended = ended;
        else if (!wasAlive)
            return;

        RebuildFacts();
        OnPropertyChanged(nameof(HasPriority));
        OnPropertyChanged(nameof(HasAffinity));
    }

    /// <summary>The off-thread reads, back on the UI thread.</summary>
    internal void ShowFacts(ProcessFactsReading facts, uint? priority, (ulong Process, ulong System)? affinity)
    {
        _facts = facts;
        _isCritical = facts.IsCritical;
        _unsafeToSuspend = ProcessFacts.IsUnsafeToSuspend(Key, Name, facts.IsCritical);
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasSubtitle));
        OnPropertyChanged(nameof(IsCritical));
        OnPropertyChanged(nameof(CanSuspend));
        RebuildFacts();

        if (!_isAlive)
            return;

        if (priority is { } p)
            ShowPriority(p);

        if (affinity is { } mask)
        {
            Processors.Clear();
            for (var i = 0; i < 64; i++)
            {
                if ((mask.System & (1UL << i)) != 0)
                    Processors.Add(new ProcessorToggle(i, (mask.Process & (1UL << i)) != 0, ProcessorChanged));
            }

            ShowAffinity(mask.Process);
            OnPropertyChanged(nameof(HasAffinity));
        }
    }

    internal void ShowPriority(uint priorityClass)
    {
        _priority = PriorityOptions.FirstOrDefault(o => o.Value == priorityClass);
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(HasPriority));
    }

    internal void ShowAffinity(ulong mask)
    {
        _affinity = mask;
        foreach (var processor in Processors)
            processor.Show((mask & (1UL << processor.Index)) != 0);

        var count = BitOperations.PopCount(mask);
        AffinityText = count == Processors.Count
            ? $"All {Format.Number(count)} processors"
            : $"{Format.Number(count)} of {Format.Number(Processors.Count)} processors";
    }

    internal void ShowNotice(string text, bool isError, bool offerElevation = false, (string Text, ICommand Command)? action = null)
    {
        Notice = text;
        NoticeIsError = isError;
        OfferElevation = offerElevation && !Elevation.IsElevated;
        SetNoticeAction(action);
    }

    internal void ClearNotice()
    {
        Notice = null;
        OfferElevation = false;
        SetNoticeAction(null);
    }

    private void SetNoticeAction((string Text, ICommand Command)? action)
    {
        _noticeAction = action;
        OnPropertyChanged(nameof(NoticeActionText));
        OnPropertyChanged(nameof(NoticeActionCommand));
        OnPropertyChanged(nameof(HasNoticeAction));
    }

    internal void SetParent(string? parent, ProcessKey? parentKey)
    {
        if (_parent == parent && _parentKey == parentKey)
            return;
        _parent = parent;
        _parentKey = parentKey;
        RebuildFacts();
    }

    private void StatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsRunningStatus));
        OnPropertyChanged(nameof(SuspendText));
        OnPropertyChanged(nameof(SuspendIcon));
        OnPropertyChanged(nameof(CanSuspend));
    }

    private void ProcessorChanged(ProcessorToggle processor)
    {
        var mask = processor.IsChecked ? _affinity | (1UL << processor.Index) : _affinity & ~(1UL << processor.Index);
        if (mask == 0)
        {
            processor.Show(true);
            ShowNotice("A process needs at least one processor.", isError: false);
            return;
        }

        _owner.SetAffinity(this, mask);
    }

    private void SelectAllProcessors()
    {
        var all = 0UL;
        foreach (var processor in Processors)
            all |= 1UL << processor.Index;
        if (all != _affinity)
            _owner.SetAffinity(this, all);
    }

    private void RebuildFacts()
    {
        var culture = CultureInfo.CurrentCulture;
        var started = DateTime.FromFileTimeUtc(Key.CreateTime).ToLocalTime();
        var copy = (Func<string, ICommand>)(text => new RelayCommand(() => _owner.Copy(text)));

        Facts.Clear();
        if (Path is not null)
            Facts.Add(new Fact("Path", Path, copy(Path), isLong: true));
        if (_facts?.CommandLine is { } commandLine)
            Facts.Add(new Fact("Command line", commandLine, copy(commandLine), isLong: true));
        if (_parent is not null)
        {
            var open = _parentKey is { } parentKey && _owner.IsRunning(parentKey)
                ? new RelayCommand(() => _owner.Select(parentKey))
                : null;
            Facts.Add(new Fact("Started by", _parent, open: open));
        }

        Facts.Add(new Fact("Started", started.ToString("G", culture)));
        if (_ended is not null)
        {
            Facts.Add(new Fact("Ended", _ended.Ended.ToString("G", culture)));
            Facts.Add(new Fact("Ran for", _ended.LifetimeText));
            Facts.Add(new Fact("Exit code", _ended.ExitCodeText));
            if (_ended.PeakCommit is { } peak)
                Facts.Add(new Fact("Peak commit", Format.Bytes(peak)));
        }
        else if (_isAlive)
        {
            Facts.Add(_runningFor!);
        }

        if (_facts?.User is { } user)
            Facts.Add(new Fact("User", _facts.IsElevated == true ? $"{user} (administrator)" : user));
        if (_sessionId >= 0)
            Facts.Add(new Fact("Session", _sessionId.ToString(culture)));

        if (_isAlive)
        {
            Facts.Add(_threads!);
            Facts.Add(_handles!);
            Facts.Add(_workingSet!);
            Facts.Add(_commit!);
            Facts.Add(_io!);
        }
    }
}
