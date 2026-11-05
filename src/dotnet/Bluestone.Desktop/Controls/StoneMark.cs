using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Bluestone.Desktop.Controls;

/// <summary>Bluestone's mark: a raw sapphire crystal, a few broad faces from frosted pale to deep blue, with a little rock still on it.</summary>
internal sealed class StoneMark : Control
{
    private static readonly Color Darkest = Color.Parse("#050C2E");
    private static readonly Color Middle = Color.Parse("#1F48B0");
    private static readonly Color Lightest = Color.Parse("#C6D8F5");
    private static readonly Color Frost = Color.Parse("#DCE5F0");

    // The crystal in a design box: the outline A–I clockwise from the top left, and the inner ridge points J–M.
    private static readonly Point A = new(36, 8), B = new(66, 4), C = new(74, 12), D = new(80, 44), E = new(70, 70);
    private static readonly Point F = new(50, 90), G = new(26, 86), H = new(14, 64), I = new(20, 36);
    private static readonly Point J = new(56, 30), K = new(58, 56), L = new(40, 22), M = new(34, 60);

    // The far side's ridge, seen faintly through the stone.
    private static readonly Point P = new(42, 44), Q = new(52, 72);
    private static readonly Rect Design = new(14, 4, 66, 86);

    // Glints inside the deep face, and faint fracture streaks through the stone (design units).
    private static readonly (Point At, double Radius)[] Glints =
        [(new(66, 22), 0.9), (new(70, 34), 0.6), (new(62, 40), 0.7), (new(73, 46), 0.5), (new(65, 50), 0.6), (new(60, 28), 0.5),
         (new(75, 40), 0.4), (new(58, 48), 0.4), (new(46, 16), 0.5), (new(72, 62), 0.4)];

    private static readonly (Point From, Point To)[] Streaks = [(new(44, 34), new(54, 52)), (new(48, 30), new(62, 46))];

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var scale = size / Math.Max(Design.Width, Design.Height);
        var origin = new Point((Bounds.Width - (Design.Width * scale)) / 2, (Bounds.Height - (Design.Height * scale)) / 2);
        Point At(Point p) => origin + ((p - Design.TopLeft) * scale);

        // Broad faces, each with its own light: frosted where it catches the sky, deep blue where you see into the stone.
        var topFace = Facet(context, [At(A), At(B), At(J), At(L)], 0.82, frost: 0.45);
        Facet(context, [At(B), At(C), At(J)], 0.72, frost: 0.2);
        var deepFace = Facet(context, [At(C), At(D), At(K), At(J)], 0.2, frost: 0);
        var lowerFace = Facet(context, [At(D), At(E), At(F), At(K)], 0.58, frost: 0.35);
        var frontFace = Facet(context, [At(L), At(J), At(K), At(M)], 0.4, frost: 0);
        Facet(context, [At(A), At(L), At(M), At(H), At(I)], 0.55, frost: 0.15);
        var rockFace = Facet(context, [At(M), At(K), At(F), At(G), At(H)], 0.3, frost: 0);

        // Small chips knocked off the edges, each a little facet of its own.
        Facet(context, [At(A), At(new Point(44, 7)), At(new Point(39, 15))], 0.9, frost: 0.55);
        Facet(context, [At(D), At(new Point(76, 34)), At(new Point(73, 46))], 0.62, frost: 0.3);
        Facet(context, [At(I), At(new Point(24, 30)), At(new Point(26, 42))], 0.66, frost: 0.3);
        Facet(context, [At(F), At(new Point(43, 89)), At(new Point(47, 83))], 0.24, frost: 0.05);
        Facet(context, [At(B), At(new Point(70, 6)), At(new Point(64, 11))], 0.86, frost: 0.4);

        // Shallow secondary facets break up the broad faces.
        Facet(context, [At(L), At(J), At(new Point(47, 46))], 0.48, frost: 0);
        Facet(context, [At(A), At(L), At(new Point(28, 40)), At(I)], 0.63, frost: 0.22);
        Facet(context, [At(E), At(F), At(new Point(64, 74))], 0.5, frost: 0.28);

