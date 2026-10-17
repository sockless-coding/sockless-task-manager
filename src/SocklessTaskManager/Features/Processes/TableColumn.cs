using System.Globalization;
using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

internal enum ProcessColumn
{
    Name,
    Id,
    User,
    Cpu,
    Memory,
    Io,
    Threads,
    Handles,
    WorkingSet,
    Commit,
    Priority,
    Session,
    Started,
    Description,
    Path,
    CommandLine,
}

/// <summary>How a column sorts, filters and aligns.</summary>
internal enum ColumnKind
{
    /// <summary>Sorted and filtered as text, left-aligned.</summary>
    Text,

    /// <summary>An identifier: a plain number in the filter means exactly that number.</summary>
    Id,

    /// <summary>A count or level: a plain number in the filter means at least that.</summary>
    Count,

    Percent,

    /// <summary>Filtered in MB unless another unit is given.</summary>
    Bytes,

    BytesPerSecond,

    /// <summary>Sorted by an underlying value, filtered by the text shown: priority names ("High"), start times ("18:42").</summary>
    Label,
}

/// <summary>
/// One column of the process table: what it shows and how wide, whether it's shown, how it sorts,
/// and the filter typed for it. Clicking the sort column reverses it.
/// </summary>
public sealed class TableColumn : ObservableObject
{
    private readonly ProcessList _owner;
    private bool _isVisible;
    private bool _isActive;
    private bool _isDescending;
    private string _filterText = string.Empty;
    private bool _isFilterInvalid;

    internal TableColumn(ProcessColumn column, string title, double width, ColumnKind kind, bool isVisible, string? hint, ProcessList owner)
    {
        Column = column;
        Title = title;
        Width = width;
        Kind = kind;
        FilterHint = hint ?? (kind is ColumnKind.Text or ColumnKind.Label ? "Contains" : "e.g. > 1");
        _isVisible = isVisible;
        _owner = owner;
        SortCommand = new RelayCommand(() => owner.SortBy(this));
        ClearFilterCommand = new RelayCommand(() => FilterText = string.Empty);
    }

    internal ProcessColumn Column { get; }

    internal ColumnKind Kind { get; }

    internal ColumnFilter? Filter { get; private set; }

    /// <summary>Names and text read naturally A→Z, ids from the lowest; amounts are most useful largest first.</summary>
    internal bool DescendingFirst => Kind is not (ColumnKind.Text or ColumnKind.Id);

    /// <summary>Sorted by value rather than by the text shown.</summary>
    internal bool SortsByNumber => Kind != ColumnKind.Text;

    public string Title { get; }

    /// <summary>Fixed width in pixels; the Name column takes what's left.</summary>
    public double Width { get; }

    public bool IsNumeric => Kind is not (ColumnKind.Text or ColumnKind.Label);

    public HorizontalAlignment Alignment => IsNumeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public TextAlignment TextAlignment => IsNumeric ? TextAlignment.Right : TextAlignment.Left;

