using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Memory;

/// <summary>
/// The Memory page: what physical memory is holding, as a drill-down from RAM to processes,
/// files, kernel pools and drivers. A process opens on the Processes page, where its details,
/// actions and memory makeup are. Its own page rather than part of Performance, because the
/// lists it shows want the full height. Samples only while the page is on screen.
/// </summary>
public sealed class MemoryFeature : Feature, ISampled
{
    public MemoryFeature(SampleClock clock, Navigator navigator)
    {
        clock.Subscribe(this);
        Breakdown = new MemoryBreakdown(navigator);
    }

    public override string Title => "Memory";

    public override string IconKey => "IconMemory";

    public MemoryBreakdown Breakdown { get; }

    protected override Control CreateView() => new MemoryView { DataContext = Breakdown };

    void ISampled.Sample() => Breakdown.Sample();

    void ISampled.Apply() => Breakdown.Apply();

    public override void Dispose() => Breakdown.Dispose();
}
