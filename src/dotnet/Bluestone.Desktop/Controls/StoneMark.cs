using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;

namespace Bluestone.Desktop.Controls;

internal enum LogoVariant
{
    /// <summary>Full facets with the luminous bloom, for the splash and About.</summary>
    Splash,

    /// <summary>Full facets without the bloom, for window, taskbar, and Dock icons.</summary>
    ApplicationIcon,

    /// <summary>Twelve broad facets and heavier edges, for roughly 16–32 pixels.</summary>
    Small,
}

/// <summary>
/// Bluestone's mark: a faceted gemstone in a dark recess inside a luminous hexagonal shell, cyan on the left and amber on
/// the right. Everything is drawn from fixed geometry in a 100-unit design box, so it scales to any size and DPI and is
/// identical on every launch. Colours come from one smooth light field sampled across the facets, so neighbouring faces
/// change gradually while each face keeps a crisp edge.
/// On screen the splash variant is animated on the render thread (StoneMark.Animated.cs): the gem builds itself, then
/// the light inside it drifts slowly. Exported images and icons are the still mark drawn here.
/// </summary>
internal sealed partial class StoneMark : Panel
{
    private const double OuterRadius = 46;
    private const double ShellInnerRadius = 41.2;
    private const double GemRadius = 36.8;

    private static readonly Point Middle = new(50, 50);

    private static readonly Geometry OuterHex = Hexagon(OuterRadius);
    private static readonly Geometry ShellInnerHex = Hexagon(ShellInnerRadius);
    private static readonly Geometry ShellBand = Band(OuterRadius, ShellInnerRadius);
    private static readonly Geometry GemHex = Hexagon(GemRadius);

    // The shell: cyan on the left, pale where the light crosses the top and bottom points, amber to the right.
    private static readonly (string Color, double Offset)[] ShellFillStops =
        [("#E01ED8F0", 0), ("#E070F4FF", 0.3), ("#E038E8F4", 0.5), ("#E0B8FAFF", 0.66), ("#E0FFD9A0", 0.72), ("#E0FF9A2A", 0.8), ("#E0FF600A", 1)];
    private static readonly (string Color, double Offset)[] ShellRimStops =
        [("#9EFFFF", 0), ("#E0FFFF", 0.3), ("#B0FFFF", 0.5), ("#FFFFFF", 0.66), ("#FFE7B0", 0.72), ("#FFB050", 0.82), ("#FF9030", 1)];
    private static readonly (string Color, double Offset)[] ShellGlowStops =
        [("#FF00E5FF", 0), ("#FF40F0FF", 0.5), ("#C0C0F8FF", 0.66), ("#FFFFB43A", 0.74), ("#FFFF6A0A", 1)];
    private static readonly (string Color, double Offset)[] GemEdgeStops = [("#C060F8FF", 0), ("#A0CFF4FF", 0.5), ("#C0FFB040", 1)];
    private static readonly (string Color, double Offset)[] RecessStops = [("#0C1C40", 0.6), ("#040A1C", 1)];
    private const string RidgeColor = "#16E8FBFF";
    private const string RecessOutlineColor = "#B0030818";

    private static readonly IBrush ShellFill = Across(ShellFillStops);
    private static readonly IBrush ShellRim = Across(ShellRimStops);
    private static readonly IBrush ShellGlow = Across(ShellGlowStops);
    private static readonly IBrush RecessFill = new RadialGradientBrush
    {
        GradientStops = { new GradientStop(Color.Parse(RecessStops[0].Color), RecessStops[0].Offset), new GradientStop(Color.Parse(RecessStops[1].Color), RecessStops[1].Offset) },
    };
    private static readonly IBrush GemEdge = Across(GemEdgeStops);
    private static readonly IBrush Ridge = new SolidColorBrush(Color.Parse(RidgeColor));

    private static readonly Color Highlight = Color.Parse("#F2FFFF");

