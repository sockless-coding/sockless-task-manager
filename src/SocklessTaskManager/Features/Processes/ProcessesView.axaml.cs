using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sockless.TaskManager.Features.Processes;

public sealed partial class ProcessesView : UserControl
{
    /// <summary>Below this body width the table would be too narrow beside the details, so they cover it.</summary>
    private const double SideBySideWidth = 960;

    private const double DetailsWidth = 340;

    private bool _attached;
    private ProcessList? _list;
    private TopLevel? _topLevel;

    public ProcessesView()
    {
        InitializeComponent();
        Search.KeyDown += OnSearchKeyDown;
        Body.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        TableHost.SizeChanged += (_, _) => FitTable();

        // Esc from anywhere on the page. Arrow keys and Delete from a focused row, caught on the way
        // down: the table's scroll viewer would otherwise take the arrows to scroll.
        AddHandler(KeyDownEvent, OnKeyDown);
        RunningTable.AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel);
        EndedTable.AddHandler(KeyDownEvent, OnTableKeyDown, RoutingStrategies.Tunnel);

        // Right-clicking a row selects it, so the menu and the details agree.
        RunningTable.AddHandler(ContextRequestedEvent, OnRowContextRequested, RoutingStrategies.Tunnel);
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
        {
            _list.IsShown = false;
            _list.PropertyChanged -= OnListChanged;
            _list.CopyText = null;
        }

        if (_list is null || !ReferenceEquals(_list, current))
        {
            _list = current;
            if (_list is not null)
            {
                _list.PropertyChanged += OnListChanged;
                _list.CopyText = Copy;
            }
        }

        if (_list is not null)
            _list.IsShown = _attached;
    }

    /// <summary>Beside the table when there's room for both; over it when there isn't.</summary>
    private void Layout(double width)
    {
        var sideBySide = width >= SideBySideWidth;
        Grid.SetColumn(DetailsPane, sideBySide ? 1 : 0);
        Grid.SetColumnSpan(DetailsPane, sideBySide ? 1 : 2);
        DetailsPane.Width = sideBySide ? DetailsWidth : double.NaN;
        DetailsPane.Margin = sideBySide ? new Thickness(16, 0, 0, 0) : default;
    }

    /// <summary>
    /// The table fills the card, but never gets narrower than its columns need; past that it
    /// scrolls sideways. Measured against the card rather than left to the scroll viewer, which
    /// would size the Name column to whichever names happen to be realized.
    /// </summary>
    private void FitTable()
    {
        if (_list is not null)
            TableContent.Width = Math.Max(TableHost.Bounds.Width, _list.TableMinWidth);
    }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessList.TableMinWidth))
            FitTable();

        // A destructive prompt starts on Cancel, so Enter or Space alone never confirms it.
        if (e.PropertyName == nameof(ProcessList.HasConfirmation) && _list is { HasConfirmation: true })
            Dispatcher.UIThread.Post(() => ConfirmCancel.Focus(NavigationMethod.Tab));
    }

    private void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && _list is not ({ HasConfirmation: true } or { HasManagedPage: true }))
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_list is not { } list || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None)
            return;

        if (list.Confirmation is { } confirmation)
            confirmation.CancelCommand.Execute(null);
        else if (list.HasManagedPage)
            list.CloseManaged();
        else if (list.HasDetails)
            list.CloseDetails();
        else
            return;
        e.Handled = true;
    }

    private void OnTableKeyDown(object? sender, KeyEventArgs e)
    {
        if (_list is not { } list || sender is not ItemsControl table || e.KeyModifiers != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                FocusRow(table, list.MoveSelection(e.Key == Key.Down ? 1 : -1));
                e.Handled = true;
                break;
            case Key.Delete when !list.IsEndedView && list.SelectedKey is { } key:
                list.RequestEnd(key, tree: false);
                e.Handled = true;
                break;
        }
    }

    private static void FocusRow(ItemsControl table, int index)
    {
        if (index < 0)
            return;

        table.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() =>
        {
            var row = table.ContainerFromIndex(index)?.GetVisualDescendants().OfType<Button>().FirstOrDefault();
            row?.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is StyledElement { DataContext: ProcessRow row })
            _list?.Select(row.Key);
    }
}
