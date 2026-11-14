using System.Buffers.Binary;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Network;

/// <summary>
/// The map outline embedded in the app: Natural Earth's countries (public domain), built by
/// tools/New-WorldMap.ps1. No IP data: it only says where countries are, so a dot has somewhere to
/// go when a database knows the country but not the city. Coordinates are degrees.
/// </summary>
internal static class WorldMapData
{
    private static readonly Lazy<(Dictionary<string, Point> Labels, List<(string Code, Point[][] Rings)> Shapes)> Data = new(Load);

    /// <summary>Where each country's dot goes, by ISO code: (longitude, latitude).</summary>
    public static IReadOnlyDictionary<string, Point> Labels => Data.Value.Labels;

    public static IReadOnlyList<(string Code, Point[][] Rings)> Shapes => Data.Value.Shapes;

    private static (Dictionary<string, Point>, List<(string, Point[][])>) Load()
    {
        var labels = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        var shapes = new List<(string, Point[][])>();
        using var stream = typeof(WorldMapData).Assembly.GetManifestResourceStream("WorldMap.bin");
        if (stream is null)
            return (labels, shapes);

        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        ReadOnlySpan<byte> span = data;
        if (!span.StartsWith("SLMAP1"u8))
            return (labels, shapes);

        var at = 6;
        int U16(ReadOnlySpan<byte> s)
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
            at += 2;
            return v;
        }

        double Degrees(ReadOnlySpan<byte> s)
        {
            var v = BinaryPrimitives.ReadInt16LittleEndian(s[at..]);
            at += 2;
            return v / 100.0;
        }

        string Code(ReadOnlySpan<byte> s)
        {
            var code = string.Create(2, (s[at], s[at + 1]), (chars, b) => (chars[0], chars[1]) = ((char)b.Item1, (char)b.Item2));
            at += 2;
            return code;
        }

        var labelCount = U16(span);
        for (var i = 0; i < labelCount; i++)
        {
            var code = Code(span);
            var x = Degrees(span);
            labels[code] = new Point(x, Degrees(span));
        }

        var shapeCount = U16(span);
        for (var i = 0; i < shapeCount; i++)
        {
            var code = Code(span);
            var rings = new Point[U16(span)][];
            for (var r = 0; r < rings.Length; r++)
            {
                var points = new Point[U16(span)];
                for (var p = 0; p < points.Length; p++)
                {
                    var x = Degrees(span);
                    points[p] = new Point(x, Degrees(span));
                }

                rings[r] = points;
            }

            shapes.Add((code, rings));
        }

        return (labels, shapes);
    }
}

/// <summary>
/// A place on the map: a city (when the database has coordinates) or a country, with the
/// connections whose other end the database puts there.
/// </summary>
public sealed class MapPlace : NetworkRow
{
    private string _name = string.Empty;
    private string _caption = string.Empty;
    private string _directionText = string.Empty;
    private string _countText = string.Empty;
    private string _openText = string.Empty;
    private string _processesText = string.Empty;
    private string _bytesText = string.Empty;

    internal MapPlace(string key, Action<NetworkRow> select)
        : base(key, select)
    {
    }

    /// <summary>Degrees.</summary>
    internal double Longitude { get; set; }

    internal double Latitude { get; set; }

    internal string? CountryCode { get; set; }

    /// <summary>True when placed by the country only.</summary>
    internal bool IsCountry { get; set; }

    /// <summary>What History searches for to show these connections.</summary>
    internal string Search { get; set; } = string.Empty;

    internal List<ConnectionInfo> Connections { get; } = [];

    internal int Count { get; set; }

    internal int Open { get; set; }

    internal int Incoming { get; set; }

    internal int Outgoing { get; set; }

