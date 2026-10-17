using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Controls;

/// <summary>
/// A scrolling history plot in the style of Task Manager: newest sample on the right,
/// a light grid, a filled primary series and an optional dashed secondary series.
/// Colours come from the theme's series tokens; the dash keeps the two series apart
/// without relying on colour alone.
/// </summary>
public sealed class HistoryGraph : Control
{
    private const double FillOpacity = 0.16;

    public static readonly StyledProperty<History?> SeriesProperty =
        AvaloniaProperty.Register<HistoryGraph, History?>(nameof(Series));

    public static readonly StyledProperty<History?> SecondarySeriesProperty =
        AvaloniaProperty.Register<HistoryGraph, History?>(nameof(SecondarySeries));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<HistoryGraph, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<HistoryGraph, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> SecondaryStrokeProperty =
        AvaloniaProperty.Register<HistoryGraph, IBrush?>(nameof(SecondaryStroke));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<HistoryGraph, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<HistoryGraph, IBrush?>(nameof(Background));

    public static readonly StyledProperty<int> GridDivisionsProperty =
        AvaloniaProperty.Register<HistoryGraph, int>(nameof(GridDivisions), 10);

    private IBrush? _fill;
    private bool _attached;

    static HistoryGraph()
    {
        AffectsRender<HistoryGraph>(
            MaximumProperty, StrokeProperty, SecondaryStrokeProperty, GridBrushProperty,
            BackgroundProperty, GridDivisionsProperty);
    }

    public History? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public History? SecondarySeries
    {
        get => GetValue(SecondarySeriesProperty);
        set => SetValue(SecondarySeriesProperty, value);
    }

    /// <summary>Value at the top of the plot. Bind it for auto-scaling series.</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Stroke of the dashed secondary series. Defaults to <see cref="Stroke"/>.</summary>
    public IBrush? SecondaryStroke
    {
        get => GetValue(SecondaryStrokeProperty);
        set => SetValue(SecondaryStrokeProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Grid cells per side; 0 hides the grid (sidebar sparklines).</summary>
    public int GridDivisions
    {
        get => GetValue(GridDivisionsProperty);
        set => SetValue(GridDivisionsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeriesProperty || change.Property == SecondarySeriesProperty)
        {
            if (change.OldValue is History old)
                old.Changed -= OnSeriesChanged;
            if (change.NewValue is History @new && _attached)
                @new.Changed += OnSeriesChanged;
            InvalidateVisual();
        }
        else if (change.Property == StrokeProperty)
        {
            _fill = change.NewValue is ISolidColorBrush solid
                ? new SolidColorBrush(solid.Color, FillOpacity)
                : null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Series?.Changed += OnSeriesChanged;
        SecondarySeries?.Changed += OnSeriesChanged;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Series?.Changed -= OnSeriesChanged;
        SecondarySeries?.Changed -= OnSeriesChanged;
    }

    private void OnSeriesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width < 2 || bounds.Height < 2)
            return;

        if (Background is { } background)
            context.FillRectangle(background, bounds);

        if (GridDivisions > 0 && GridBrush is { } gridBrush)
        {
            var gridPen = new Pen(gridBrush, 1);
            for (var i = 1; i < GridDivisions; i++)
            {
                var x = Math.Round(bounds.Width * i / GridDivisions) + 0.5;
                var y = Math.Round(bounds.Height * i / GridDivisions) + 0.5;
                context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
                context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        var max = Maximum > 0 ? Maximum : 1;
        if (Series is { } primary && Stroke is { } stroke)
            DrawSeries(context, primary, bounds, max, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round), _fill);

        if (SecondarySeries is { } secondary && (SecondaryStroke ?? Stroke) is { } secondaryStroke)
        {
            var dashed = new Pen(secondaryStroke, 1.5, new DashStyle([4, 3], 0), lineJoin: PenLineJoin.Round);
            DrawSeries(context, secondary, bounds, max, dashed, fill: null);
        }

        if (Stroke is { } border)
            context.DrawRectangle(new Pen(border, 1), bounds.Deflate(0.5));
    }

    private static void DrawSeries(DrawingContext context, History series, Rect bounds, double max, Pen pen, IBrush? fill)
    {
        if (series.Count < 2)
            return;

        var step = bounds.Width / (series.Length - 1);
        var first = series.Length - series.Count;

        Point At(int i) => new(
            i * step,
            bounds.Height - Math.Clamp(series[i] / max, 0, 1) * (bounds.Height - 1));

        if (fill is not null)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(first * step, bounds.Height), isFilled: true);
                for (var i = first; i < series.Length; i++)
                    ctx.LineTo(At(i));
                ctx.LineTo(new Point(bounds.Width, bounds.Height));
                ctx.EndFigure(isClosed: true);
            }

            context.DrawGeometry(fill, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(At(first), isFilled: false);
            for (var i = first + 1; i < series.Length; i++)
                ctx.LineTo(At(i));
            ctx.EndFigure(isClosed: false);
        }

        context.DrawGeometry(null, pen, line);
    }
}
