using System.Windows.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Services;

internal enum ServiceSortKey
{
    Name,
    Status,
    Startup,
    Account,
    ProcessId,
    Publisher,
}

/// <summary>A sortable column title.</summary>
public sealed class ServiceColumn : ObservableObject
{
    private bool _isActive;
    private bool _isDescending;

    internal ServiceColumn(string title, ServiceSortKey key, Action<ServiceColumn> sort, bool alignRight = false)
    {
        Title = title;
        Key = key;
        Alignment = alignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        SortCommand = new RelayCommand(() => sort(this));
    }

    public string Title { get; }

    internal ServiceSortKey Key { get; }

    public HorizontalAlignment Alignment { get; }

    public ICommand SortCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (Set(ref _isActive, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsDescending
    {
        get => _isDescending;
        set
        {
            if (Set(ref _isDescending, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public Geometry? Glyph => ThemeResource.Geometry(_isDescending ? "IconChevronDown" : "IconChevronUp");

    public string AccessibleName => _isActive
        ? $"{Title}, sorted {(_isDescending ? "descending" : "ascending")}"
        : $"Sort by {Title}";
}

/// <summary>A publisher's heading in the grouped table: its name, how many services, and how many run. Folds its services away.</summary>
public sealed class ServiceGroupRow : ObservableObject
{
    private string _countText = string.Empty;
    private bool _isExpanded;

    internal ServiceGroupRow(string name, bool isExpanded, Action<ServiceGroupRow> toggle)
    {
        Name = name;
        _isExpanded = isExpanded;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public string Name { get; }

    /// <summary>The programs these services name aren't on disk.</summary>
    public bool IsMissing => Name == Publisher.MissingName;

    public ICommand ToggleCommand { get; }

    public string CountText
    {
        get => _countText;
        set
        {
            if (Set(ref _countText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public Geometry? Glyph => ThemeResource.Geometry(_isExpanded ? "IconChevronDown" : "IconChevronRight");

    public string AccessibleName => $"{Name}, {_countText}, {(_isExpanded ? "expanded" : "collapsed")}";
}

/// <summary>One service in the table. Kept while the service exists and updated in place.</summary>
public sealed class ServiceRow : ObservableObject
{
    private string _displayName = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSuccess;
    private bool _isWarning;
    private string _startupText = string.Empty;
    private string _accountText = string.Empty;
    private string _processIdText = string.Empty;
    private string _publisherText = string.Empty;
    private string _toolTip = string.Empty;
    private bool _isSelected;
    private bool _canStart;
    private bool _canStop;
    private bool _canPause;
    private bool _canContinue;

    internal ServiceRow(string name, ServiceActions actions)
    {
        Name = name;
        SelectCommand = new RelayCommand(() => actions.Select(this));
        StartCommand = new RelayCommand(() => actions.Start(this));
        StopCommand = new RelayCommand(() => actions.Stop(this));
        RestartCommand = new RelayCommand(() => actions.Restart(this));
        PauseCommand = new RelayCommand(() => actions.Pause(this));
        ContinueCommand = new RelayCommand(() => actions.Continue(this));
        EditCommand = new RelayCommand(() => actions.Edit(this));
        UninstallCommand = new RelayCommand(() => actions.Uninstall(this));
        CopyNameCommand = new RelayCommand(() => actions.CopyName(this));
    }

    /// <summary>The service's key name, which never changes.</summary>
    public string Name { get; }

    internal ServiceEntry? Entry { get; set; }

    public ICommand SelectCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand PauseCommand { get; }

    public ICommand ContinueCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand CopyNameCommand { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>A word, always: Running, Stopped, Paused, Starting…</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (Set(ref _statusText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        set => Set(ref _isSuccess, value);
    }

    /// <summary>Paused, or on its way somewhere.</summary>
    public bool IsWarning
    {
        get => _isWarning;
        set => Set(ref _isWarning, value);
    }

    public string StartupText
    {
        get => _startupText;
        set => Set(ref _startupText, value);
    }

    public string AccountText
    {
        get => _accountText;
        set => Set(ref _accountText, value);
    }

    public string ProcessIdText
    {
        get => _processIdText;
        set => Set(ref _processIdText, value);
    }

    public string PublisherText
    {
        get => _publisherText;
        set => Set(ref _publisherText, value);
    }

    /// <summary>The description, which is too long for a column.</summary>
    public string ToolTip
    {
        get => _toolTip;
        set => Set(ref _toolTip, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool CanStart
    {
        get => _canStart;
        set => Set(ref _canStart, value);
    }

    public bool CanStop
    {
        get => _canStop;
        set => Set(ref _canStop, value);
    }

    public bool CanPause
    {
        get => _canPause;
        set => Set(ref _canPause, value);
    }

    public bool CanContinue
    {
        get => _canContinue;
        set => Set(ref _canContinue, value);
    }

    public string AccessibleName => $"{_displayName}, {_statusText}";
}

/// <summary>What a row's commands do; the list implements them.</summary>
internal sealed record ServiceActions(
    Action<ServiceRow> Select,
    Action<ServiceRow> Start,
    Action<ServiceRow> Stop,
    Action<ServiceRow> Restart,
    Action<ServiceRow> Pause,
    Action<ServiceRow> Continue,
    Action<ServiceRow> Edit,
    Action<ServiceRow> Uninstall,
    Action<ServiceRow> CopyName);
