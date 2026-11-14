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
    private Feature _selectedFeature;
    private bool _isSidebarOpen = true;

    public ShellViewModel(SampleClock clock, Navigator navigator, IReadOnlyList<Feature> features, Updater updates)
    {
        _clock = clock;
        _navigator = navigator;
        Features = features;
        Updates = updates;
        _selectedFeature = features[0];
        navigator.Current = _selectedFeature;
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
                _navigator.Current = value;
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

    public bool IsPaused => _clock.IsPaused;

    public string LiveText => _clock.IsPaused ? "Paused" : "Live";

    public string PauseText => _clock.IsPaused ? "Resume" : "Pause";

    public Geometry? PauseIcon => ThemeResource.Geometry(_clock.IsPaused ? "IconPlay" : "IconPause");

    /// <summary>The live dot pulses only while sampling and only if Windows animations are on.</summary>
    public bool Pulse => !_clock.IsPaused && !_motionReduced;

    public ICommand TogglePauseCommand { get; }

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
