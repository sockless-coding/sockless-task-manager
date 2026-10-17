using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Sockless.TaskManager.Features.Processes;

public sealed partial class ProcessesView : UserControl
{
    private bool _attached;
    private ProcessList? _list;
    private TopLevel? _topLevel;

    public ProcessesView()
    {
        InitializeComponent();
        Search.KeyDown += OnSearchKeyDown;
    }

    // The table only updates while the Processes page is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Track();

        // Ctrl+F finds wherever focus is in the window, as long as this page is shown.
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Track();

        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Track();
    }

    private void Track()
    {
        var current = DataContext as ProcessList;
        if (_list is not null && !ReferenceEquals(_list, current))
            _list.IsShown = false;

        _list = current;
        if (_list is not null)
            _list.IsShown = _attached;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            Search.Focus(NavigationMethod.Tab);
            Search.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _list is { HasSearch: true })
        {
            _list.SearchText = string.Empty;
            e.Handled = true;
        }
    }
}
