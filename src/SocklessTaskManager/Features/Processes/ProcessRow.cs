using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>Where a process sits in the list as currently shown.</summary>
/// <param name="IsContext">Shown only because something below it matches the search or filters.</param>
internal readonly record struct RowPlacement(int Depth, bool HasChildren, bool IsExpanded, bool IsContext);

/// <summary>
/// One cell of a row, for one of the columns shown after the name. Numeric cells only format
/// their text when the value changes: most rows of a long list don't change from tick to tick.
/// </summary>
public sealed class Cell : ObservableObject
{
    private string _text = string.Empty;
    private double _value = double.NaN;

    internal Cell(TableColumn column) => Column = column;

    public TableColumn Column { get; }

    public string Text
    {
        get => _text;
        private set
        {
            if (Set(ref _text, value) && !Column.IsNumeric)
                OnPropertyChanged(nameof(ToolTip));
        }
    }

    /// <summary>Text columns are trimmed to their width, so the whole value is a hover away.</summary>
    public string? ToolTip => Column.Column == ProcessColumn.User && _text == TableColumn.UnknownUser
        ? Elevation.IsElevated ? "Windows doesn't say who runs this process" : "Another user's or a system process: run as administrator to see who runs it"
        : Column.IsNumeric || _text.Length == 0 ? null : _text;

    /// <returns>Whether the text changed.</returns>
    internal bool Update(ProcessReading reading)
    {
        if (Column.SortsByNumber)
        {
            var value = Column.Number(reading);
            if (value == _value)
                return false;
            _value = value;
        }

        var before = _text;
        Text = Column.Text(reading);
        return !ReferenceEquals(before, _text);
    }
}

/// <summary>One row of the process table, updated in place every tick. Selecting it opens the details pane.</summary>
public sealed class ProcessRow : ObservableObject
{
    /// <summary>Indent per tree level, in pixels.</summary>
    private const double IndentStep = 20;

    private string _name = string.Empty;
    private string _toolTip = string.Empty;
    private string? _path;
    private double _indent;
    private bool _showExpander;
    private bool _hasChildren;
    private bool _isExpanded;
    private bool _isContext;
    private bool _isFollowed;
    private bool _isSelected;
    private bool _isSuspended;

    internal ProcessRow(ProcessKey key, IReadOnlyList<TableColumn> columns, ProcessList owner)
    {
        SetColumns(columns);
        Key = key;
        IdText = key.Id.ToString(CultureInfo.CurrentCulture);
        SelectCommand = new RelayCommand(() => owner.Select(Key));
        FollowCommand = new RelayCommand(() => owner.Follow(Key));
        ToggleCommand = new RelayCommand(() => owner.Toggle(Key));
        EndCommand = new RelayCommand(() => owner.RequestEnd(Key, tree: false));
        EndTreeCommand = new RelayCommand(() => owner.RequestEnd(Key, tree: true));
        SuspendCommand = new RelayCommand(() => owner.SuspendOrResume(Key));
        OpenLocationCommand = new RelayCommand(() => owner.OpenLocation(_path));
        DumpCommand = new RelayCommand(() => owner.CreateDump(Key));
    }

    internal ProcessKey Key { get; }

    public string IdText { get; }

    public ICommand SelectCommand { get; }

    public ICommand FollowCommand { get; }

    public ICommand ToggleCommand { get; }

    public ICommand EndCommand { get; }

    public ICommand EndTreeCommand { get; }

    public ICommand SuspendCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand DumpCommand { get; }

    /// <summary>The executable's folder can only be opened when its path could be read.</summary>
    public bool HasPath => _path is not null;

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

    /// <summary>The shown columns after the name, in order.</summary>
    public ObservableCollection<Cell> Cells { get; } = [];

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
                OnPropertyChanged(nameof(FollowText));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string FollowName => _isFollowed ? $"Stop following {_name}" : $"Follow {_name}";

    public string FollowText => _isFollowed ? "Stop following" : "Follow";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>Every thread is suspended. Shown as a badge with the word, not by colour alone.</summary>
    public bool IsSuspended
    {
        get => _isSuspended;
        private set
        {
            if (Set(ref _isSuspended, value))
            {
                OnPropertyChanged(nameof(SuspendText));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string SuspendText => _isSuspended ? "Resume" : "Suspend";

    public string AccessibleName =>
        $"{_name}, {string.Join(", ", Cells.Select(c => $"{c.Column.Title} {c.Text}"))}{(_isSuspended ? ", suspended" : string.Empty)}{(_isFollowed ? ", followed" : string.Empty)}";

    /// <summary>The shown columns changed: one cell per column, filled on the next update.</summary>
    internal void SetColumns(IReadOnlyList<TableColumn> columns)
    {
        Cells.Clear();
        foreach (var column in columns)
            Cells.Add(new Cell(column));
    }

    internal void Update(ProcessReading reading, RowPlacement placement, bool showTree, bool isFollowed, bool isSelected)
    {
        Name = reading.Name;
        ToolTip = reading.Path ?? reading.Name;
        if (_path != reading.Path)
        {
            _path = reading.Path;
            OnPropertyChanged(nameof(HasPath));
        }

        var changed = false;
        foreach (var cell in Cells)
            changed |= cell.Update(reading);
        if (changed)
            OnPropertyChanged(nameof(AccessibleName));

        Indent = placement.Depth * IndentStep;
        ShowExpander = showTree;
        HasChildren = placement.HasChildren;
        IsExpanded = placement.IsExpanded;
        IsContext = placement.IsContext;
        IsFollowed = isFollowed;
        IsSelected = isSelected;
        IsSuspended = reading.IsSuspended;
    }

    /// <summary>One decimal, like Task Manager's process list; idle reads as a plain 0%.</summary>
    internal static string CpuPercent(double value) =>
        value < 0.05 ? "0%" : $"{value.ToString("F1", CultureInfo.CurrentCulture)}%";
}
