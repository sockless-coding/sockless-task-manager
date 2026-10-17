using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

internal enum ProcessColumn
{
    Name,
    Id,
    Cpu,
    Memory,
    Io,
}

/// <summary>A sortable column header. Clicking the sort column reverses it.</summary>
public sealed class SortColumn : ObservableObject
{
    private bool _isActive;
    private bool _isDescending;

    internal SortColumn(ProcessColumn column, string title, Action<SortColumn> sort)
    {
        Column = column;
        Title = title;
        SortCommand = new RelayCommand(() => sort(this));
    }

    internal ProcessColumn Column { get; }

    /// <summary>Names and ids read naturally A→Z; amounts are most useful largest first.</summary>
    internal bool DescendingFirst => Column is not (ProcessColumn.Name or ProcessColumn.Id);

    public string Title { get; }

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

    internal void Show(bool active, bool descending)
    {
        IsActive = active;
        IsDescending = descending;
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(AccessibleName));
    }
}
