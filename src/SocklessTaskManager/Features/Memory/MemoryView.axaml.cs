using Avalonia;
using Avalonia.Controls;

namespace Sockless.TaskManager.Features.Memory;

public sealed partial class MemoryView : UserControl
{
    private bool _attached;
    private MemoryBreakdown? _breakdown;

    public MemoryView()
    {
        InitializeComponent();
    }

    // The breakdown only samples while the Memory page is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Track();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Track();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Track();
    }

    private void Track()
    {
        var current = DataContext as MemoryBreakdown;
        if (_breakdown is not null && !ReferenceEquals(_breakdown, current))
            _breakdown.IsShown = false;

        _breakdown = current;
        if (_breakdown is not null)
            _breakdown.IsShown = _attached;
    }
}
