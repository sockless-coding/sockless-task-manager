using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Drivers;

/// <summary>A status word and its colour. The word is always shown; the colour only adds to it.</summary>
public sealed record Badge(string Text, BadgeKind Kind)
{
    public static readonly Badge None = new(string.Empty, BadgeKind.Neutral);

    public bool HasText => Text.Length > 0;

    public bool IsSuccess => Kind == BadgeKind.Success;

    public bool IsWarning => Kind == BadgeKind.Warning;

    public bool IsDanger => Kind == BadgeKind.Danger;

    public bool IsInfo => Kind == BadgeKind.Info;
}

internal enum DriverSortKey
{
    Name,
    Status,
    Driver,
    Version,
    Date,
    Signature,
    Description,
    Source,
}

/// <summary>A button that opens a vendor's update tool: "Open NVIDIA App".</summary>
public sealed class ToolLink
{
    internal ToolLink(string name, Action open)
    {
        Name = name;
        Text = $"Open {name}";
        OpenCommand = new RelayCommand(open);
    }

    public string Name { get; }

    public string Text { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>A sortable column title.</summary>
public sealed class DriverColumn : ObservableObject
{
    private bool _isActive;
    private bool _isDescending;

    internal DriverColumn(string title, DriverSortKey key, Action<DriverColumn> sort)
    {
        Title = title;
        Key = key;
        SortCommand = new RelayCommand(() => sort(this));
    }

    public string Title { get; }

    internal DriverSortKey Key { get; }

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

/// <summary>A row in one of the page's tables. Built when the data changes; only selection changes in place.</summary>
public abstract class DriverRow : ObservableObject
{
    private bool _isSelected;

    internal DriverRow(string id, Action<DriverRow> select)
    {
        Id = id;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Id { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public abstract string AccessibleName { get; }
}

/// <summary>A device class's heading in the grouped device table. Folds its devices away.</summary>
public sealed class DeviceGroupRow : ObservableObject
{
    private string _countText = string.Empty;
    private bool _isExpanded;
    private bool _hasProblems;

    internal DeviceGroupRow(string name, bool isExpanded, Action<DeviceGroupRow> toggle)
    {
        Name = name;
        _isExpanded = isExpanded;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public string Name { get; }

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

    public bool HasProblems
    {
        get => _hasProblems;
        set => Set(ref _hasProblems, value);
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

public sealed class DeviceRow : DriverRow
{
    internal DeviceRow(DeviceInfo device, Badge status, string driver, DriverOrigin? origin, DriverUpdate? update, Action<DriverRow> select)
        : base(device.InstanceId, select)
    {
        Device = device;
        Origin = origin;
        Update = update;
        SourceText = origin?.Title ?? (device.HasDriver ? "—" : "No driver");
        Name = device.Name;
        Status = status;
        DriverText = driver;
        VersionText = device.Version ?? "—";
        DateText = Drivers.DriverText.DriverDate(device.Date);
        ClassText = device.ClassTitle;
        ToolTip = $"{device.Name}\n{device.InstanceId}";
    }

    internal DeviceInfo Device { get; }

    internal DriverUpdate? Update { get; }

    internal DriverOrigin? Origin { get; }

    public string Name { get; }

    public Badge Status { get; }

    /// <summary>Where the driver came from: "Windows", "Windows Update", "NVIDIA Graphics Driver 610.88".</summary>
    public string SourceText { get; }

    /// <summary>Who provides the driver: "NVIDIA", "Microsoft (Windows)", or "No driver".</summary>
    public string DriverText { get; }

    public string VersionText { get; }

    public string DateText { get; }

    public string ClassText { get; }

    public string ToolTip { get; }

    public bool HasUpdate => Update is not null;

    public override string AccessibleName => HasUpdate ? $"{Name}, {Status.Text}, update available" : $"{Name}, {Status.Text}";
}

public sealed class ModuleRow : DriverRow
{
    internal ModuleRow(LoadedModule module, DriverOrigin? origin, Action<DriverRow> select)
        : base(module.Path, select)
    {
        Module = module;
        Origin = origin;
        SourceText = origin?.Title ?? "—";
        Name = module.Name;
        var file = module.File;
        Look = Drivers.DriverText.Look(file) is { Length: > 0 } look ? new Badge(look, BadgeKind.Warning) : Badge.None;
        SignatureText = file.Signature.State == Core.Interop.SignatureState.Signed
            ? file.IsWindows ? "Windows"
                : file.IsHardwareProgram ? $"Microsoft, for {file.Company ?? "another company"}"
                : file.Signature.Signer ?? "Signed"
            : Drivers.DriverText.SignatureText(file.Signature);
        DescriptionText = module.Service?.DisplayName ?? file.Description ?? "—";
        VersionText = file.Version ?? "—";
        ToolTip = module.Path;
    }

    internal LoadedModule Module { get; }

    internal DriverOrigin? Origin { get; }

    public string SourceText { get; }

    public string Name { get; }

    /// <summary>"Unsigned", "Doesn't verify", "Not found"; nothing when there's nothing to check.</summary>
    public Badge Look { get; }

    /// <summary>Who signed it ("Windows" for Windows' own).</summary>
    public string SignatureText { get; }

    public string DescriptionText { get; }

    public string VersionText { get; }

    public string ToolTip { get; }

    public override string AccessibleName => Look.HasText ? $"{Name}, {Look.Text}" : $"{Name}, signed by {SignatureText}";
}

public sealed class UpdateRow : DriverRow
{
    private Badge _status;

    internal UpdateRow(DriverUpdate update, DeviceInfo? device, Action<DriverRow> select)
        : base(update.UpdateId, select)
    {
        Update = update;
        Device = device;
        Title = update.Title;
        DeviceText = device?.Name ?? update.Model ?? update.HardwareId ?? "—";
        ProviderText = update.Provider ?? update.Manufacturer ?? "—";
        DateText = DriverText.DriverDate(update.Date);
        _status = Kind(update);
    }

    internal DriverUpdate Update { get; }

    internal DeviceInfo? Device { get; }

    public string Title { get; }

    public string DeviceText { get; }

    public string ProviderText { get; }

    public string DateText { get; }

    /// <summary>"Optional" or "Recommended", or what's happening to it now.</summary>
    public Badge Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public override string AccessibleName => $"{Title}, {_status.Text}";

    internal static Badge Kind(DriverUpdate update) => update.IsOptional ? new Badge("Optional", BadgeKind.Neutral) : new Badge("Recommended", BadgeKind.Info);
}

public sealed class ChangeRow : DriverRow
{
    internal ChangeRow(DriverChange change, string subject, Action<DriverRow> select)
        : base(change.Id, select)
    {
        Change = change;
        TimeText = DriverText.Time(change.Time);
        Kind = new Badge(DriverText.ChangeTitle(change.Kind), DriverText.ChangeBadge(change.Kind));
        Subject = subject;
        Detail = change.Detail;
    }

    internal DriverChange Change { get; }

    public string TimeText { get; }

    public Badge Kind { get; }

    public string Subject { get; }

    public string Detail { get; }

    public override string AccessibleName => $"{TimeText}, {Kind.Text}, {Subject}";
}

public sealed class CrashRow : DriverRow
{
    internal CrashRow(DriverCrash crash, Action<DriverRow> select)
        : base(crash.Id, select)
    {
        Crash = crash;
        TimeText = DriverText.Time(crash.Time);
        Kind = new Badge(DriverText.CrashTitle(crash.Kind), DriverText.CrashBadge(crash.Kind));
        DriverName = crash.Driver ?? (crash.Kind == CrashKind.UnexpectedRestart ? "—" : "Not known");
        CodeText = crash.Code is { } code ? DriverText.StopCode(code) : "—";
    }

    internal DriverCrash Crash { get; }

    public string TimeText { get; }

    public Badge Kind { get; }

    public string DriverName { get; }

    public string CodeText { get; }

    public override string AccessibleName => $"{TimeText}, {Kind.Text}, {DriverName}";
}

/// <summary>What the details pane's commands do; the list implements them.</summary>
internal sealed record DriverDetailActions(
    Action Close,
    Action InstallUpdate,
    Action UpdateFromFile,
    Action OpenLocation,
    Action ShowDevice,
    Action ShowModule,
    Action OpenDeviceManager,
    Action Copy);

/// <summary>
/// The details pane, for whichever table's row is selected: a heading and status, an explanation,
/// groups of facts, and the actions that make sense for it.
/// </summary>
public sealed class DriverDetails : ObservableObject
{
    private string _name = string.Empty;
    private string _subtitle = string.Empty;
    private Badge _status = Badge.None;
    private string _about = string.Empty;
    private string _lookTitle = string.Empty;
    private string _lookText = string.Empty;
    private IReadOnlyList<DriverFactGroup> _groups = [];
    private IReadOnlyList<ToolLink> _tools = [];
    private bool _canInstallUpdate;
    private bool _showInstallUpdate;
    private bool _canUpdateFromFile;
    private bool _showUpdateFromFile;
    private bool _canOpenLocation;
    private string _openLocationText = "Open location";
    private bool _canShowDevice;
    private bool _canShowModule;
    private bool _showDeviceManager;
    private string _copyText = string.Empty;
    private string _needsElevation = string.Empty;
    private bool _isBusy;
    private string _message = string.Empty;
    private bool _messageIsError;

    internal DriverDetails(string id, DriverDetailActions actions)
    {
        Id = id;
        CloseCommand = new RelayCommand(actions.Close);
        InstallUpdateCommand = new RelayCommand(actions.InstallUpdate);
        UpdateFromFileCommand = new RelayCommand(actions.UpdateFromFile);
        OpenLocationCommand = new RelayCommand(actions.OpenLocation);
        ShowDeviceCommand = new RelayCommand(actions.ShowDevice);
        ShowModuleCommand = new RelayCommand(actions.ShowModule);
        OpenDeviceManagerCommand = new RelayCommand(actions.OpenDeviceManager);
        CopyCommand = new RelayCommand(actions.Copy);
    }

    /// <summary>The tab and the row's id.</summary>
    public string Id { get; }

    public ICommand CloseCommand { get; }

    public ICommand InstallUpdateCommand { get; }

    public ICommand UpdateFromFileCommand { get; }

    public ICommand OpenLocationCommand { get; }

    public ICommand ShowDeviceCommand { get; }

    public ICommand ShowModuleCommand { get; }

    public ICommand OpenDeviceManagerCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>What kind of thing it is: the device class, "Kernel module", "Driver update".</summary>
    public string Subtitle
    {
        get => _subtitle;
        set => Set(ref _subtitle, value);
    }

    public Badge Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public string About
    {
        get => _about;
        set
        {
            if (Set(ref _about, value))
                OnPropertyChanged(nameof(HasAbout));
        }
    }

    public bool HasAbout => _about.Length > 0;

    /// <summary>A problem or something worth checking, in a few words; with <see cref="LookText"/> explaining it.</summary>
    public string LookTitle
    {
        get => _lookTitle;
        set
        {
            if (Set(ref _lookTitle, value))
                OnPropertyChanged(nameof(HasLook));
        }
    }

    public bool HasLook => _lookTitle.Length > 0;

    public string LookText
    {
        get => _lookText;
        set => Set(ref _lookText, value);
    }

    public IReadOnlyList<DriverFactGroup> Groups
    {
        get => _groups;
        set => Set(ref _groups, value);
    }

    /// <summary>Vendors' update tools that look after this driver.</summary>
    public IReadOnlyList<ToolLink> Tools
    {
        get => _tools;
        set
        {
            if (Set(ref _tools, value))
                OnPropertyChanged(nameof(HasTools));
        }
    }

    public bool HasTools => _tools.Count > 0;

    public bool CanInstallUpdate
    {
        get => _canInstallUpdate;
        set => Set(ref _canInstallUpdate, value);
    }

    public bool ShowInstallUpdate
    {
        get => _showInstallUpdate;
        set => Set(ref _showInstallUpdate, value);
    }

    public bool CanUpdateFromFile
    {
        get => _canUpdateFromFile;
        set => Set(ref _canUpdateFromFile, value);
    }

    public bool ShowUpdateFromFile
    {
        get => _showUpdateFromFile;
        set => Set(ref _showUpdateFromFile, value);
    }

    public bool CanOpenLocation
    {
        get => _canOpenLocation;
        set => Set(ref _canOpenLocation, value);
    }

    public string OpenLocationText
    {
        get => _openLocationText;
        set => Set(ref _openLocationText, value);
    }

    public bool CanShowDevice
    {
        get => _canShowDevice;
        set => Set(ref _canShowDevice, value);
    }

    public bool CanShowModule
    {
        get => _canShowModule;
        set => Set(ref _canShowModule, value);
    }

    public bool ShowDeviceManager
    {
        get => _showDeviceManager;
        set => Set(ref _showDeviceManager, value);
    }

    /// <summary>The link's label ("Copy instance id", "Copy path"); empty hides it.</summary>
    public string CopyText
    {
        get => _copyText;
        set
        {
            if (Set(ref _copyText, value))
                OnPropertyChanged(nameof(HasCopy));
        }
    }

    public bool HasCopy => _copyText.Length > 0;

    /// <summary>What needs administrator rights here; empty when nothing does or the app has them.</summary>
    public string NeedsElevation
    {
        get => _needsElevation;
        set
        {
            if (Set(ref _needsElevation, value))
                OnPropertyChanged(nameof(HasNeedsElevation));
        }
    }

    public bool HasNeedsElevation => _needsElevation.Length > 0;

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
