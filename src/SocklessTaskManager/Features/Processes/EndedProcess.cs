using System.Globalization;
using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// A process that ended while the app was watching: one row of the Ended table. Traced exits are
/// exact; exits found by comparing samples are known to within a second, and the processes
/// that lived and died between two samples are never seen.
/// </summary>
public sealed class EndedProcess : ObservableObject
{
    /// <summary>Lifetimes shorter than this count as short-lived.</summary>
    internal static readonly TimeSpan ShortLived = TimeSpan.FromSeconds(10);

    private bool _isSelected;

    internal EndedProcess(
        ProcessKey key,
        string name,
        string? path,
        int sessionId,
        string? parent,
        DateTime ended,
        bool isExact,
        uint? exitCode,
        ulong? peakCommit,
        Action<EndedProcess> select)
    {
        Key = key;
        Name = name;
        Path = path;
        SessionId = sessionId;
        Parent = parent;
        Started = DateTime.FromFileTimeUtc(key.CreateTime).ToLocalTime();
        Ended = ended;
        IsExact = isExact;
        ExitCode = exitCode;
        PeakCommit = peakCommit;
        Lifetime = ended > Started ? ended - Started : TimeSpan.Zero;
        SelectCommand = new RelayCommand(() => select(this));
    }

    internal ProcessKey Key { get; }

    internal int SessionId { get; }

    internal DateTime Started { get; }

    internal DateTime Ended { get; }

    internal TimeSpan Lifetime { get; }

    /// <summary>Reported by the kernel, rather than inferred from a process missing from a sample.</summary>
    internal bool IsExact { get; }

    internal uint? ExitCode { get; }

    internal ulong? PeakCommit { get; }

    internal bool IsShortLived => Lifetime < ShortLived;

    public string Name { get; }

    public string? Path { get; }

    /// <summary>"explorer.exe (PID 4120)", if it was still running when this process started.</summary>
    public string? Parent { get; }

    public string ToolTip => Path ?? Name;

    public string IdText => Key.Id.ToString(CultureInfo.CurrentCulture);

    public string EndedText => Ended.ToString("T", CultureInfo.CurrentCulture);

    /// <summary>Sampled exits are only known to the second they were noticed.</summary>
    public string LifetimeText => IsExact ? Duration(Lifetime) : $"≈ {Duration(Lifetime)}";

    public string ExitCodeText => ExitCode is { } code ? ExitCodeString(code) : "—";

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public string AccessibleName => $"{Name}, process {IdText}, ended {EndedText}, ran {LifetimeText}";

    internal bool Matches(string search) =>
        Name.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Path?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || IdText.StartsWith(search, StringComparison.Ordinal);

    /// <summary>NTSTATUS-style codes read best in hex, ordinary ones in decimal.</summary>
    internal static string ExitCodeString(uint code) =>
        code >= 0x80000000 ? $"0x{code:X8}" : code.ToString(CultureInfo.CurrentCulture);

    /// <summary>"40 ms", "2.4 s", "38 s", "4 min 12 s", "3 h 05 min", "2 d 4 h".</summary>
    internal static string Duration(TimeSpan span)
    {
        var culture = CultureInfo.CurrentCulture;
        if (span.TotalSeconds < 1)
            return $"{Math.Max(0, (int)span.TotalMilliseconds).ToString(culture)} ms";
        if (span.TotalSeconds < 10)
            return $"{span.TotalSeconds.ToString("F1", culture)} s";
        if (span.TotalMinutes < 1)
            return $"{((int)span.TotalSeconds).ToString(culture)} s";
        if (span.TotalHours < 1)
            return $"{span.Minutes.ToString(culture)} min {span.Seconds.ToString(culture)} s";
        if (span.TotalDays < 1)
            return $"{span.Hours.ToString(culture)} h {span.Minutes:00} min";
        return $"{((int)span.TotalDays).ToString(culture)} d {span.Hours.ToString(culture)} h";
    }
}
