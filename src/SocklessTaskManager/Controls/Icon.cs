using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sockless.TaskManager.Controls;

/// <summary>
/// Draws a Heroicons outline path (24×24 viewbox, 2px round stroke) or, with
/// <see cref="Filled"/>, a filled mark such as the gear. Colour follows the inherited foreground.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public static readonly StyledProperty<double> ViewBoxSizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(ViewBoxSize), 24);

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<Icon, bool>(nameof(Filled));

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, ForegroundProperty, ViewBoxSizeProperty, FilledProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double ViewBoxSize
    {
        get => GetValue(ViewBoxSizeProperty);
        set => SetValue(ViewBoxSizeProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsNaN(Width) ? 20 : Width,
        double.IsNaN(Height) ? 20 : Height);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush)
            return;

        var scale = Math.Min(Bounds.Width, Bounds.Height) / ViewBoxSize;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (Filled)
                context.DrawGeometry(brush, null, data);
            else
                context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
        }
    }
}
