using Avalonia.Controls;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Storage;

/// <summary>
/// The Storage page: what takes the space on each drive, as a treemap and a table of folders
/// and files to drill into. A drive is scanned when it's first opened; the scan runs in the
/// background and is kept until it's run again. Samples (free space) only while on screen.
/// </summary>
public sealed class StorageFeature : Feature, ISampled
{
    public StorageFeature(SampleClock clock)
    {
        clock.Subscribe(this);
    }

    public override string Title => "Storage";

    public override string IconKey => "IconFolder";

    public StorageBrowser Browser { get; } = new();

    protected override Control CreateView() => new StorageView { DataContext = Browser };

    void ISampled.Sample() => Browser.Sample();

    void ISampled.Apply() => Browser.Apply();

    public override void Dispose() => Browser.Dispose();
}
