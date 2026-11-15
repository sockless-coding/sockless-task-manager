using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Sockless.TaskManager.Features.Autostart;

public sealed partial class AutostartView : UserControl
{
    /// <summary>Below this body width the table would be too narrow beside the details, so they cover it.</summary>
    private const double SideBySideWidth = 1060;

    private const double DetailsWidth = 400;

    private AutostartList? _list;
    private bool _attached;

    public AutostartView()
    {
        InitializeComponent();
        Body.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    // The first time the page is shown, it scans.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (_list is not null)
            _list.IsShown = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_list is not null)
        {
            _list.PropertyChanged -= OnListChanged;
            _list.CopyRequested -= OnCopyRequested;
        }

        _list = DataContext as AutostartList;
        if (_list is not null)
        {
            _list.PropertyChanged += OnListChanged;
            _list.CopyRequested += OnCopyRequested;
            if (_attached)
                _list.IsShown = true;
        }
    }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The dialog takes focus, so Esc and Enter reach it and Tab stays inside.
        if (e.PropertyName == nameof(AutostartList.HasConfirmation) && _list is { HasConfirmation: true })
            Dispatcher.UIThread.Post(() => ConfirmCancel.Focus(NavigationMethod.Tab));
    }

    private void OnCopyRequested(string text)
    {
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _list?.Dismiss() == true)
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && _list is { HasConfirmation: false })
        {
            Search.Focus(NavigationMethod.Tab);
            Search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None && _list is { HasConfirmation: false })
        {
            _list.Refresh();
            e.Handled = true;
        }
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
}
