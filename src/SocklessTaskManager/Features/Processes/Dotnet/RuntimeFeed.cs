using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes.Dotnet;

/// <summary>
/// Events from a .NET process's runtime, followed while the .NET page is open: a session of its
/// own, gathered off the UI thread, shown on the tick. Subclasses say what to turn on and keep
/// what arrives under <see cref="Gate"/>, calling <see cref="Changed"/> when it does.
/// </summary>
public abstract class RuntimeFeed : ObservableObject
{
    protected const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const uint BufferMegabytes = 16;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly int _processId;
    private readonly EventPipeProvider _provider;
    private readonly string _following;
    private CancellationTokenSource? _run;
    private int _version;
    private int _shownVersion = -1;
    private string? _status;
    private bool _statusIsError;

    // Written by the reading task, read by Apply.
    private volatile string? _pendingStatus;
    private volatile bool _pendingIsError;

    /// <param name="following">What it follows, for errors: "its exceptions".</param>
    private protected RuntimeFeed(int processId, EventPipeProvider provider, string following)
    {
        _processId = processId;
        _provider = provider;
        _following = following;
        ClearCommand = new RelayCommand(Clear);
    }

    /// <summary>Connecting, or why there's nothing to show.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status is not null;

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    public ICommand ClearCommand { get; }

    /// <summary>Guards what the reading task gathers.</summary>
    protected Lock Gate { get; } = new();

    /// <summary>Starts afresh: the feed counts from when the page opened. UI thread.</summary>
    internal void Start()
    {
        if (_run is not null)
            return;

        lock (Gate)
        {
            Reset();
            Changed();
        }

        _run = new CancellationTokenSource();
        var cancel = _run.Token;
        _pendingIsError = false;
        _pendingStatus = "Connecting to the .NET runtime…";
        Task.Run(() => RunAsync(cancel), cancel);
    }

    /// <summary>UI thread.</summary>
    internal void Stop()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        _pendingStatus = null;
    }

    /// <summary>What arrived since the last tick, into properties. UI thread.</summary>
    internal void Apply()
    {
        Status = _pendingStatus;
        StatusIsError = _pendingIsError;

        lock (Gate)
        {
            if (_version == _shownVersion)
                return;
            _shownVersion = _version;
        }

        Show();
    }

    /// <summary>Forgets what was gathered. Under <see cref="Gate"/>.</summary>
    private protected abstract void Reset();

    /// <summary>Puts what was gathered on screen: take it under <see cref="Gate"/>. UI thread.</summary>
    private protected abstract void Show();

    /// <summary>One event of the provider's. Reading task.</summary>
    private protected abstract void OnEvent(EventMetadata metadata, ReadOnlySpan<byte> payload, int pointerSize);

    /// <summary>Something new to show. Under <see cref="Gate"/>.</summary>
    protected void Changed() => _version++;

    private void Clear()
    {
        lock (Gate)
        {
            Reset();
            Changed();
        }

        Apply();
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        EventPipeSession? session = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                handshake.CancelAfter(HandshakeTimeout);
                session = await DiagnosticsPort.StartAsync(_processId, [_provider], BufferMegabytes, handshake.Token);
            }

            _pendingStatus = null;
            var reader = new NettraceReader(session.Stream);
            await reader.ReadAsync((metadata, payload) =>
            {
                if (metadata.Provider == _provider.Name)
                    OnEvent(metadata, payload, reader.PointerSize);
            }, cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            Fail("The .NET runtime didn't answer. A suspended or busy process can't.");
        }
        catch (UnauthorizedAccessException)
        {
            Fail("Windows refused access to its .NET runtime.");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or DiagnosticsException or TimeoutException)
        {
            Fail($"Couldn't follow {_following}. {e.Message}");
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private void Fail(string message)
    {
        _pendingIsError = true;
        _pendingStatus = message;
    }
}
