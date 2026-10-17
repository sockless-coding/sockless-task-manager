using Avalonia;
using Avalonia.Controls;

namespace Sockless.TaskManager.Controls;

/// <summary>
/// Frames a <see cref="HistoryGraph"/> with Task Manager's captions: what is plotted and the
/// scale above it, the time span and zero below. Template lives in <c>Theme/Controls.axaml</c>.
/// </summary>
public sealed class GraphFrame : ContentControl
{
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<GraphFrame, string?>(nameof(Caption));

    public static readonly StyledProperty<string?> ScaleProperty =
        AvaloniaProperty.Register<GraphFrame, string?>(nameof(Scale));

    public static readonly StyledProperty<string?> SpanProperty =
        AvaloniaProperty.Register<GraphFrame, string?>(nameof(Span), "60 seconds");

    public static readonly StyledProperty<bool> ShowFooterProperty =
        AvaloniaProperty.Register<GraphFrame, bool>(nameof(ShowFooter), true);

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>The value at the top of the graph: "100%", "4.0 GB", "1 Mbps".</summary>
    public string? Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    public string? Span
    {
        get => GetValue(SpanProperty);
        set => SetValue(SpanProperty, value);
    }

    public bool ShowFooter
    {
        get => GetValue(ShowFooterProperty);
        set => SetValue(ShowFooterProperty, value);
    }
}
