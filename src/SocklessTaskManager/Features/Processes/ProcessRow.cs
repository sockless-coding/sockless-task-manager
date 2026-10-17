using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>Where a process sits in the list as currently shown.</summary>
/// <param name="IsContext">Shown only because something below it matches the search or filters.</param>
internal readonly record struct RowPlacement(int Depth, bool HasChildren, bool IsExpanded, bool IsContext);

/// <summary>One row of the process table, updated in place every tick.</summary>
public sealed class ProcessRow : ObservableObject
{
    /// <summary>Indent per tree level, in pixels.</summary>
    private const double IndentStep = 20;

    private string _name = string.Empty;
    private string _toolTip = string.Empty;
    private string _cpuText = string.Empty;
    private string _memoryText = string.Empty;
    private string _ioText = string.Empty;
    private double _indent;
    private bool _showExpander;
    private bool _hasChildren;
    private bool _isExpanded;
    private bool _isContext;
    private bool _isFollowed;
    private double _cpu = double.NaN;
    private ulong _memory = ulong.MaxValue;
    private double _io = double.NaN;

    internal ProcessRow(ProcessKey key, Action<ProcessKey> follow, Action<ProcessKey> toggle)
    {
        Key = key;
        IdText = key.Id.ToString(CultureInfo.CurrentCulture);
        FollowCommand = new RelayCommand(() => follow(Key));
        ToggleCommand = new RelayCommand(() => toggle(Key));
    }

    internal ProcessKey Key { get; }

    public string IdText { get; }

    public ICommand FollowCommand { get; }

    public ICommand ToggleCommand { get; }

    public string Name
    {
        get => _name;
        private set
        {
            if (Set(ref _name, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
                OnPropertyChanged(nameof(FollowName));
                OnPropertyChanged(nameof(ExpanderName));
            }
        }
    }

    /// <summary>The executable path, or the name where the process can't be opened.</summary>
    public string ToolTip
    {
        get => _toolTip;
        private set => Set(ref _toolTip, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set
        {
            if (Set(ref _cpuText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string MemoryText
    {
        get => _memoryText;
        private set
        {
            if (Set(ref _memoryText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string IoText
    {
        get => _ioText;
        private set => Set(ref _ioText, value);
    }

    /// <summary>Left indent of the name: tree depth.</summary>
    public double Indent
    {
        get => _indent;
        private set => Set(ref _indent, value);
    }

    /// <summary>The tree is shown, so every row keeps a slot for the expander, used or not.</summary>
    public bool ShowExpander
    {
        get => _showExpander;
        private set => Set(ref _showExpander, value);
    }

    public bool HasChildren
    {
        get => _hasChildren;
        private set => Set(ref _hasChildren, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (Set(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpanderGlyph));
                OnPropertyChanged(nameof(ExpanderName));
            }
        }
    }

    public Geometry? ExpanderGlyph => ThemeResource.Geometry(_isExpanded ? "IconChevronDown" : "IconChevronRight");

    public string ExpanderName => _isExpanded ? $"Collapse {_name}" : $"Expand {_name}";

    /// <summary>Shown only as the parent of a match; drawn quieter than the matches.</summary>
    public bool IsContext
    {
        get => _isContext;
        private set => Set(ref _isContext, value);
    }

    public bool IsFollowed
    {
        get => _isFollowed;
        private set
        {
            if (Set(ref _isFollowed, value))
            {
                OnPropertyChanged(nameof(FollowName));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string FollowName => _isFollowed ? $"Stop following {_name}" : $"Follow {_name}";

    public string AccessibleName =>
        $"{_name}, process {IdText}, CPU {_cpuText}, memory {_memoryText}{(_isFollowed ? ", followed" : string.Empty)}";

    internal void Update(ProcessReading reading, RowPlacement placement, bool showTree, bool isFollowed)
    {
        Name = reading.Name;
        ToolTip = reading.Path ?? reading.Name;

        // Most rows of a long list don't change from one tick to the next.
        if (reading.Cpu != _cpu)
        {
            _cpu = reading.Cpu;
            CpuText = CpuPercent(reading.Cpu);
        }

        if (reading.Memory != _memory)
        {
            _memory = reading.Memory;
            MemoryText = Format.Bytes(reading.Memory);
        }

        if (reading.IoRate != _io)
        {
            _io = reading.IoRate;
            IoText = Format.BytesPerSecond(reading.IoRate);
        }

        Indent = placement.Depth * IndentStep;
        ShowExpander = showTree;
        HasChildren = placement.HasChildren;
        IsExpanded = placement.IsExpanded;
        IsContext = placement.IsContext;
        IsFollowed = isFollowed;
    }

    /// <summary>One decimal, like Task Manager's process list; idle reads as a plain 0%.</summary>
    internal static string CpuPercent(double value) =>
        value < 0.05 ? "0%" : $"{value.ToString("F1", CultureInfo.CurrentCulture)}%";
}
