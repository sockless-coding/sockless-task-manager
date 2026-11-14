using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sockless.TaskManager.Features.Users;

public sealed partial class UsersView : UserControl
{
    /// <summary>Below this body width the table would be too narrow beside the details, so they cover it.</summary>
    private const double SideBySideWidth = 1000;

    private const double DetailsWidth = 360;

    private bool _attached;
    private UserList? _list;

    public UsersView()
    {
        InitializeComponent();
        Body.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    // The list only samples while the Users page is on screen.
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
        var current = DataContext as UserList;
        if (_list is not null && !ReferenceEquals(_list, current))
        {
            _list.IsShown = false;
            _list.PropertyChanged -= OnListChanged;
        }

        if (_list is null || !ReferenceEquals(_list, current))
        {
            _list = current;
            if (_list is not null)
                _list.PropertyChanged += OnListChanged;
        }

        if (_list is not null)
            _list.IsShown = _attached;
    }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The dialog takes focus, so Esc and Enter reach it and Tab stays inside.
        if (e.PropertyName == nameof(UserList.HasConfirmation) && _list is { HasConfirmation: true })
            Dispatcher.UIThread.Post(() => ConfirmCancel.Focus(NavigationMethod.Tab));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _list?.Dismiss() == true)
            e.Handled = true;
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
