using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Sockless.TaskManager.Controls;
using Sockless.TaskManager.Core;
using Sockless.TaskManager.Core.Interop;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>A part of the working set: private data, programs and DLLs, mapped files…</summary>
public sealed class MemoryPart(string name, string note, ulong bytes, double share, IBrush series)
{
    public string Name { get; } = name;

    public string Note { get; } = note;

    public string SizeText { get; } = Format.Bytes(bytes);

    /// <summary>0–1 of the working set.</summary>
    public double Share { get; } = share;

    public string ShareText { get; } = share is > 0 and < 0.005 ? "<1%" : Format.Percent(share * 100);

    public IBrush Series { get; } = series;

    public string AccessibleName => $"{Name}, {SizeText}";
}

/// <summary>A file the process has mapped, and how much of it is in RAM.</summary>
public sealed class MappedFileLine
{
    internal MappedFileLine(FileUse file, double share)
    {
        Name = Path.GetFileName(file.Path);
        Folder = Path.GetDirectoryName(file.Path) ?? string.Empty;
        FullPath = file.Path;
        SizeText = Format.Bytes(file.ResidentBytes);
        Share = share;
    }

    public string Name { get; }

    public string Folder { get; }

    public string FullPath { get; }

    public string SizeText { get; }

    /// <summary>0–1 of the largest file listed.</summary>
    public double Share { get; }

    public string AccessibleName => $"{Name}, {SizeText}";
}

/// <summary>
/// The memory makeup page of one process, opened from its details: its working set split by what
/// backs each page (as VMMap does), and the EXE, DLL and data files it has in RAM. A snapshot,
/// read once on opening and again on Refresh: walking an address space takes milliseconds, too
/// slow to repeat every tick.
/// </summary>
public sealed class ProcessMemoryMap : ObservableObject
{
    private readonly int _processId;
    private ProcessSpace? _space;
    private int _generation;
    private bool _isBusy;
    private string _status = string.Empty;
    private bool _statusIsError;
    private string _totalText = "—";
    private IReadOnlyList<MemoryPart> _parts = [];
    private IReadOnlyList<CompositionSegment> _composition = [];
    private IReadOnlyList<MappedFileLine> _files = [];
    private bool _showImages = true;

    internal ProcessMemoryMap(int processId, string processName, Action close)
    {
        _processId = processId;
        ProcessName = processName;
        CloseCommand = new RelayCommand(close);
        RefreshCommand = new RelayCommand(Scan);
        RestartElevatedCommand = new RelayCommand(Elevation.Restart);
        Scan();
    }

    public string ProcessName { get; }

    public string Subtitle => $"PID {_processId.ToString(CultureInfo.CurrentCulture)} · everything it has in RAM, including DLLs and files other processes may share";

    public ICommand CloseCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand RestartElevatedCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        private set
        {
            if (Set(ref _statusIsError, value))
                OnPropertyChanged(nameof(OfferElevation));
        }
    }

    public bool OfferElevation => _statusIsError && !Elevation.IsElevated;

    public bool HasSpace => _space is not null;

    public string TotalText
    {
        get => _totalText;
        private set => Set(ref _totalText, value);
    }

    public IReadOnlyList<MemoryPart> Parts
    {
        get => _parts;
        private set => Set(ref _parts, value);
    }

    public IReadOnlyList<CompositionSegment> Composition
    {
        get => _composition;
        private set => Set(ref _composition, value);
    }

    /// <summary>Programs and DLLs, rather than mapped data files.</summary>
    public bool ShowImages
    {
        get => _showImages;
        set
        {
            if (Set(ref _showImages, value))
            {
                OnPropertyChanged(nameof(ShowMapped));
                ShowFiles();
            }
        }
    }

    public bool ShowMapped
    {
        get => !_showImages;
        set => ShowImages = !value;
    }

    public string ImagesText => _space is { } space ? $"Programs and DLLs · {Format.Number(space.Images.Count)}" : "Programs and DLLs";

    public string MappedText => _space is { } space ? $"Mapped files · {Format.Number(space.MappedFiles.Count)}" : "Mapped files";

    public IReadOnlyList<MappedFileLine> Files
    {
        get => _files;
        private set
        {
            if (Set(ref _files, value))
                OnPropertyChanged(nameof(HasNoFiles));
        }
    }

    public bool HasNoFiles => _space is not null && _files.Count == 0;

    private void Scan()
    {
        if (_isBusy)
            return;

        IsBusy = true;
        StatusIsError = false;
        Status = "Reading the address space…";
        var generation = ++_generation;
        var id = _processId;
        Task.Run(() =>
        {
            ProcessSpace? space = null;
            string? error = null;
            try
            {
                space = AddressSpace.Scan(id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Address space scan failed: {ex}");
                error = ex is UnauthorizedAccessException ? ex.Message : $"Reading failed: {ex.Message}";
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _generation)
                    Show(space, error);
            });
        });
    }

    private void Show(ProcessSpace? space, string? error)
    {
        IsBusy = false;
        if (space is null)
        {
            StatusIsError = true;
            Status = error ?? "Reading failed.";
            return;
        }

        _space = space;
        Status = $"Snapshot taken at {DateTime.Now.ToString("T", CultureInfo.CurrentCulture)}.";
        TotalText = Format.Bytes(space.WorkingSetBytes);

        var total = Math.Max(1d, space.WorkingSetBytes);
        (string Name, string Note, ulong Bytes, string Series, bool Optional)[] parts =
        [
            ("Private data", "Heaps, stacks and other memory only this process uses", space.PrivateBytes, "SlSeries1", false),
            ("Programs and DLLs", $"{Format.Number(space.Images.Count)} EXE and DLL files, mostly shared with other processes", space.ImageBytes, "SlSeries2", false),
            ("Mapped files", $"{Format.Number(space.MappedFiles.Count)} data files mapped into memory", space.MappedFileBytes, "SlSeries3", false),
            ("Shared memory", "Sections shared with other processes", space.SharedMemoryBytes, "SlSeries4", false),
            ("Other", "Pages outside the address space map", space.OtherBytes, "SlSeriesIdle", true),
        ];
        var shown = parts.Where(p => !p.Optional || p.Bytes > 0).ToList();
        Parts = [.. shown.Select(p => new MemoryPart(p.Name, p.Note, p.Bytes, p.Bytes / total, ThemeResource.Brush(p.Series)))];
        Composition = [.. shown.Select(p => new CompositionSegment(p.Bytes / total, ThemeResource.Brush(p.Series)))];

        OnPropertyChanged(nameof(HasSpace));
        OnPropertyChanged(nameof(ImagesText));
        OnPropertyChanged(nameof(MappedText));
        ShowFiles();
    }

    private void ShowFiles()
    {
        if (_space is not { } space)
            return;

        var files = _showImages ? space.Images : space.MappedFiles;
        var largest = Math.Max(1d, files.Count == 0 ? 0 : files.Max(f => (double)f.ResidentBytes));
        Files = [.. files
            .OrderByDescending(f => f.ResidentBytes)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => new MappedFileLine(f, f.ResidentBytes / largest))];
    }
}
