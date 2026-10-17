using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Sockless.TaskManager.Core;

/// <summary>Looks up tokens from <c>Theme/Tokens.axaml</c> and icons from <c>Theme/Icons.axaml</c> by key. UI thread only.</summary>
public static class ThemeResource
{
    public static IBrush Brush(string key) => Get<IBrush>(key) ?? Brushes.Transparent;

    public static Geometry? Geometry(string key) => Get<Geometry>(key);

    private static T? Get<T>(string key) where T : class =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) ? value as T : null;
}
