using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Apps;

internal enum AppSortKey
{
    Name,
    Publisher,
    Version,
    Installed,
    Size,
    InstalledWith,
}

/// <summary>A sortable column title.</summary>
public sealed class AppColumn : ObservableObject
{
    private bool _isActive;
    private bool _isDescending;

    internal AppColumn(string title, AppSortKey key, Action<AppColumn> sort)
    {
        Title = title;
        Key = key;
        SortCommand = new RelayCommand(() => sort(this));
    }

    public string Title { get; }

    internal AppSortKey Key { get; }

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

/// <summary>A group's heading in the grouped table: how it was installed, how many, how many to check. Folds its apps away.</summary>
public sealed class AppGroupRow : ObservableObject
{
    private string _countText = string.Empty;
    private bool _isExpanded;
    private bool _hasProblem;

    internal AppGroupRow(AppGroup group, bool isExpanded, Action<AppGroupRow> toggle)
    {
        Group = group;
        Name = AppText.GroupTitle(group);
        About = AppText.GroupAbout(group);
        _isExpanded = isExpanded;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    internal AppGroup Group { get; }

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

    public bool HasProblem
    {
        get => _hasProblem;
        set => Set(ref _hasProblem, value);
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

/// <summary>One app in the table. Kept while the app is installed and updated in place.</summary>
public sealed class AppRow : ObservableObject
{
    private string _name = string.Empty;
    private string _publisherText = string.Empty;
    private string _versionText = string.Empty;
    private string _installedText = string.Empty;
    private string _sizeText = string.Empty;
    private string _installedWithText = string.Empty;
    private string _problemText = string.Empty;
    private string _toolTip = string.Empty;
    private string _uninstallText = "Uninstall";
    private bool _isSelected;
    private bool _canUninstall;
    private bool _canModify;
    private bool _canRepair;
    private bool _hasLocation;

    internal AppRow(string id, AppRowActions actions)
    {
        Id = id;
        SelectCommand = new RelayCommand(() => actions.Select(this));
        UninstallCommand = new RelayCommand(() => actions.Uninstall(this));
        ModifyCommand = new RelayCommand(() => actions.Modify(this));
        RepairCommand = new RelayCommand(() => actions.Repair(this));
        OpenLocationCommand = new RelayCommand(() => actions.OpenLocation(this));
        CopyNameCommand = new RelayCommand(() => actions.CopyName(this));
    }

    public string Id { get; }

    internal AppItem? Item { get; set; }

    public ICommand SelectCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand ModifyCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand CopyNameCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string PublisherText
    {
        get => _publisherText;
        set => Set(ref _publisherText, value);
    }

    public string VersionText
    {
        get => _versionText;
        set => Set(ref _versionText, value);
    }

    public string InstalledText
    {
        get => _installedText;
        set => Set(ref _installedText, value);
    }

    public string SizeText
    {
        get => _sizeText;
        set => Set(ref _sizeText, value);
    }

    public string InstalledWithText
    {
        get => _installedWithText;
        set => Set(ref _installedWithText, value);
    }

    /// <summary>"Uninstaller not found", "Needs repair"; empty when nothing stands out.</summary>
    public string ProblemText
    {
        get => _problemText;
        set
        {
            if (Set(ref _problemText, value))
            {
                OnPropertyChanged(nameof(HasProblem));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public bool HasProblem => _problemText.Length > 0;

    public string ToolTip
    {
        get => _toolTip;
        set => Set(ref _toolTip, value);
    }

    /// <summary>The menu's uninstall item: "Uninstall…", "Remove…", "Turn off…".</summary>
    public string UninstallText
    {
        get => _uninstallText;
        set => Set(ref _uninstallText, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool CanUninstall
    {
        get => _canUninstall;
        set => Set(ref _canUninstall, value);
    }

    public bool CanModify
    {
        get => _canModify;
        set => Set(ref _canModify, value);
    }

    public bool CanRepair
    {
        get => _canRepair;
        set => Set(ref _canRepair, value);
    }

    public bool HasLocation
    {
        get => _hasLocation;
        set => Set(ref _hasLocation, value);
    }

    public string AccessibleName => HasProblem ? $"{_name}, {_problemText}" : _name;
}

/// <summary>What a row's commands do; the list implements them.</summary>
internal sealed record AppRowActions(
    Action<AppRow> Select,
    Action<AppRow> Uninstall,
    Action<AppRow> Modify,
    Action<AppRow> Repair,
    Action<AppRow> OpenLocation,
    Action<AppRow> CopyName);

/// <summary>What the details pane's commands do; the list implements them.</summary>
internal sealed record AppDetailActions(
    Action Close,
    Action Uninstall,
    Action Modify,
    Action Repair,
    Action RemoveEntry,
    Action OpenLocation,
    Action ShowProcesses,
    Action OpenWebsite,
    Action OpenHelp,
    Action OpenRegistry,
    Action CopyCommand,
    Action OpenBackups);

/// <summary>The details pane for the selected app: what it is, how it was installed, where it lives, and how to remove it.</summary>
public sealed class AppDetails : ObservableObject
{
    private string _name = string.Empty;
    private string _subtitle = string.Empty;
    private string _about = string.Empty;
    private string _problemText = string.Empty;
    private string _problemExplanation = string.Empty;
    private IReadOnlyList<AppFact> _facts = [];
    private string _uninstallText = "Uninstall";
    private bool _canUninstall;
    private bool _canModify;
    private bool _canRepair;
    private bool _canRemoveEntry;
    private bool _hasLocation;
    private bool _hasWebsite;
    private bool _hasHelp;
    private bool _hasRegistry;
    private bool _hasCommand;
    private string _locked = string.Empty;
    private bool _needsElevation;
    private string _runningText = string.Empty;
    private bool _isBusy;
    private string _message = string.Empty;
    private bool _messageIsError;

    internal AppDetails(string id, AppDetailActions actions)
    {
        Id = id;
        CloseCommand = new RelayCommand(actions.Close);
        UninstallCommand = new RelayCommand(actions.Uninstall);
        ModifyCommand = new RelayCommand(actions.Modify);
        RepairCommand = new RelayCommand(actions.Repair);
        RemoveEntryCommand = new RelayCommand(actions.RemoveEntry);
        OpenLocationCommand = new RelayCommand(actions.OpenLocation);
        ShowProcessesCommand = new RelayCommand(actions.ShowProcesses);
        OpenWebsiteCommand = new RelayCommand(actions.OpenWebsite);
        OpenHelpCommand = new RelayCommand(actions.OpenHelp);
        OpenRegistryCommand = new RelayCommand(actions.OpenRegistry);
        CopyCommandCommand = new RelayCommand(actions.CopyCommand);
        OpenBackupsCommand = new RelayCommand(actions.OpenBackups);
    }

    public string Id { get; }

    public ICommand CloseCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand ModifyCommand { get; }

    public ICommand RepairCommand { get; }

    public ICommand RemoveEntryCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand ShowProcessesCommand { get; }

    public ICommand OpenWebsiteCommand { get; }

    public ICommand OpenHelpCommand { get; }

    public ICommand OpenRegistryCommand { get; }

    public ICommand CopyCommandCommand { get; }

    public ICommand OpenBackupsCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Publisher and version, under the name.</summary>
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (Set(ref _subtitle, value))
                OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool HasSubtitle => _subtitle.Length > 0;

    public string About
    {
        get => _about;
        set => Set(ref _about, value);
    }

    public string ProblemText
    {
        get => _problemText;
        set
        {
            if (Set(ref _problemText, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problemText.Length > 0;

    public string ProblemExplanation
    {
        get => _problemExplanation;
        set => Set(ref _problemExplanation, value);
    }

    public IReadOnlyList<AppFact> Facts
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

    /// <summary>"Uninstall", "Remove", "Turn off".</summary>
    public string UninstallText
    {
        get => _uninstallText;
        set => Set(ref _uninstallText, value);
    }

    public bool CanUninstall
    {
        get => _canUninstall;
        set => Set(ref _canUninstall, value);
    }

    public bool CanModify
    {
        get => _canModify;
        set => Set(ref _canModify, value);
    }

    public bool CanRepair
    {
        get => _canRepair;
        set => Set(ref _canRepair, value);
    }

    /// <summary>What the entry names is gone: offer to remove the entry itself.</summary>
    public bool CanRemoveEntry
    {
        get => _canRemoveEntry;
        set => Set(ref _canRemoveEntry, value);
    }

    public bool HasLocation
    {
        get => _hasLocation;
        set => Set(ref _hasLocation, value);
    }

    public bool HasWebsite
    {
        get => _hasWebsite;
        set => Set(ref _hasWebsite, value);
    }

    public bool HasHelp
    {
        get => _hasHelp;
        set => Set(ref _hasHelp, value);
    }

    public bool HasRegistry
    {
        get => _hasRegistry;
        set => Set(ref _hasRegistry, value);
    }

    /// <summary>It's uninstalled by a command, which can be copied.</summary>
    public bool HasCommand
    {
        get => _hasCommand;
        set => Set(ref _hasCommand, value);
    }

    /// <summary>Why it can't be uninstalled here.</summary>
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

    /// <summary>Removing its entry needs administrator rights, which this process doesn't have.</summary>
    public bool NeedsElevation
    {
        get => _needsElevation;
        set => Set(ref _needsElevation, value);
    }

    /// <summary>"Running now: 3 processes"; empty when nothing runs from its folder.</summary>
    public string RunningText
    {
        get => _runningText;
        set
        {
            if (Set(ref _runningText, value))
                OnPropertyChanged(nameof(IsRunning));
        }
    }

    public bool IsRunning => _runningText.Length > 0;

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
}
