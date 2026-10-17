using Avalonia.Controls;
using Avalonia.Media;

namespace Sockless.TaskManager.Core;

/// <summary>
/// A top-level page in the shell's sidebar (Performance today; Processes, Startup, Services later).
/// Each feature is a vertical slice under <c>Features/&lt;Name&gt;/</c> and is registered once in
/// <see cref="App"/>. The view is created on first visit and kept, so the page keeps its state.
/// </summary>
public abstract class Feature : ObservableObject, IDisposable
{
    private Control? _view;

    /// <summary>Sidebar label and top bar title.</summary>
    public abstract string Title { get; }

    /// <summary>Key of a <c>StreamGeometry</c> in <c>Theme/Icons.axaml</c>.</summary>
    public abstract string IconKey { get; }

    public Geometry? Icon => ThemeResource.Geometry(IconKey);

    public Control View => _view ??= CreateView();

    protected abstract Control CreateView();

    /// <summary>Accessible name of the sidebar item.</summary>
    public override string ToString() => Title;

    public virtual void Dispose()
    {
    }
}
