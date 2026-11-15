using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Features.Updates;

namespace Sockless.TaskManager.Shell;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly SampleClock _clock;
    private readonly Navigator _navigator;
    private readonly bool _motionReduced = Motion.IsReduced;
    private readonly ShellSettings _settings = ShellSettings.Load();
    private Feature _selectedFeature;
    private bool _isSidebarOpen = true;

    public ShellViewModel(SampleClock clock, Navigator navigator, IReadOnlyList<Feature> features, Updater updates, string? startPage = null)
    {
        _clock = clock;
        _navigator = navigator;
        Features = features;
        Updates = updates;
        _selectedFeature = features.FirstOrDefault(f => f.Title == startPage) ?? features[0];
        navigator.Current = _selectedFeature;
        Elevation.CurrentPage = _selectedFeature.Title;
        TogglePauseCommand = new RelayCommand(() => _clock.IsPaused = !_clock.IsPaused);
        _clock.PropertyChanged += OnClockChanged;
        navigator.OpenRequested += feature => SelectedFeature = feature;
    }

    public IReadOnlyList<Feature> Features { get; }

    public Updater Updates { get; }

    public Feature SelectedFeature
    {
        get => _selectedFeature;
        set
        {
            // The nav list can momentarily clear its selection; a page is always shown.
            if (value is not null && Set(ref _selectedFeature, value))
            {
                _navigator.Current = value;
                Elevation.CurrentPage = value.Title;
            }
        }
    }

    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set
        {
            if (Set(ref _isSidebarOpen, value))
                OnPropertyChanged(nameof(SidebarWidth));
        }
    }

    public double SidebarWidth => _isSidebarOpen ? 240 : 64;

    /// <summary>Keeps the window above others, as Task Manager's "Always on top" does. Remembered across runs.</summary>
    public bool IsAlwaysOnTop
    {
        get => _settings.AlwaysOnTop;
        set
        {
            if (_settings.AlwaysOnTop == value)
                return;

            _settings.AlwaysOnTop = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AlwaysOnTopHint));
        }
    }

    public string AlwaysOnTopHint => _settings.AlwaysOnTop
        ? "Always on top: on. Click to let other windows cover this one"
        : "Keep this window on top of other windows";

    public bool IsPaused => _clock.IsPaused;

    public string LiveText => _clock.IsPaused ? "Paused" : "Live";

    public string PauseText => _clock.IsPaused ? "Resume" : "Pause";

    public Geometry? PauseIcon => ThemeResource.Geometry(_clock.IsPaused ? "IconPlay" : "IconPause");

    /// <summary>The live dot pulses only while sampling and only if Windows animations are on.</summary>
    public bool Pulse => !_clock.IsPaused && !_motionReduced;

    public ICommand TogglePauseCommand { get; }

    public bool IsElevated => Elevation.IsElevated;

    public bool CanRestartElevated => !Elevation.IsElevated;

    /// <summary>As Windows' own tools do, the taskbar and Alt+Tab say when this copy runs as administrator.</summary>
    public string WindowTitle => Elevation.IsElevated ? "Sockless Task Manager (Administrator)" : "Sockless Task Manager";

    public ICommand RestartElevatedCommand { get; } = new RelayCommand(Elevation.Restart);

    private void OnClockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SampleClock.IsPaused))
            return;

        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(LiveText));
        OnPropertyChanged(nameof(PauseText));
        OnPropertyChanged(nameof(PauseIcon));
        OnPropertyChanged(nameof(Pulse));
    }

    public void Dispose()
    {
        _clock.Dispose();
        Updates.Dispose();
        foreach (var feature in Features)
            feature.Dispose();
    }
}
