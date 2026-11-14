using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Sockless.TaskManager.Features.Storage;

public sealed partial class StorageView : UserControl
{
    private bool _attached;
    private StorageBrowser? _browser;

    public StorageView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown);
    }

    // The drive list only samples while the Storage page is on screen; scans run regardless.
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
        var current = DataContext as StorageBrowser;
        if (!ReferenceEquals(_browser, current))
        {
            if (_browser is not null)
            {
                _browser.IsShown = false;
                _browser.PropertyChanged -= OnBrowserChanged;
                _browser.CopyText = null;
                _browser.OwnerWindow = null;
            }

            _browser = current;
            if (_browser is not null)
            {
                _browser.PropertyChanged += OnBrowserChanged;
                _browser.CopyText = Copy;
                _browser.OwnerWindow = Owner;
            }
        }

        if (_browser is not null)
            _browser.IsShown = _attached;
    }

    private void OnBrowserChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The dialog takes focus, so Esc and Enter reach it and Tab stays inside.
        if (e.PropertyName == nameof(StorageBrowser.HasConfirmation) && _browser is { HasConfirmation: true })
            Dispatcher.UIThread.Post(() => ConfirmCancel.Focus(NavigationMethod.Tab));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _browser?.Dismiss() == true)
            e.Handled = true;
    }

    private void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
    }

    private nint Owner() => TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;
}
