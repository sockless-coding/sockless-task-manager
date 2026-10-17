using System.Globalization;
using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// A process the user follows: a card with its CPU and memory over the last minute. Keeps
/// recording while the Processes page is hidden, and stays (marked as exited) after the
/// process ends, until it is dismissed.
/// </summary>
public sealed class FollowedProcess : ObservableObject
{
    private const double BytesPerMegabyte = 1024 * 1024;

    private string _cpuText = "0%";
    private string _memoryText = string.Empty;
    private double _memoryMaximum = 1;
    private bool _isRunning = true;

    internal FollowedProcess(ProcessReading reading, Action<ProcessKey> unfollow)
    {
        Key = reading.Key;
        Name = reading.Name;
        ToolTip = reading.Path ?? reading.Name;
        IdText = $"PID {reading.Key.Id.ToString(CultureInfo.CurrentCulture)}";
        UnfollowCommand = new RelayCommand(() => unfollow(Key));
        Update(reading);
    }

    internal ProcessKey Key { get; }

    public string Name { get; }

    public string ToolTip { get; }

    public string IdText { get; }

    public ICommand UnfollowCommand { get; }

    public string UnfollowName => $"Stop following {Name}";

    /// <summary>Share of all logical processors, 0–100.</summary>
    public History Cpu { get; } = new();

    /// <summary>Private working set in MB.</summary>
    public History Memory { get; } = new();

    public string CpuText
    {
        get => _cpuText;
        private set => Set(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => Set(ref _memoryText, value);
    }

    /// <summary>Top of the memory graph in MB, rounded up to a 1/2/5 step.</summary>
    public double MemoryMaximum
    {
        get => _memoryMaximum;
        private set => Set(ref _memoryMaximum, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusText => _isRunning ? "Running" : "Exited";

    internal void Update(ProcessReading reading)
    {
        Cpu.Push(reading.Cpu);
        Memory.Push(reading.Memory / BytesPerMegabyte);
        CpuText = ProcessRow.CpuPercent(reading.Cpu);
        MemoryText = Format.Bytes(reading.Memory);
        MemoryMaximum = Format.NiceCeiling(Memory.Max(), 1);
    }

    /// <summary>The graphs freeze at the last sample.</summary>
    internal void MarkExited()
    {
        IsRunning = false;
        CpuText = "—";
    }
}
