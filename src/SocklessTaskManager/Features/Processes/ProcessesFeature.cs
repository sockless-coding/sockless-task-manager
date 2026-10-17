using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// The Processes page: every running process, as a list or as a tree of what started what,
/// with search, filters, sorting, and processes to follow over time. One
/// <c>SystemProcessInformation</c> read per tick, only while the page is shown or something is followed.
/// </summary>
public sealed class ProcessesFeature : Feature, ISampled
{
    public ProcessesFeature(SampleClock clock)
    {
        clock.Subscribe(this);
    }

    public override string Title => "Processes";

    public override string IconKey => "IconProcesses";

    public ProcessList List { get; } = new();

    protected override Control CreateView() => new ProcessesView { DataContext = List };

    void ISampled.Sample() => List.Sample();

    void ISampled.Apply() => List.Apply();
}
