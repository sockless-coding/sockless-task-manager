using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Sockless.TaskManager.Features.Performance;

public sealed partial class ReadoutPanel : UserControl
{
    public ReadoutPanel()
    {
        InitializeComponent();
    }
}

/// <summary>Dash pattern for a legend swatch that stands for a dashed (secondary) series.</summary>
public static class SwatchDash
{
    private static readonly AvaloniaList<double> Dashed = [1.5, 1];

    public static readonly IValueConverter Converter =
        new FuncValueConverter<bool, AvaloniaList<double>?>(dashed => dashed ? Dashed : null);
}
