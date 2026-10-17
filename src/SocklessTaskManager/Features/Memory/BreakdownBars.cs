using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sockless.TaskManager.Features.Memory;

public sealed record CompositionSegment(double Fraction, IBrush Brush);

/// <summary>
/// Task Manager's memory composition bar: one stacked bar of the level's parts, left to right
/// in table order. The rows below carry the names and matching swatches, so the bar is never
/// read by colour alone.
/// </summary>
public sealed class CompositionBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<CompositionSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<CompositionBar, IReadOnlyList<CompositionSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<CompositionBar, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<CompositionBar, IBrush?>(nameof(BorderBrush));

    static CompositionBar()
    {
        AffectsRender<CompositionBar>(SegmentsProperty, BackgroundProperty, BorderBrushProperty);
    }

    public IReadOnlyList<CompositionSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Shows between segments, so neighbours of similar colour stay apart.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var outline = new RoundedRect(bounds, 3);
        context.DrawRectangle(Background, null, outline);

        if (Segments is { } segments)
        {
            using (context.PushClip(outline))
            {
                var x = 0d;
                foreach (var segment in segments)
                {
                    var width = segment.Fraction * bounds.Width;
                    if (width >= 1)
                        context.DrawRectangle(segment.Brush, null, new Rect(x, 0, Math.Max(0, width - 1), bounds.Height));
                    x += width;
                }
            }
        }

        if (BorderBrush is { } border)
            context.DrawRectangle(null, new Pen(border, 1), new RoundedRect(bounds.Deflate(0.5), 3));
    }
}

/// <summary>A thin horizontal bar: a row's share of its level.</summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ShareBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Background));

    static ShareBar()
    {
        AffectsRender<ShareBar>(ValueProperty, FillProperty, BackgroundProperty);
    }

    /// <summary>0–1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 6 : Height);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Background, null, new RoundedRect(bounds, 2));

        var width = Math.Clamp(Value, 0, 1) * bounds.Width;
        if (width > 0)
            context.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, 0, Math.Max(width, 2), bounds.Height), 2));
    }
}