    /// <summary>Name and PID identify a row, so they can't be hidden.</summary>
    public bool CanHide => Column is not (ProcessColumn.Name or ProcessColumn.Id);

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!CanHide || !Set(ref _isVisible, value))
                return;

            // A hidden column's filter would hide rows for no visible reason.
            if (!value)
                FilterText = string.Empty;
            _owner.ColumnsChanged();
        }
    }

    public ICommand SortCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        private set => Set(ref _isActive, value);
    }

    public bool IsDescending
    {
        get => _isDescending;
        private set => Set(ref _isDescending, value);
    }

    public Geometry? Glyph => ThemeResource.Geometry(_isDescending ? "IconChevronDown" : "IconChevronUp");

    public string AccessibleName => _isActive
        ? $"{Title}, sorted {(_isDescending ? "descending" : "ascending")}"
        : $"Sort by {Title}";

    /// <summary>What the user typed in this column's filter box.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            value ??= string.Empty;
            if (!Set(ref _filterText, value))
                return;

            try
            {
                Filter = ColumnFilter.Parse(value, Kind);
                IsFilterInvalid = false;
            }
            catch (FormatException)
            {
                // Ignored until it reads as a filter, and marked so the user can see why.
                Filter = null;
                IsFilterInvalid = true;
            }

            OnPropertyChanged(nameof(HasFilter));
            _owner.FiltersChanged();
        }
    }

    public bool HasFilter => Filter is not null;

    public bool IsFilterInvalid
    {
        get => _isFilterInvalid;
        private set => Set(ref _isFilterInvalid, value);
    }

    public string FilterHint { get; }

    public string FilterName => $"Filter {Title}";

    public string FilterHelp => Kind switch
    {
        ColumnKind.Text or ColumnKind.Label => "Text it contains. =exact, !not, a|b for either",
        ColumnKind.Id => "A number, or > < >= <= and ranges like 100-200",
        ColumnKind.Bytes => "> 100 MB, < 1 GB, 10-50 MB (MB if no unit)",
        ColumnKind.BytesPerSecond => "> 1 MB/s, < 100 KB/s (MB/s if no unit)",
        _ => "> 5, < 1, 10-50, or a number for at least that",
    };

    public ICommand ClearFilterCommand { get; }

    internal void ShowSort(bool active, bool descending)
    {
        IsActive = active;
        IsDescending = descending;
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(AccessibleName));
    }

    /// <summary>Shown when the owner can't be read: without elevation, any other user's or system process.</summary>
    internal const string UnknownUser = "—";

    /// <summary>The value sorted on and filtered by number: bytes, percent, counts, FILETIME.</summary>
    internal double Number(ProcessReading p) => Column switch
    {
        ProcessColumn.Id => p.Key.Id,
        ProcessColumn.Cpu => p.Cpu,
        ProcessColumn.Memory => p.Memory,
        ProcessColumn.Io => p.IoRate,
        ProcessColumn.Threads => p.Threads,
        ProcessColumn.Handles => p.Handles,
        ProcessColumn.WorkingSet => p.WorkingSet,
        ProcessColumn.Commit => p.Commit,
        ProcessColumn.Priority => p.BasePriority,
        ProcessColumn.Session => p.SessionId,
        ProcessColumn.Started => p.Key.CreateTime,
        _ => 0,
    };

    /// <summary>The cell's text.</summary>
    internal string Text(ProcessReading p) => Column switch
    {
        ProcessColumn.Name => p.Name,
        ProcessColumn.Id => p.Key.Id.ToString(CultureInfo.CurrentCulture),
        ProcessColumn.User => p.User ?? UnknownUser,
        ProcessColumn.Cpu => ProcessRow.CpuPercent(p.Cpu),
        ProcessColumn.Memory => Format.Bytes(p.Memory),
        ProcessColumn.Io => Format.BytesPerSecond(p.IoRate),
        ProcessColumn.Threads => Format.Number(p.Threads),
        ProcessColumn.Handles => Format.Number(p.Handles),
        ProcessColumn.WorkingSet => Format.Bytes(p.WorkingSet),
        ProcessColumn.Commit => Format.Bytes(p.Commit),
        ProcessColumn.Priority => PriorityName(p.BasePriority),
        ProcessColumn.Session => p.SessionId.ToString(CultureInfo.CurrentCulture),
        ProcessColumn.Started => Started(p.Key.CreateTime),
        ProcessColumn.Description => p.Description ?? string.Empty,
        ProcessColumn.Path => p.Path ?? string.Empty,
        ProcessColumn.CommandLine => p.CommandLine ?? string.Empty,
        _ => string.Empty,
    };

    /// <summary>What a text filter matches: the text shown, but an unknown user matches nothing.</summary>
    internal string MatchText(ProcessReading p) => Column == ProcessColumn.User ? p.User ?? string.Empty : Text(p);

    /// <summary>Text columns sort by the text; an unknown value sorts last either way.</summary>
    internal string SortText(ProcessReading p) => Column switch
    {
        ProcessColumn.Name => p.Name,
        ProcessColumn.User => p.User ?? string.Empty,
        ProcessColumn.Description => p.Description ?? string.Empty,
        ProcessColumn.Path => p.Path ?? string.Empty,
        ProcessColumn.CommandLine => p.CommandLine ?? string.Empty,
        _ => Text(p),
    };

    /// <summary>
    /// The priority class names Task Manager uses, from the base priority. A class's base can sit
    /// a step off its usual value (a Normal process reads 9 while in the foreground), hence ranges.
    /// </summary>
    internal static string PriorityName(int basePriority) => basePriority switch
    {
        <= 4 => "Low",
        <= 6 => "Below normal",
        <= 9 => "Normal",
        <= 12 => "Above normal",
        <= 23 => "High",
        _ => "Real-time",
    };

    /// <summary>Time only for today, date and time before that.</summary>
    private static string Started(long fileTime)
    {
        var started = DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        return started.Date == DateTime.Today ? started.ToString("T", culture) : started.ToString("g", culture);
    }
}
