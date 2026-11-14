using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Sockless.TaskManager.Features.Network;

public sealed partial class NetworkView : UserControl
{
    /// <summary>Below this body width the table would be too narrow beside the details, so they cover it.</summary>
    private const double SideBySideWidth = 1100;

    private const double DetailsWidth = 380;

    private bool _attached;
    private NetworkPage? _page;
    private TopLevel? _topLevel;

    public NetworkView()
    {
        InitializeComponent();
        Body.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        AddHandler(KeyDownEvent, OnKeyDown);
        Search.KeyDown += OnSearchKeyDown;
        Fit(ConnectionScroller, ConnectionTable);
        Fit(ListenerScroller, ListenerTable);
        Fit(ProcessScroller, ProcessTable);
    }

    /// <summary>
    /// A table fills its scroller but never gets narrower than its minimum, where it scrolls instead.
    /// A definite width keeps star columns the same in every row: measured at the scroller's
    /// unbounded width, each row would size them to its own content.
    /// </summary>
    private static void Fit(ScrollViewer scroller, Control table) =>
        scroller.SizeChanged += (_, e) => table.Width = Math.Max(table.MinWidth, e.NewSize.Width);

    // The page renders only while it's on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
            _topLevel.KeyDown += OnWindowKeyDown;
        Track();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        if (_topLevel is not null)
            _topLevel.KeyDown -= OnWindowKeyDown;
        _topLevel = null;
        Track();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Track();
    }

    private void Track()
    {
        var current = DataContext as NetworkPage;
        if (_page is not null && !ReferenceEquals(_page, current))
        {
            _page.IsShown = false;
            _page.PropertyChanged -= OnPageChanged;
            _page.ExportRequested -= OnExportRequested;
            _page.CopyRequested -= OnCopyRequested;
            _page.ImportRequested -= OnImportRequested;
        }

        if (_page is null || !ReferenceEquals(_page, current))
        {
            _page = current;
            if (_page is not null)
            {
                _page.PropertyChanged += OnPageChanged;
                _page.ExportRequested += OnExportRequested;
                _page.CopyRequested += OnCopyRequested;
                _page.ImportRequested += OnImportRequested;
            }
        }

        if (_page is not null)
            _page.IsShown = _attached;
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A dialog takes focus, so Esc and Enter reach it and Tab stays inside.
        if (e.PropertyName == nameof(NetworkPage.HasConfirmation) && _page is { HasConfirmation: true })
            Dispatcher.UIThread.Post(() => ConfirmCancel.Focus(NavigationMethod.Tab));
        else if (e.PropertyName == nameof(NetworkPage.HasGeoSetup) && _page is { HasGeoSetup: true })
            Dispatcher.UIThread.Post(() => SettingsClose.Focus(NavigationMethod.Tab));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _page?.Dismiss() == true)
            e.Handled = true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && _page is { HasDialog: false })
        {
            Search.Focus(NavigationMethod.Tab);
            Search.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _page is { HasSearch: true })
        {
            _page.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private async void OnExportRequested(string suggestedName)
    {
        if (_page is not { } page || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV file") { Patterns = ["*.csv"] }],
            ShowOverwritePrompt = true,
        });
        if (file?.TryGetLocalPath() is { } path)
            await page.ExportAsync(path);
    }

    private async void OnImportRequested()
    {
        if (_page is not { } page || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
            return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a location or network database",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("MaxMind DB file") { Patterns = ["*.mmdb"] }],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
            await page.ImportGeoAsync(path);
    }

    private void OnCopyRequested(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(text);
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