    /// <summary>
    /// The colour of light through the stone at points in the gem's own units (centre 0,0; corners at radius 1), sampled
    /// from the reference: ice and cyan across the top face to a white-hot centre, dusk violet toward the upper right,
    /// cobalt falling to navy down the left face, and amber on the right face over a smoky interior near the centre.
    /// </summary>
    private static readonly (Vector At, Color Color)[] LightField =
    [
        (new(-0.30, -0.80), Color.Parse("#22F1FD")),
        (new(-0.80, -0.45), Color.Parse("#40EEFE")),
        (new(-0.40, -0.30), Color.Parse("#8DF9FD")),
        (new(0.12, -0.72), Color.Parse("#0A90F2")),
        (new(0.30, -0.30), Color.Parse("#73B3EA")),
        (new(0.50, -0.55), Color.Parse("#5A50A0")),
        (new(0.88, -0.42), Color.Parse("#A42C16")),
        (new(0.00, -0.20), Color.Parse("#69DDFD")),
        (new(0.00, -0.08), Color.Parse("#B4F4FD")),
        (new(-0.75, -0.20), Color.Parse("#0A86F0")),
        (new(-0.88, 0.05), Color.Parse("#007FE8")),
        (new(-0.40, 0.10), Color.Parse("#005AD4")),
        (new(-0.10, 0.08), Color.Parse("#0A4CC4")),
        (new(-0.14, 0.18), Color.Parse("#013291")),
        (new(-0.30, 0.34), Color.Parse("#012F8B")),
        (new(-0.62, 0.50), Color.Parse("#001C5B")),
        (new(-0.08, 0.55), Color.Parse("#06246E")),
        (new(-0.12, 0.84), Color.Parse("#0A2466")),
        (new(0.38, -0.08), Color.Parse("#984545")),
        (new(0.10, 0.08), Color.Parse("#8A3A22")),
        (new(0.14, 0.18), Color.Parse("#7A3426")),
        (new(0.08, 0.55), Color.Parse("#C84C12")),
        (new(0.22, 0.48), Color.Parse("#C1491B")),
        (new(0.86, 0.10), Color.Parse("#FC760C")),
        (new(0.70, 0.46), Color.Parse("#FB6A04")),
        (new(0.50, 0.72), Color.Parse("#FFA61A")),
        (new(0.12, 0.86), Color.Parse("#FD8A10")),
    ];

    // Each sixth of the stone from the top clockwise, paired into the faces of a cube seen point-on as in the reference:
    // the top face is lifted and the left face lowered a little, so the cube's edges read crisply between the faces.
    private static readonly double[] SectorLight = [0.12, 0.04, 0.04, -0.12, -0.12, 0.12];

    private static readonly Facet[] Facets = CutFacets();
    private static readonly Facet[] SmallFacets = CutSmallFacets();

    private readonly LogoLayer[] _bloom;
    private readonly LogoLayer _artwork;
    private LogoVariant _variant;
    private CompositionCustomVisual? _animated;

    public StoneMark()
    {
        // The bloom builds outward in steps, as a real glow does: a far, faint aura that spills well into whatever is
        // behind the mark, a nearer and stronger one, the halo hugging the shell, and the flare over the artwork.
        _bloom =
        [
            new LogoLayer(this, LayerKind.Aura, blur: AuraBlur) { Opacity = AuraOpacity },
            new LogoLayer(this, LayerKind.Aura, blur: NearAuraBlur) { Opacity = NearAuraOpacity },
            new LogoLayer(this, LayerKind.Halo, blur: HaloBlur),
            new LogoLayer(this, LayerKind.Flare, blur: FlareBlur) { Opacity = FlareOpacity },
        ];
        _artwork = new LogoLayer(this, LayerKind.Artwork, blur: 0);
        Children.Add(_bloom[0]);
        Children.Add(_bloom[1]);
        Children.Add(_bloom[2]);
        Children.Add(_artwork);
        Children.Add(_bloom[3]);
        IsHitTestVisible = false;
    }

    // The bloom passes: blur radii in design units, and how strongly each shows.
    private const double AuraBlur = 30;
    private const double AuraOpacity = 0.45;
    private const double NearAuraBlur = 14;
    private const double NearAuraOpacity = 0.5;
    private const double HaloBlur = 4;
    private const double FlareBlur = 1.6;
    private const double FlareOpacity = 0.8;

    public LogoVariant Variant
    {
        get => _variant;
        set
        {
            _variant = value;
            UpdateAnimation();
            foreach (var layer in Children)
            {
                layer.InvalidateVisual();
            }
        }
    }