    internal long Bytes { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string Caption
    {
        get => _caption;
        set => Set(ref _caption, value);
    }

    /// <summary>"Outgoing", "Incoming" or "Both ways": who connected to whom.</summary>
    public string DirectionText
    {
        get => _directionText;
        set => Set(ref _directionText, value);
    }

    public string CountText
    {
        get => _countText;
        set => Set(ref _countText, value);
    }

    public string OpenText
    {
        get => _openText;
        set => Set(ref _openText, value);
    }

    public string ProcessesText
    {
        get => _processesText;
        set => Set(ref _processesText, value);
    }

    public string BytesText
    {
        get => _bytesText;
        set => Set(ref _bytesText, value);
    }

    public override string AccessibleName => $"{_name}, {_countText} connections, {_directionText}";
}

/// <summary>
/// The world, equirectangular, with countries shaded by how many connections end there, a dot per
/// place sized by its connections, and a line from this computer's Windows region to each. Lines
/// to places that connected in are dashed and verdigris; outgoing ones solid copper (colour is never
/// the only cue). Scroll to zoom, drag to move, double-click to reset; point at a dot for its name,
/// click it to select it.
/// </summary>
public sealed class WorldMap : Control
{
    private const double North = 84;
    private const double South = -58;
    private const double WorldHeight = North - South;
    private const double MaxZoom = 24;
    private const int LabelledPlaces = 8;

    public static readonly StyledProperty<IReadOnlyList<MapPlace>?> PlacesProperty =
        AvaloniaProperty.Register<WorldMap, IReadOnlyList<MapPlace>?>(nameof(Places));

    public static readonly StyledProperty<IReadOnlyDictionary<string, int>?> CountriesProperty =
        AvaloniaProperty.Register<WorldMap, IReadOnlyDictionary<string, int>?>(nameof(Countries));

    public static readonly StyledProperty<string?> HomeProperty =
        AvaloniaProperty.Register<WorldMap, string?>(nameof(Home));

    /// <summary>Bumped when places change in place, to redraw.</summary>
    public static readonly StyledProperty<int> VersionProperty =
        AvaloniaProperty.Register<WorldMap, int>(nameof(Version));

    private static List<(string Code, StreamGeometry Geometry)>? _shapes;

    private double _zoom = 1;
    private Point _center = new(0, (North + South) / 2);
    private MapPlace? _hover;
    private Point? _dragFrom;
    private Point _dragCenter;

    static WorldMap()
    {
        AffectsRender<WorldMap>(PlacesProperty, CountriesProperty, HomeProperty, VersionProperty);
        FocusableProperty.OverrideDefaultValue<WorldMap>(true);
    }

    public WorldMap()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<MapPlace>? Places
    {
        get => GetValue(PlacesProperty);
        set => SetValue(PlacesProperty, value);
    }

    /// <summary>Connections by country code, for the shading.</summary>
    public IReadOnlyDictionary<string, int>? Countries
    {
        get => GetValue(CountriesProperty);
        set => SetValue(CountriesProperty, value);
    }

    /// <summary>The country code lines start from: this computer's Windows region. Null for no lines.</summary>
    public string? Home
    {
        get => GetValue(HomeProperty);
        set => SetValue(HomeProperty, value);
    }

    public int Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    // ---------- Projection ----------

    /// <summary>Pixels per degree at the current zoom.</summary>
    private double Scale => Math.Min(Bounds.Width / 360, Bounds.Height / WorldHeight) * _zoom;

    /// <summary>(longitude, latitude) to a point on the control.</summary>
    private Point ToScreen(double longitude, double latitude)
    {
        var s = Scale;
        return new Point(
            Bounds.Width / 2 + (longitude - _center.X) * s,
            Bounds.Height / 2 - (latitude - _center.Y) * s);
    }

    private Point ToWorld(Point screen)
    {
        var s = Scale;
        return new Point(
            _center.X + (screen.X - Bounds.Width / 2) / s,
            _center.Y - (screen.Y - Bounds.Height / 2) / s);
    }

    /// <summary>Keeps the visible area on the map.</summary>
    private void Clamp()
    {
        var s = Scale;
        var halfWidth = Bounds.Width / 2 / s;
        var halfHeight = Bounds.Height / 2 / s;
        _center = new Point(
            halfWidth >= 180 ? 0 : Math.Clamp(_center.X, -180 + halfWidth, 180 - halfWidth),
            halfHeight >= WorldHeight / 2 ? (North + South) / 2 : Math.Clamp(_center.Y, South + halfHeight, North - halfHeight));
    }

    // ---------- Drawing ----------

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 10 || Bounds.Height < 10)
            return;

        Clamp();
        context.FillRectangle(ThemeResource.Brush("SlChartPlot"), new Rect(Bounds.Size));
        DrawCountries(context);

        var places = Places ?? [];
        var home = Home is { } code && WorldMapData.Labels.TryGetValue(code, out var h) ? ToScreen(h.X, h.Y) : (Point?)null;
        if (home is { } from)
        {
            foreach (var place in places)
                DrawArc(context, from, place);
        }

