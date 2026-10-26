using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Updates;

public enum UpdateState
{
    None,
    Available,
    Installing,
    Failed,
}

/// <summary>
/// Looks for a newer release shortly after start and twice a day, and offers it in the top bar.
/// Installing downloads it, replaces this copy and restarts. Where this copy can't be replaced, or
/// installing failed, the offer opens the release page instead. Runs off the sample clock: one
/// request every twelve hours costs nothing per tick.
/// </summary>
public sealed class Updater : ObservableObject, IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    private readonly CancellationTokenSource _stop = new();
    private UpdateState _state;
    private string? _tag;
    private bool _canReplace;
    private string? _error;

    public Updater()
    {
        InstallCommand = new RelayCommand(Install);
        if (ReleaseVersion.IsRelease)
            _ = Task.Run(() => WatchAsync(_stop.Token));
    }

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value))
                return;

            OnPropertyChanged(nameof(IsOffered));
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(ButtonText));
            OnPropertyChanged(nameof(Hint));
        }
    }

    public bool IsOffered => _state != UpdateState.None;

    public bool IsIdle => _state != UpdateState.Installing;

    public string VersionText => ReleaseVersion.Text;

    public string ButtonText => _state switch
    {
        UpdateState.Installing => "Updating…",
        UpdateState.Failed => "Update failed",
        _ => _canReplace ? $"Update to {_tag}" : $"Download {_tag}",
    };

    public string Hint => _state switch
    {
        UpdateState.Installing => $"Downloading {_tag}. The app restarts when it's in place.",
        UpdateState.Failed => $"Couldn't update: {_error} Opens the release page to download it yourself.",
        _ when _canReplace => $"{_tag} is available. Downloads it, replaces this copy and restarts.",
        _ => $"{_tag} is available. This folder can't be written to, so this opens the release page.",
    };

    public ICommand InstallCommand { get; }

    private async Task WatchAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(FirstCheck, cancel);

            // By now the copy this one replaced has exited, so its renamed files can go.
            UpdateInstaller.RemoveLeftovers();
            var canReplace = UpdateInstaller.CanReplace();

            using var timer = new PeriodicTimer(CheckEvery);
            do
            {
                try
                {
                    if (await ReleaseFeed.LatestTagAsync(cancel) is { } tag && ReleaseVersion.IsNewer(tag))
                        Dispatcher.UIThread.Post(() => Offer(tag, canReplace));
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancel.IsCancellationRequested))
                {
                    // Offline or timed out: look again next time.
                    Debug.WriteLine($"Update check failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(cancel));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Offer(string tag, bool canReplace)
    {
        if (_state is UpdateState.Installing or UpdateState.Failed)
            return;

        _tag = tag;
        _canReplace = canReplace;
        State = UpdateState.Available;
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(Hint));
    }

    private async void Install()
    {
        if (_tag is not { } tag || _state is UpdateState.None or UpdateState.Installing)
            return;

        if (!_canReplace || _state == UpdateState.Failed)
        {
            OpenReleasePage(tag);
            return;
        }

        State = UpdateState.Installing;
        try
        {
            await Task.Run(async () => UpdateInstaller.Replace(await UpdateInstaller.DownloadAsync(tag, _stop.Token)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or OperationCanceledException)
        {
            Debug.WriteLine($"Update failed: {ex}");
            Fail(ex is OperationCanceledException ? "the download timed out." : ex.Message);
            return;
        }

        if (!UpdateInstaller.Restart())
        {
            Fail($"{tag} is installed but didn't start. Close this window and start it again.");
            return;
        }

        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void Fail(string error)
    {
        _error = error;
        State = UpdateState.Failed;
    }

    private static void OpenReleasePage(string tag)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(ReleaseFeed.ReleasePage(tag)) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"Couldn't open the release page: {ex}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