    /// <summary>Whether the mark is being drawn by the animated render-thread visual rather than the still layers.</summary>
    internal bool IsAnimated => _animated is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopAnimation();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_animated is not null)
        {
            _animated.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
        }
    }

    /// <summary>
    /// Animates the splash mark while it is on screen, and shows the still layers otherwise (icons, exports, and anywhere
    /// the compositor is unavailable).
    /// </summary>
    private void UpdateAnimation()
    {
        if (_variant == LogoVariant.Splash && _animated is null && ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            _animated = compositor.CreateCustomVisual(new AnimatedMark());
            _animated.Size = new Vector(Bounds.Width, Bounds.Height);

            // Composition visuals clip to their size by default; the bloom must spill past the mark's box.
            _animated.ClipToBounds = false;
            ElementComposition.SetElementChildVisual(this, _animated);
            _animated.SendHandlerMessage(AnimatedMark.Start.Instance);
        }
        else if (_variant != LogoVariant.Splash)
        {
            StopAnimation();
        }

        _artwork.IsVisible = _animated is null;
        foreach (var layer in _bloom)
        {
            layer.IsVisible = _animated is null && _variant == LogoVariant.Splash;
        }
    }

    private void StopAnimation()
    {
        if (_animated is not null)
        {
            _animated.SendHandlerMessage(AnimatedMark.Stop.Instance);
            ElementComposition.SetElementChildVisual(this, null);
            _animated = null;
        }
    }

    /// <summary>Exports the scalable vector mark as a PNG at the requested pixel size.</summary>
    public static byte[] CreatePng(int pixels, LogoVariant variant, double inset = 0)
    {
        using var bitmap = Render(pixels, inset, variant);
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        return png.ToArray();
    }

    /// <summary>
    /// The application-window and taskbar icon, rendered without the splash bloom. The design box leaves a margin around
    /// the hexagon, so the icon bleeds the box past its edges until the hexagon's points nearly touch the top and bottom.
    /// </summary>
    public static WindowIcon CreateIcon(int pixels = 256) =>
        new(Render(pixels, inset: -pixels * 0.035, variant: pixels <= 32 ? LogoVariant.Small : LogoVariant.ApplicationIcon));

    /// <summary>The macOS Dock image, padded to sit on Apple's icon grid.</summary>
    public static byte[] CreateDockImage(int pixels = 512) => CreatePng(pixels, LogoVariant.ApplicationIcon, inset: pixels / 10.0);

    private static RenderTargetBitmap Render(int pixels, double inset, LogoVariant variant)
    {
        // A negative inset is a negative margin: the mark is laid out larger than the bitmap, which crops its empty edges.
        var mark = new StoneMark { Variant = variant, Margin = new Thickness(inset) };
        var frame = new Decorator { Child = mark, Width = pixels, Height = pixels };
        frame.Measure(new Size(pixels, pixels));
        frame.Arrange(new Rect(0, 0, pixels, pixels));
        var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels));
        bitmap.Render(frame);
        return bitmap;
    }

    private static void DrawArtwork(DrawingContext context, LogoVariant variant)
    {
        var small = variant == LogoVariant.Small;

        context.DrawGeometry(ShellFill, null, ShellBand);
        context.DrawGeometry(RecessFill, null, ShellInnerHex);

        // Each face is stroked in its own fill so neighbouring faces meet without a hairline seam.
        foreach (var facet in small ? SmallFacets : Facets)
        {
            context.DrawGeometry(facet.Fill, new Pen(facet.Fill, 0.3, lineJoin: PenLineJoin.Round), facet.Shape);
        }

        if (!small)
        {
            var ridge = new Pen(Ridge, 0.28, lineCap: PenLineCap.Round);
            var edge = new Pen(Ridge, 0.5, lineCap: PenLineCap.Round);
            for (var k = 0; k < 6; k++)
            {
                // The odd spokes are the cube's edges (to the upper right, bottom and upper left), so they catch more light.
                context.DrawLine(k % 2 == 1 ? edge : ridge, Middle, Corner(GemRadius, k));
                context.DrawLine(ridge, Corner(GemRadius, k), Corner(GemRadius, k + 2));
            }
        }

        context.DrawGeometry(null, new Pen(GemEdge, small ? 1.4 : 0.6), GemHex);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse(RecessOutlineColor)), small ? 1.2 : 0.7), ShellInnerHex);
        context.DrawGeometry(null, new Pen(ShellRim, small ? 2.4 : 1.5, lineJoin: PenLineJoin.Miter), Hexagon(OuterRadius - (small ? 1.2 : 0.75)));
    }

    private static void DrawAura(DrawingContext context)
    {
        context.DrawGeometry(ShellGlow, new Pen(ShellGlow, 4, lineJoin: PenLineJoin.Round), OuterHex);
    }

    private static void DrawHalo(DrawingContext context)
    {
        context.DrawGeometry(ShellGlow, new Pen(ShellGlow, 6, lineJoin: PenLineJoin.Round), ShellBand);
    }

    private static void DrawFlare(DrawingContext context)
    {
        context.DrawGeometry(null, new Pen(ShellGlow, 1.4, lineJoin: PenLineJoin.Round), ShellInnerHex);
        context.DrawGeometry(null, new Pen(GemEdge, 0.8), GemHex);
    }

    /// <summary>
    /// The full cut: each sixth of the gem is split by the spokes and the two crossing triangles of a hexagram into
    /// five triangles, thirty in all.
    /// </summary>
    private static Facet[] CutFacets()
    {
        var facets = new List<Facet>();
        for (var k = 0; k < 6; k++)
        {
            var corner = Corner(GemRadius, k);
            var next = Corner(GemRadius, k + 1);
            var spoke = Corner(GemRadius / 2, k);
            var nextSpoke = Corner(GemRadius / 2, k + 1);
            var crossing = Middle + (Direction(-60 + (60 * k)) * (GemRadius / Math.Sqrt(3)));

            var tone = SectorLight[k];
            facets.Add(Cut([corner, spoke, crossing], light: tone + 0.05));
            facets.Add(Cut([crossing, nextSpoke, next], light: tone - 0.04));
            facets.Add(Cut([corner, next, crossing], light: tone));
            facets.Add(Cut([Middle, spoke, crossing], light: tone + 0.02));
            facets.Add(Cut([Middle, crossing, nextSpoke], light: tone - 0.02));
        }

        return [.. facets];
    }

    /// <summary>The cut for small icons: each sixth halved at its edge's midpoint, twelve broad faces.</summary>
    private static Facet[] CutSmallFacets()
    {
        var facets = new List<Facet>();
        for (var k = 0; k < 6; k++)
        {
            var corner = Corner(GemRadius, k);
            var next = Corner(GemRadius, k + 1);
            var edge = new Point((corner.X + next.X) / 2, (corner.Y + next.Y) / 2);
            facets.Add(Cut([Middle, corner, edge], light: SectorLight[k] + 0.06));
            facets.Add(Cut([Middle, edge, next], light: SectorLight[k] - 0.06));
        }

        return [.. facets];
    }

    /// <summary>A face shaded from its first corner across to the middle of its opposite edge, sampling the light field.</summary>
    private static Facet Cut(Point[] corners, double light)
    {
        var shape = Shape(corners);
        var (from, to, colors) = Shading(corners, light, drift: default);
        var fill = Gradient(from, to, shape.Bounds, (colors[0], 0), (colors[1], 0.5), (colors[2], 1));
        return new Facet(shape, fill, corners, light);
    }

    /// <summary>
    /// The gradient across a face: from its first corner to the middle of its opposite edge, with the light sampled near
    /// each end and at the centroid. <paramref name="drift"/> moves the light field's points (empty: the still mark).
    /// </summary>
    private static (Point From, Point To, Color[] Colors) Shading(Point[] corners, double light, ReadOnlySpan<Vector> drift)
    {
        var from = corners[0];
        var to = new Point((corners[1].X + corners[2].X) / 2, (corners[1].Y + corners[2].Y) / 2);
        var centroid = Centroid(corners);

        // Sample a little inside the face so a corner shared by several faces (the centre most of all) doesn't bleed
        // one face's light into its neighbours.
        return (from, to,
        [
            Shade(LightAt(Inside(from, centroid), drift), light + 0.04),
            Shade(LightAt(centroid, drift), light),
            Shade(LightAt(Inside(to, centroid), drift), light - 0.04),
        ]);

        static Point Inside(Point point, Point centroid) => point + ((centroid - point) * 0.3);
    }

    private static Point Centroid(Point[] corners) =>
        new((corners[0].X + corners[1].X + corners[2].X) / 3, (corners[0].Y + corners[1].Y + corners[2].Y) / 3);

    /// <summary>Inverse-distance blend of <see cref="LightField"/>, each point moved by its <paramref name="drift"/> if given.</summary>
    private static Color LightAt(Point point, ReadOnlySpan<Vector> drift)
    {
        var x = (point.X - Middle.X) / GemRadius;
        var y = (point.Y - Middle.Y) / GemRadius;
        double r = 0, g = 0, b = 0, total = 0;
        for (var i = 0; i < LightField.Length; i++)
        {
            var (at, color) = LightField[i];
            if (!drift.IsEmpty)
            {
                at += drift[i];
            }

            var distance = ((x - at.X) * (x - at.X)) + ((y - at.Y) * (y - at.Y)) + 0.004;
            var weight = 1 / Math.Pow(distance, 1.6);
            r += color.R * weight;
            g += color.G * weight;
            b += color.B * weight;
            total += weight;
        }

        return Color.FromRgb((byte)Math.Round(r / total), (byte)Math.Round(g / total), (byte)Math.Round(b / total));
    }

    /// <summary>
    /// Brightens or darkens by scaling the channels, which keeps the hue and saturation of the light field; strong light
    /// also lifts slightly toward ice.
    /// </summary>
    private static Color Shade(Color color, double light)
    {
        var scale = 1 + light;
        var shaded = Color.FromRgb(
            (byte)Math.Clamp(Math.Round(color.R * scale), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * scale), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * scale), 0, 255));
        return light > 0 ? Mix(shaded, Highlight, light * 0.25) : shaded;
    }

    private static Color Mix(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(from.A + ((to.A - from.A) * t)),
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }

    /// <summary>Corner <paramref name="index"/> of a pointy-topped hexagon, clockwise from the top.</summary>
    private static Point Corner(double radius, int index) => Middle + (Direction(-90 + (60 * index)) * radius);

    private static Vector Direction(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector(Math.Cos(radians), Math.Sin(radians));
    }

    private static StreamGeometry Hexagon(double radius) => Shape([.. Enumerable.Range(0, 6).Select(k => Corner(radius, k))]);

    private static StreamGeometry Band(double outer, double inner)
    {
        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            draw.SetFillRule(FillRule.EvenOdd);
            foreach (var radius in new[] { outer, inner })
            {
                draw.BeginFigure(Corner(radius, 0), isFilled: true);
                for (var k = 1; k < 6; k++)
                {
                    draw.LineTo(Corner(radius, k));
                }

                draw.EndFigure(isClosed: true);
            }
        }

        return geometry;
    }

    private static StreamGeometry Shape(Point[] corners)
    {
        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            draw.BeginFigure(corners[0], isFilled: true);
            foreach (var corner in corners.Skip(1))
            {
                draw.LineTo(corner);
            }

            draw.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>
    /// Across the whole mark on a slight diagonal, as the light falls in the reference: amber reaches the upper right only
    /// near its corner but wraps the lower right from just past the bottom point.
    /// </summary>
    private static LinearGradientBrush Across((string Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(Middle.X - (OuterRadius * 0.93), Middle.Y - (OuterRadius * 0.37), RelativeUnit.Absolute),
            EndPoint = new RelativePoint(Middle.X + (OuterRadius * 0.93), Middle.Y + (OuterRadius * 0.37), RelativeUnit.Absolute),
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse(color), offset));
        }

        return brush;
    }

    /// <summary>A gradient between two design points, expressed relative to the bounds of the shape it fills.</summary>
    private static LinearGradientBrush Gradient(Point from, Point to, Rect bounds, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint((from.X - bounds.X) / bounds.Width, (from.Y - bounds.Y) / bounds.Height, RelativeUnit.Relative),
            EndPoint = new RelativePoint((to.X - bounds.X) / bounds.Width, (to.Y - bounds.Y) / bounds.Height, RelativeUnit.Relative),
        };
        foreach (var (color, offset) in stops)
        {
            brush.GradientStops.Add(new GradientStop(color, offset));
        }

        return brush;
    }

    private enum LayerKind
    {
        Aura,
        Halo,
        Artwork,
        Flare,
    }

    /// <summary>
    /// One pass of the mark. The auras and halo (behind) and flare (in front) are the splash bloom; the artwork pass is
    /// the crisp geometry between them. Blur radii are in design units, so the glow scales with the mark.
    /// </summary>
    private sealed class LogoLayer(StoneMark owner, LayerKind kind, double blur) : Control
    {
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (blur > 0)
            {
                Effect = new BlurEffect { Radius = blur * Math.Min(e.NewSize.Width, e.NewSize.Height) / 100 };
            }
        }

        public override void Render(DrawingContext context)
        {
            var size = Math.Min(Bounds.Width, Bounds.Height);
            if (size <= 0)
            {
                return;
            }

            var scale = size / 100;
            var origin = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
            using (context.PushTransform(new Matrix(scale, 0, 0, scale, origin.X, origin.Y)))
            {
                switch (kind)
                {
                    case LayerKind.Aura:
                        DrawAura(context);
                        break;
                    case LayerKind.Halo:
                        DrawHalo(context);
                        break;
                    case LayerKind.Flare:
                        DrawFlare(context);
                        break;
                    default:
                        DrawArtwork(context, owner.Variant);
                        break;
                }
            }
        }
    }

    private sealed record Facet(Geometry Shape, IBrush Fill, Point[] Corners, double Light);
}
