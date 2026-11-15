using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Autostart;

internal enum AutostartSortKey
{
    Name,
    Status,
    Publisher,
    Command,
    Category,
}

/// <summary>A sortable column title.</summary>
public sealed class AutostartColumn : ObservableObject
{
    private bool _isActive;
    private bool _isDescending;

    internal AutostartColumn(string title, AutostartSortKey key, Action<AutostartColumn> sort)
    {
        Title = title;
        Key = key;
        SortCommand = new RelayCommand(() => sort(this));
    }

    public string Title { get; }

    internal AutostartSortKey Key { get; }

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

/// <summary>A category's heading in the grouped table: what it is, how many entries, how many to check. Folds its entries away.</summary>
public sealed class AutostartGroupRow : ObservableObject
{
    private string _countText = string.Empty;
    private bool _isExpanded;
    private bool _hasLook;

    internal AutostartGroupRow(AutostartCategory category, bool isExpanded, Action<AutostartGroupRow> toggle)
    {
        Category = category;
        Name = AutostartText.CategoryTitle(category);
        About = AutostartText.CategoryAbout(category);
        _isExpanded = isExpanded;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    internal AutostartCategory Category { get; }

    public string Name { get; }

    public string About { get; }

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

    /// <summary>Some of its entries are unsigned, don't verify, or point at nothing.</summary>
    public bool HasLook
    {
        get => _hasLook;
        set => Set(ref _hasLook, value);
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

/// <summary>One entry in the table. Kept while the entry exists and updated in place.</summary>
public sealed class AutostartRow : ObservableObject
{
    private string _name = string.Empty;
    private string _statusText = string.Empty;
    private bool _isEnabled;
    private string _publisherText = string.Empty;
    private string _lookText = string.Empty;
    private string _commandText = string.Empty;
    private string _categoryText = string.Empty;
    private string _toolTip = string.Empty;
    private bool _isSelected;
    private bool _canToggle;
    private bool _canRemove;
    private bool _hasFile;

    internal AutostartRow(string id, AutostartRowActions actions)
    {
        Id = id;
        SelectCommand = new RelayCommand(() => actions.Select(this));
        ToggleCommand = new RelayCommand(() => actions.Toggle(this));
        RemoveCommand = new RelayCommand(() => actions.Remove(this));
        OpenLocationCommand = new RelayCommand(() => actions.OpenLocation(this));
        CopyCommandCommand = new RelayCommand(() => actions.CopyCommand(this));
    }

    public string Id { get; }

    internal AutostartEntry? Entry { get; set; }

    public ICommand SelectCommand { get; }

    public ICommand ToggleCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand CopyCommandCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    /// <summary>A word, always: On or Off.</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (Set(ref _statusText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
                OnPropertyChanged(nameof(ToggleText));
        }
    }

    public string ToggleText => _isEnabled ? "Turn off" : "Turn on";

    public string PublisherText
    {
        get => _publisherText;
        set => Set(ref _publisherText, value);
    }

    /// <summary>"Unsigned", "Not found", "Doesn't verify"; empty when there's nothing to check.</summary>
    public string LookText
    {
        get => _lookText;
        set
        {
            if (Set(ref _lookText, value))
            {
                OnPropertyChanged(nameof(HasLook));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public bool HasLook => _lookText.Length > 0;

    public string CommandText
    {
        get => _commandText;
        set => Set(ref _commandText, value);
    }

    public string CategoryText
    {
        get => _categoryText;
        set => Set(ref _categoryText, value);
    }

    /// <summary>Where it's registered, and what runs.</summary>
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

    public bool CanToggle
    {
        get => _canToggle;
        set => Set(ref _canToggle, value);
    }

    public bool CanRemove
    {
        get => _canRemove;
        set => Set(ref _canRemove, value);
    }

    public bool HasFile
    {
        get => _hasFile;
        set => Set(ref _hasFile, value);
    }

    public string AccessibleName => HasLook ? $"{_name}, {_statusText}, {_lookText}" : $"{_name}, {_statusText}";
}

/// <summary>What a row's commands do; the list implements them.</summary>
internal sealed record AutostartRowActions(
    Action<AutostartRow> Select,
    Action<AutostartRow> Toggle,
    Action<AutostartRow> Remove,
    Action<AutostartRow> OpenLocation,
    Action<AutostartRow> CopyCommand);

/// <summary>What the details pane's commands do; the list implements them.</summary>
internal sealed record AutostartDetailActions(
    Action Close,
    Action Toggle,
    Action Remove,
    Action OpenLocation,
    Action OpenRegistry,
    Action CopyCommand,
    Action OpenBackups);

/// <summary>The details pane for the selected entry: what it is, where it's registered, what runs, who signed it.</summary>
public sealed class AutostartDetails : ObservableObject
{
    private string _name = string.Empty;
    private string _about = string.Empty;
    private string _statusText = string.Empty;
    private bool _isEnabled;
    private string _lookText = string.Empty;
    private string _lookExplanation = string.Empty;
    private IReadOnlyList<AutostartFact> _facts = [];
    private IReadOnlyList<AutostartFact> _fileFacts = [];
    private bool _canToggle;
    private bool _canRemove;
    private bool _hasFile;
    private bool _hasRegistry;
    private string _locked = string.Empty;
    private bool _needsElevation;
    private bool _isBusy;
    private string _message = string.Empty;
    private bool _messageIsError;
    private bool _hasBackup;

    internal AutostartDetails(string id, AutostartDetailActions actions)
    {
        Id = id;
        CloseCommand = new RelayCommand(actions.Close);
        ToggleCommand = new RelayCommand(actions.Toggle);
        RemoveCommand = new RelayCommand(actions.Remove);
        OpenLocationCommand = new RelayCommand(actions.OpenLocation);
        OpenRegistryCommand = new RelayCommand(actions.OpenRegistry);
        CopyCommandCommand = new RelayCommand(actions.CopyCommand);
        OpenBackupsCommand = new RelayCommand(actions.OpenBackups);
    }

    public string Id { get; }

    public ICommand CloseCommand { get; }

    public ICommand ToggleCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand OpenRegistryCommand { get; }

    public ICommand CopyCommandCommand { get; }

    public ICommand OpenBackupsCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>What this kind of entry does.</summary>
    public string About
    {
        get => _about;
        set => Set(ref _about, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(ToggleTip));
            }
        }
    }

    public string ToggleText => _isEnabled ? "Turn off" : "Turn on";

    public string ToggleTip => _isEnabled ? "Keep it from starting; it stays listed so it can be turned on again" : "Let it start again";

    public string LookText
    {
        get => _lookText;
        set
        {
            if (Set(ref _lookText, value))
                OnPropertyChanged(nameof(HasLook));
        }
    }

    public bool HasLook => _lookText.Length > 0;

    /// <summary>Why it's worth a look, in a sentence.</summary>
    public string LookExplanation
    {
        get => _lookExplanation;
        set => Set(ref _lookExplanation, value);
    }

    public IReadOnlyList<AutostartFact> Facts
    {
        get => _facts;
        set
        {
            if (!_facts.SequenceEqual(value))
            {
                _facts = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The file: path, publisher, signature, version.</summary>
    public IReadOnlyList<AutostartFact> FileFacts
    {
        get => _fileFacts;
        set
        {
            if (!_fileFacts.SequenceEqual(value))
            {
                _fileFacts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFileFacts));
            }
        }
    }

    public bool HasFileFacts => _fileFacts.Count > 0;

    public bool CanToggle
    {
        get => _canToggle;
        set => Set(ref _canToggle, value);
    }

    public bool CanRemove
    {
        get => _canRemove;
        set => Set(ref _canRemove, value);
    }

    /// <summary>The file exists, so its folder can be opened.</summary>
    public bool HasFile
    {
        get => _hasFile;
        set => Set(ref _hasFile, value);
    }

    public bool HasRegistry
    {
        get => _hasRegistry;
        set => Set(ref _hasRegistry, value);
    }

    /// <summary>Why it can't be turned off or removed here.</summary>
    public string Locked
    {
        get => _locked;
        set
        {
            if (Set(ref _locked, value))
                OnPropertyChanged(nameof(IsLocked));
        }
    }

    public bool IsLocked => _locked.Length > 0;

    /// <summary>Changing it needs administrator rights, which this process doesn't have.</summary>
    public bool NeedsElevation
    {
        get => _needsElevation;
        set => Set(ref _needsElevation, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isBusy;

    public string Message
    {
        get => _message;
        set
        {
            if (Set(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    public bool MessageIsError
    {
        get => _messageIsError;
        set => Set(ref _messageIsError, value);
    }

    /// <summary>The last removal saved a backup: offer its folder.</summary>
    public bool HasBackup
    {
        get => _hasBackup;
        set => Set(ref _hasBackup, value);
    }
}