        // Light gathers inside the deep face; fine needles of "silk" lie across it at 60°, as in natural sapphire.
        using (context.PushGeometryClip(deepFace))
        {
            context.DrawEllipse(new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.Parse("#504C8DDB"), 0), new GradientStop(Color.Parse("#004C8DDB"), 1) },
            }, null, At(new Point(68, 34)), scale * 10, scale * 12);

            var silk = new Pen(new SolidColorBrush(Color.Parse("#30DCE5F0")), Math.Max(0.4, scale * 0.25), lineCap: PenLineCap.Round);
            for (var i = 0; i < 8; i++)
            {
                var from = new Point(61 + (i * 2.2), 18 + (i * 3.6));
                context.DrawLine(silk, At(from), At(from + new Point(3, 5.2)));
            }
        }

        // Sapphire grows in bands; lighter zones run across the deep face.
        using (context.PushGeometryClip(deepFace))
        {
            var band = new Pen(new SolidColorBrush(Color.Parse("#283C78D8")), scale * 2.2);
            context.DrawLine(band, At(new Point(60, 14)), At(new Point(82, 30)));
            context.DrawLine(band, At(new Point(56, 36)), At(new Point(82, 54)));
        }

        // Frosted faces are rough: a fine scatter of paler grains.
        Speckle(context, topFace, At, new Rect(36, 4, 30, 26), seed: 3, count: 22, "#55F4F8FF", 0.35 * scale);
        Speckle(context, lowerFace, At, new Rect(50, 44, 30, 46), seed: 5, count: 22, "#45F4F8FF", 0.35 * scale);

        // A few dark inclusions deep in the front face.
        Speckle(context, frontFace, At, new Rect(36, 24, 22, 34), seed: 11, count: 5, "#90050C2E", 0.5 * scale);

        // The back faces show through, as if the stone were partly clear: light where they catch the light, darker below.
        Ghost(context, [At(I), At(P), At(Q), At(G), At(H)], "#18DCE5F0");
        Ghost(context, [At(C), At(D), At(Q), At(P)], "#26C6D8F5");
        Ghost(context, [At(D), At(E), At(F), At(G), At(Q)], "#30050C2E");
        var backEdge = new Pen(new SolidColorBrush(Color.Parse("#38DCE5F0")), Math.Max(0.5, scale * 0.5), lineCap: PenLineCap.Round);
        foreach (var (from, to) in new[] { (I, P), (C, P), (P, Q), (D, Q), (G, Q) })
        {
            context.DrawLine(backEdge, At(from), At(to));
        }

        // A little of the host rock still clings to the lower left.
        using (context.PushGeometryClip(rockFace))
        {
            foreach (var (centre, radius) in new[] { (new Point(28, 76), 11.0), (new Point(38, 84), 8.0), (new Point(20, 68), 6.0) })
            {
                var r = radius * scale;
                context.DrawEllipse(new RadialGradientBrush
                {
                    GradientStops = { new GradientStop(Color.Parse("#A0745E4A"), 0), new GradientStop(Color.Parse("#00745E4A"), 1) },
                }, null, At(centre), r, r * 0.8);
            }
        }

        // The rock is gritty: darker and paler grains through it.
        Speckle(context, rockFace, At, new Rect(16, 62, 30, 26), seed: 17, count: 24, "#A04A3A2C", 0.45 * scale);
        Speckle(context, rockFace, At, new Rect(16, 62, 30, 26), seed: 19, count: 14, "#80B89E80", 0.35 * scale);

        // The ridges that face the light catch a thin bright edge, fading along their length.
        foreach (var (from, to) in new[] { (B, J), (L, J), (J, K) })
        {
            context.DrawLine(new Pen(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(At(from), RelativeUnit.Absolute),
                EndPoint = new RelativePoint(At(to), RelativeUnit.Absolute),
                GradientStops = { new GradientStop(Color.Parse("#90F2F7FF"), 0), new GradientStop(Color.Parse("#00F2F7FF"), 1) },
            }, Math.Max(0.5, scale * 0.5), lineCap: PenLineCap.Round), At(from), At(to));
        }

        var streak = new Pen(new SolidColorBrush(Color.Parse("#40DCE5F0")), Math.Max(0.5, scale * 0.6), lineCap: PenLineCap.Round);
        foreach (var (from, to) in Streaks)
        {
            context.DrawLine(streak, At(from), At(to));
        }

        // A hairline fracture in the front face, branching once.
        var fracture = new Pen(new SolidColorBrush(Color.Parse("#50DCE5F0")), Math.Max(0.4, scale * 0.3), lineCap: PenLineCap.Round);
        context.DrawLine(fracture, At(new Point(40, 30)), At(new Point(44, 38)));
        context.DrawLine(fracture, At(new Point(44, 38)), At(new Point(43, 45)));
        context.DrawLine(fracture, At(new Point(44, 38)), At(new Point(50, 41)));

        // The edges turning away from the light fall into shadow.
        using (context.PushGeometryClip(Shape([At(A), At(B), At(C), At(D), At(E), At(F), At(G), At(H), At(I)])))
        {
            context.DrawGeometry(null, new Pen(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#00050C2E"), 0.45), new GradientStop(Color.Parse("#A0050C2E"), 1) },
            }, scale * 3), Shape([At(A), At(B), At(C), At(D), At(E), At(F), At(G), At(H), At(I)]));
        }

        var glint = new SolidColorBrush(Color.Parse("#D0F2F7FF"));
        foreach (var (at, radius) in Glints)
        {
            context.DrawEllipse(glint, null, At(at), radius * scale, radius * scale);
        }

        // One star of light where the top face meets the ridge.
        var star = new Pen(new SolidColorBrush(Color.Parse("#B0FFFFFF")), Math.Max(0.5, scale * 0.4), lineCap: PenLineCap.Round);
        var spark = At(new Point(54, 12));
        context.DrawLine(star, spark - new Point(scale * 4, 0), spark + new Point(scale * 4, 0));
        context.DrawLine(star, spark - new Point(0, scale * 2.5), spark + new Point(0, scale * 2.5));
        context.DrawEllipse(glint, null, spark, scale * 0.8, scale * 0.8);
    }

    /// <summary>Scatters <paramref name="count"/> fixed grains over <paramref name="area"/> (design units), kept inside <paramref name="face"/>.</summary>
    private static void Speckle(DrawingContext context, Geometry face, Func<Point, Point> at, Rect area, int seed, int count, string color, double radius)
    {
#pragma warning disable CA5394 // Decorative grain from a fixed seed, not security.
        var random = new Random(seed);
        var brush = new SolidColorBrush(Color.Parse(color));
        using (context.PushGeometryClip(face))
        {
            for (var i = 0; i < count; i++)
            {
                var point = new Point(area.X + (random.NextDouble() * area.Width), area.Y + (random.NextDouble() * area.Height));
                var r = radius * (0.6 + random.NextDouble());
                context.DrawEllipse(brush, null, at(point), r, r);
            }
        }
#pragma warning restore CA5394
    }

    /// <summary>A back face seen through the stone: a faint wash of <paramref name="color"/> (#AARRGGBB).</summary>
    private static void Ghost(DrawingContext context, Point[] corners, string color) =>
        context.DrawGeometry(new SolidColorBrush(Color.Parse(color)), null, Shape(corners));

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

    /// <summary>Fills a face in the blue for <paramref name="brightness"/>, washed toward frosted white by <paramref name="frost"/>.</summary>
    private static StreamGeometry Facet(DrawingContext context, Point[] corners, double brightness, double frost)
    {
        var geometry = Shape(corners);

        // Each face darkens away from the light, top left to bottom right.
        var color = Lerp(Ramp(Math.Clamp(brightness, 0, 1)), Frost, frost);
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Lerp(color, Lightest, 0.2), 0),
                new GradientStop(color, 0.382),
                new GradientStop(Lerp(color, Darkest, 0.6), 1),
            },
        };
        // Stroked in its own fill so neighbouring faces meet without a hairline seam.
        context.DrawGeometry(fill, new Pen(fill, 0.75, lineJoin: PenLineJoin.Round), geometry);
        return geometry;
    }

    private static Color Ramp(double shade) =>
        shade < 0.5 ? Lerp(Darkest, Middle, shade * 2) : Lerp(Middle, Lightest, (shade - 0.5) * 2);

    private static Color Lerp(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + ((to.R - from.R) * t)),
        (byte)(from.G + ((to.G - from.G) * t)),
        (byte)(from.B + ((to.B - from.B) * t)));

    /// <summary>The mark drawn at <paramref name="pixels"/> square, for window and taskbar icons.</summary>
    public static WindowIcon CreateIcon(int pixels = 256)
    {
        var mark = new StoneMark { Width = pixels, Height = pixels };
        mark.Measure(new Size(pixels, pixels));
        mark.Arrange(new Rect(0, 0, pixels, pixels));
        var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels));
        bitmap.Render(mark);
        return new WindowIcon(bitmap);
    }
}