        var ranked = places.OrderByDescending(p => p.Count).ToList();
        foreach (var place in Enumerable.Reverse(ranked))
            DrawDot(context, place);

        if (home is { } origin)
        {
            var ring = new Pen(ThemeResource.Brush("SlTextHeading"), 2);
            context.DrawEllipse(ThemeResource.Brush("SlChartPlot"), ring, origin, 5, 5);
            context.DrawEllipse(ThemeResource.Brush("SlTextHeading"), null, origin, 2, 2);
        }

        DrawLabels(context, ranked);
        if (_hover is { } hover)
            DrawTip(context, hover);
    }

    private void DrawCountries(DrawingContext context)
    {
        _shapes ??= WorldMapData.Shapes.Select(s => (s.Code, Outline(s.Rings))).ToList();

        var counts = Countries;
        var max = counts is { Count: > 0 } ? counts.Values.Max() : 0;
        var s = Scale;
        var border = new Pen(ThemeResource.Brush("SlBorder"), 0.8 / s);
        var land = ThemeResource.Brush("SlRaised");
        IBrush[] shades = [ThemeResource.Brush("SlAccentTint10"), ThemeResource.Brush("SlAccentTint20"), ThemeResource.Brush("SlAccentTint30")];

        var origin = ToScreen(-180, North);
        using (context.PushTransform(new Matrix(s, 0, 0, s, origin.X, origin.Y)))
        {
            foreach (var (code, geometry) in _shapes)
            {
                var brush = land;
                if (counts is not null && counts.TryGetValue(code, out var count) && count > 0)
                {
                    // Thirds of the busiest country, on a square-root scale so one busy country doesn't wash out the rest.
                    var share = Math.Sqrt(count / (double)max);
                    brush = shades[share > 0.66 ? 2 : share > 0.33 ? 1 : 0];
                }

                context.DrawGeometry(brush, border, geometry);
            }
        }
    }

    /// <summary>A country in map units: x = degrees east of 180°W, y = degrees south of 84°N.</summary>
    private static StreamGeometry Outline(Point[][] rings)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.SetFillRule(FillRule.EvenOdd);
        foreach (var ring in rings)
        {
            if (ring.Length < 3)
                continue;
            g.BeginFigure(new Point(ring[0].X + 180, North - ring[0].Y), true);
            for (var i = 1; i < ring.Length; i++)
                g.LineTo(new Point(ring[i].X + 180, North - ring[i].Y));
            g.EndFigure(true);
        }

        return geometry;
    }

    private static double Radius(MapPlace place) => Math.Min(14, 3 + 1.6 * Math.Sqrt(place.Count));

    private void DrawArc(DrawingContext context, Point from, MapPlace place)
    {
        var to = ToScreen(place.Longitude, place.Latitude);
        var distance = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
        if (distance < 4)
            return;

        // Bow the line upwards, more the longer it is, like a flight path.
        var middle = new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2 - distance * 0.2);
        var incoming = place.Incoming > 0;
        var pen = incoming
            ? new Pen(ThemeResource.Brush("SlSeries2"), 1.2, new DashStyle([4, 3], 0))
            : new Pen(ThemeResource.Brush("SlAccentLine"), 1.2);

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(from, false);
            g.QuadraticBezierTo(middle, to);
            g.EndFigure(false);
        }

        using (context.PushOpacity(ReferenceEquals(place, _hover) || place.IsSelected ? 0.95 : 0.45))
            context.DrawGeometry(null, pen, geometry);
    }

    private void DrawDot(DrawingContext context, MapPlace place)
    {
        var at = ToScreen(place.Longitude, place.Latitude);
        var radius = Radius(place);
        var fill = place.Outgoing == 0 ? ThemeResource.Brush("SlSeries2") : ThemeResource.Brush("SlAccent");
        IPen? ring = place.IsSelected || ReferenceEquals(place, _hover)
            ? new Pen(ThemeResource.Brush("SlTextHeading"), 2)
            : place.Incoming > 0 && place.Outgoing > 0
                ? new Pen(ThemeResource.Brush("SlSeries2"), 2)
                : new Pen(ThemeResource.Brush("SlChartPlot"), 1);
        using (context.PushOpacity(0.9))
            context.DrawEllipse(fill, ring, at, radius, radius);
    }

    /// <summary>Names for the busiest places, where they don't run into each other.</summary>
    private void DrawLabels(DrawingContext context, List<MapPlace> ranked)
    {
        var taken = new List<Rect>();
        foreach (var place in ranked.Take(LabelledPlaces))
        {
            if (ReferenceEquals(place, _hover))
                continue;
            var text = Text(place.Name, ThemeResource.Brush("SlText"), 11);
            var at = ToScreen(place.Longitude, place.Latitude);
            var rect = new Rect(at.X + Radius(place) + 4, at.Y - text.Height / 2, text.Width, text.Height);
            if (taken.Any(r => r.Intersects(rect)) || !new Rect(Bounds.Size).Contains(rect))
                continue;
            taken.Add(rect);
            context.DrawText(text, rect.TopLeft);
        }
    }

    private void DrawTip(DrawingContext context, MapPlace place)
    {
        var title = Text(place.Name, ThemeResource.Brush("SlTextHeading"), 12, FontWeight.SemiBold);
        var detail = Text($"{place.CountText} connections · {place.DirectionText} · {place.ProcessesText} processes", ThemeResource.Brush("SlTextSecondary"), 11);
        var width = Math.Max(title.Width, detail.Width) + 16;
        var height = title.Height + detail.Height + 12;
        var at = ToScreen(place.Longitude, place.Latitude);
        var x = at.X + Radius(place) + 8;
        if (x + width > Bounds.Width)
            x = at.X - Radius(place) - 8 - width;
        var y = Math.Clamp(at.Y - height / 2, 4, Math.Max(4, Bounds.Height - height - 4));

        var box = new Rect(x, y, width, height);
        context.DrawRectangle(ThemeResource.Brush("SlSurface"), new Pen(ThemeResource.Brush("SlBorderAccent"), 1), box, 3, 3);
        context.DrawText(title, new Point(x + 8, y + 6));
        context.DrawText(detail, new Point(x + 8, y + 6 + title.Height));
    }

    private FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal)
    {
        var family = this.TryFindResource("SlFontBody", out var f) && f is FontFamily font ? font : FontFamily.Default;
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);
    }

    // ---------- Pointer ----------

    private MapPlace? PlaceAt(Point point)
    {
        MapPlace? best = null;
        var bestDistance = double.MaxValue;
        foreach (var place in Places ?? [])
        {
            var at = ToScreen(place.Longitude, place.Latitude);
            var distance = Math.Sqrt((at.X - point.X) * (at.X - point.X) + (at.Y - point.Y) * (at.Y - point.Y));
            if (distance <= Radius(place) + 4 && distance < bestDistance)
                (best, bestDistance) = (place, distance);
        }

        return best;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragFrom is { } from)
        {
            var s = Scale;
            _center = new Point(_dragCenter.X - (point.X - from.X) / s, _dragCenter.Y + (point.Y - from.Y) / s);
            InvalidateVisual();
            return;
        }

        var hover = PlaceAt(point);
        if (!ReferenceEquals(hover, _hover))
        {
            _hover = hover;
            Cursor = hover is null ? null : new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            _zoom = 1;
            _center = new Point(0, (North + South) / 2);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (PlaceAt(e.GetPosition(this)) is { } place)
        {
            place.SelectCommand.Execute(null);
            InvalidateVisual();
        }
        else
        {
            _dragFrom = e.GetPosition(this);
            _dragCenter = _center;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is not null)
        {
            _dragFrom = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var point = e.GetPosition(this);
        var before = ToWorld(point);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.25, e.Delta.Y), 1, MaxZoom);

        // Keep the point under the pointer where it was.
        var after = ToWorld(point);
        _center = new Point(_center.X + before.X - after.X, _center.Y + before.Y - after.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = 40 / Scale;
        switch (e.Key)
        {
            case Key.Add or Key.OemPlus:
                _zoom = Math.Min(MaxZoom, _zoom * 1.25);
                break;
            case Key.Subtract or Key.OemMinus:
                _zoom = Math.Max(1, _zoom / 1.25);
                break;
            case Key.Left:
                _center = new Point(_center.X - step, _center.Y);
                break;
            case Key.Right:
                _center = new Point(_center.X + step, _center.Y);
                break;
            case Key.Up:
                _center = new Point(_center.X, _center.Y + step);
                break;
            case Key.Down:
                _center = new Point(_center.X, _center.Y - step);
                break;
            default:
                return;
        }

        InvalidateVisual();
        e.Handled = true;
    }
}
