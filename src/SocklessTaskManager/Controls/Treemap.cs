using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace Sockless.TaskManager.Controls;

/// <summary>One rectangle of a treemap, and what it holds.</summary>
/// <param name="Label">Name drawn in the top left corner, when there is room.</param>
/// <param name="Detail">A size, drawn after or below the label.</param>
/// <param name="Weight">Area, relative to its siblings.</param>
/// <param name="Fill">Area fill: a tint of <paramref name="Stroke"/>, so labels stay legible.</param>
/// <param name="Stroke">Rim, in the series colour.</param>
/// <param name="Open">Run on click; items without one are inert.</param>
/// <param name="Children">What the item holds, largest first, laid out inside it where there is room.</param>
/// <param name="Describe">Tooltip text, built when the pointer reaches the item; label and detail if null.</param>
/// <param name="Tag">What the item stands for, passed to <see cref="Treemap.ContextCommand"/>.</param>
public sealed record TreemapItem(
    string Label,
    string Detail,
    double Weight,
    IBrush Fill,
    IBrush Stroke,
    ICommand? Open = null,
    IReadOnlyList<TreemapItem>? Children = null,
    Func<string>? Describe = null,
    object? Tag = null);

/// <summary>
/// A nested, squarified treemap (Bruls, Huizing and van Wijk): each item's area is its share of
/// the whole, laid out in rows that keep rectangles close to square, and an item's children are
/// laid out inside it below a one-line header, down to <see cref="MaxDepth"/> levels, while
/// there's room. The pointer picks the innermost item: it's outlined, its tooltip describes it,
/// and a click runs its <see cref="TreemapItem.Open"/>. A visual aid only: the page's table lists
/// the same top-level items with names, sizes and keyboard access, so nothing is read by colour
/// or area alone.
/// </summary>
public sealed class Treemap : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreemapItem>?> ItemsProperty =
        AvaloniaProperty.Register<Treemap, IReadOnlyList<TreemapItem>?>(nameof(Items));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<Treemap, IBrush?>(nameof(Background));

    /// <summary>Rim of the item under the pointer.</summary>
    public static readonly StyledProperty<IBrush?> HoverBrushProperty =
        AvaloniaProperty.Register<Treemap, IBrush?>(nameof(HoverBrush));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Treemap>();

    public static readonly StyledProperty<IBrush?> DetailForegroundProperty =
        AvaloniaProperty.Register<Treemap, IBrush?>(nameof(DetailForeground));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<Treemap>();

    /// <summary>
    /// Run with the <see cref="TreemapItem.Tag"/> of the innermost item under a right-click (null
    /// over nothing) before the control's context menu opens, so the menu can act on that item.
    /// </summary>
    public static readonly StyledProperty<ICommand?> ContextCommandProperty =
        AvaloniaProperty.Register<Treemap, ICommand?>(nameof(ContextCommand));

    /// <summary>Levels drawn: the items and up to two levels of what they hold.</summary>
    public const int MaxDepth = 3;

    private const double LabelSize = 12;
    private const double DetailSize = 11;
    private const double LineHeight = 16;
    private const double Inset = 5;

    /// <summary>Nested fills are fainter, so three levels of tint don't drown the labels.</summary>
    private const double NestedFillOpacity = 0.5;

    private readonly List<Box> _boxes = [];
    private Size _laidOutFor;
    private int _hover = -1;

    /// <summary>A laid-out item. Parents come before their children, so drawing in order nests them.</summary>
    private readonly record struct Box(Rect Area, TreemapItem Item, int Depth, bool HasChildren);

    static Treemap()
    {
        AffectsRender<Treemap>(BackgroundProperty, HoverBrushProperty, ForegroundProperty, DetailForegroundProperty, FontFamilyProperty);
    }

    public Treemap()
    {
        ClipToBounds = true;

        // Tunnel, so the target is set before the context menu (which listens as it bubbles) opens.
        AddHandler(ContextRequestedEvent, OnContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<TreemapItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>Shows in the 1px gaps between items.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? HoverBrush
    {
        get => GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? DetailForeground
    {
        get => GetValue(DetailForegroundProperty);
        set => SetValue(DetailForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public ICommand? ContextCommand
    {
        get => GetValue(ContextCommandProperty);
        set => SetValue(ContextCommandProperty, value);
    }

    /// <summary>An image to assistive technology, named by <c>AutomationProperties.Name</c>; the table beside it is the accessible equivalent.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new TreemapAutomationPeer(this);

    private sealed class TreemapAutomationPeer(Treemap owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override bool IsContentElementCore() => true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            _laidOutFor = default;
            _boxes.Clear();
            SetHover(-1);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Background, null, bounds);

        if (Items is not { Count: > 0 } items || bounds.Width < 4 || bounds.Height < 4)
            return;

        if (_laidOutFor != bounds.Size)
        {
            _boxes.Clear();
            LayoutLevel(items, bounds, 0);
            _laidOutFor = bounds.Size;
            if (_hover >= _boxes.Count)
                SetHover(-1);
        }

        var typeface = new Typeface(FontFamily);
        var semibold = new Typeface(FontFamily, weight: FontWeight.SemiBold);
        foreach (var box in _boxes)
        {
            var area = box.Area;
            if (box.Depth == 0)
            {
                context.DrawRectangle(box.Item.Fill, null, area);
            }
            else
            {
                using (context.PushOpacity(NestedFillOpacity))
                    context.DrawRectangle(box.Item.Fill, null, area);
            }

            if (area.Width >= 3 && area.Height >= 3)
                context.DrawRectangle(null, new Pen(box.Item.Stroke, 1), area.Deflate(0.5));

            DrawLabel(context, box, semibold, typeface);
        }

        if (_hover >= 0 && HoverBrush is { } hover)
            context.DrawRectangle(null, new Pen(hover, 2), _boxes[_hover].Area.Deflate(1));
    }

    private void LayoutLevel(IReadOnlyList<TreemapItem> items, Rect bounds, int depth)
    {
        var rects = Layout([.. items.Select(i => i.Weight)], bounds);
        for (var i = 0; i < items.Count; i++)
        {
            var r = rects[i];
            if (r.Width < 2 || r.Height < 2)
                continue;

            // A 1px gap on the right and bottom keeps neighbours of the same colour apart.
            var area = new Rect(r.X, r.Y, r.Width - 1, r.Height - 1);
            var item = items[i];
            var index = _boxes.Count;
            _boxes.Add(new Box(area, item, depth, false));

            if (depth + 1 >= MaxDepth || item.Children is not { Count: > 0 } children)
                continue;

            var header = HeaderLines(area) > 0 ? LineHeight + 3 : 3;
            var inner = new Rect(area.X + 3, area.Y + header, area.Width - 6, area.Height - header - 3);
            if (inner.Width < 24 || inner.Height < 16)
                continue;

            _boxes[index] = _boxes[index] with { HasChildren = true };
            LayoutLevel(children, inner, depth + 1);
        }
    }

    /// <summary>Lines of text that fit at the top of <paramref name="area"/>.</summary>
    private static int HeaderLines(Rect area) =>
        area.Width < 40 ? 0 : area.Height >= LineHeight * 2 + Inset ? 2 : area.Height >= LineHeight + 2 ? 1 : 0;

    private void DrawLabel(DrawingContext context, Box box, Typeface name, Typeface detail)
    {
        var area = box.Area;
        var lines = HeaderLines(area);
        var width = area.Width - Inset * 2;
        if (lines == 0 || width < 8)
            return;

        var x = area.X + Inset;
        var y = area.Y + 2;
        var label = Text(box.Item.Label, name, LabelSize, Foreground, width);

        // An item showing its contents keeps to one line: "Users  133.1 GB".
        if (box.HasChildren || lines == 1)
        {
            context.DrawText(label, new Point(x, y));
            var room = width - label.WidthIncludingTrailingWhitespace - 8;
            if (room >= 30)
            {
                var size = Text(box.Item.Detail, detail, DetailSize, DetailForeground ?? Foreground, room);
                context.DrawText(size, new Point(x + label.WidthIncludingTrailingWhitespace + 8, y + 1));
            }

            return;
        }

        context.DrawText(label, new Point(x, y));
        context.DrawText(Text(box.Item.Detail, detail, DetailSize, DetailForeground ?? Foreground, width), new Point(x, y + LineHeight));
    }

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush? brush, double width) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        SetHover(HitTest(point));
        Cursor = Opener(point) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        var index = HitTest(e.GetPosition(this));
        SetHover(index);
        ContextCommand?.Execute(index >= 0 ? _boxes[index].Item.Tag : null);
    }

    /// <summary>Shift+F10 or the menu key: the menu acts on the item under the pointer, if any.</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(this, out _))
            ContextCommand?.Execute(_hover >= 0 && _hover < _boxes.Count ? _boxes[_hover].Item.Tag : null);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;

        if (Opener(e.GetPosition(this)) is { } open)
        {
            open.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The innermost item at <paramref name="point"/> that opens: a click on a file inside a folder opens the folder.</summary>
    private ICommand? Opener(Point point)
    {
        for (var i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxes[i].Area.Contains(point) && _boxes[i].Item.Open is { } open && open.CanExecute(null))
                return open;
        }

        return null;
    }

    /// <summary>The innermost box at <paramref name="point"/>: children follow their parents, so the last match.</summary>
    private int HitTest(Point point)
    {
        for (var i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxes[i].Area.Contains(point))
                return i;
        }

        return -1;
    }

    private void SetHover(int index)
    {
        if (index == _hover)
            return;

        _hover = index;
        var item = index >= 0 && index < _boxes.Count ? _boxes[index].Item : null;
        ToolTip.SetTip(this, item is null ? null : item.Describe?.Invoke() ?? $"{item.Label}\n{item.Detail}");
        InvalidateVisual();
    }

    /// <summary>
    /// Squarified layout. <paramref name="weights"/> must be largest first, as the callers sort
    /// them; the result is in the same order. Items of zero weight get an empty rectangle.
    /// </summary>
    public static Rect[] Layout(IReadOnlyList<double> weights, Rect bounds)
    {
        var rects = new Rect[weights.Count];
        var total = 0d;
        foreach (var w in weights)
            total += Math.Max(0, w);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return rects;

        var scale = bounds.Width * bounds.Height / total;
        var areas = new double[weights.Count];
        for (var i = 0; i < areas.Length; i++)
            areas[i] = Math.Max(0, weights[i]) * scale;

        var remaining = bounds;
        var start = 0;
        while (start < areas.Length && areas[start] > 0 && remaining.Width > 0 && remaining.Height > 0)
        {
            var side = Math.Min(remaining.Width, remaining.Height);
            var sum = areas[start];
            var worst = Worst(areas[start], areas[start], sum, side);
            var end = start + 1;

            // Add items to the row while that keeps its worst aspect ratio from getting worse.
            while (end < areas.Length && areas[end] > 0)
            {
                var next = sum + areas[end];
                var nextWorst = Worst(areas[start], areas[end], next, side);
                if (nextWorst > worst)
                    break;

                sum = next;
                worst = nextWorst;
                end++;
            }

            // Lay the row along the short side, then carry on in what's left.
            if (remaining.Width >= remaining.Height)
            {
                var width = Math.Min(remaining.Width, sum / remaining.Height);
                var y = remaining.Y;
                for (var i = start; i < end; i++)
                {
                    var height = areas[i] / width;
                    rects[i] = new Rect(remaining.X, y, width, height);
                    y += height;
                }

                remaining = new Rect(remaining.X + width, remaining.Y, remaining.Width - width, remaining.Height);
            }
            else
            {
                var height = Math.Min(remaining.Height, sum / remaining.Width);
                var x = remaining.X;
                for (var i = start; i < end; i++)
                {
                    var width = areas[i] / height;
                    rects[i] = new Rect(x, remaining.Y, width, height);
                    x += width;
                }

                remaining = new Rect(remaining.X, remaining.Y + height, remaining.Width, remaining.Height - height);
            }

            start = end;
        }

        return rects;
    }

    /// <summary>The worst aspect ratio in a row of total <paramref name="sum"/> along <paramref name="side"/>.</summary>
    private static double Worst(double largest, double smallest, double sum, double side)
    {
        var side2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(side2 * largest / sum2, sum2 / (side2 * smallest));
    }
}
