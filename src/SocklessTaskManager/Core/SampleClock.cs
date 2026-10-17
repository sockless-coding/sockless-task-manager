using System.Diagnostics;
using Avalonia.Threading;

namespace Sockless.TaskManager.Core;

/// <summary>Something that reads system state on a schedule.</summary>
public interface ISampled
{
    /// <summary>Read the system. Runs on a background thread; must not touch bound properties.</summary>
    void Sample();

    /// <summary>Publish the last sample to the UI. Runs on the UI thread.</summary>
    void Apply();
}

/// <summary>
/// The one heartbeat of the app. Every tick it calls <see cref="ISampled.Sample"/> on each
/// subscriber off the UI thread, then <see cref="ISampled.Apply"/> on the UI thread, and waits
/// for that to finish before the next tick so a slow sample can never pile up.
/// </summary>
public sealed class SampleClock : ObservableObject, IDisposable
{
    private readonly List<ISampled> _subscribers = [];
    private readonly CancellationTokenSource _stop = new();
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private bool _isPaused;
    private PeriodicTimer? _timer;

    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            if (Set(ref _interval, value) && _timer is not null)
                _timer.Period = value;
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => Set(ref _isPaused, value);
    }

    public void Subscribe(ISampled sampled)
    {
        lock (_subscribers)
            _subscribers.Add(sampled);
    }

    public void Start()
    {
        _timer ??= new PeriodicTimer(_interval);
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        ISampled[] subscribers = [];
        try
        {
            // Sample immediately so rate counters are primed by the first visible tick.
            do
            {
                if (_isPaused)
                    continue;

                lock (_subscribers)
                {
                    if (subscribers.Length != _subscribers.Count)
                        subscribers = [.. _subscribers];
                }

                foreach (var subscriber in subscribers)
                {
                    try
                    {
                        subscriber.Sample();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Sample failed in {subscriber}: {ex}");
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var subscriber in subscribers)
                    {
                        try
                        {
                            subscriber.Apply();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Apply failed in {subscriber}: {ex}");
                        }
                    }
                });
            }
            while (await _timer!.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer?.Dispose();
    }
}
