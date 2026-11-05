using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Bluestone.Desktop.Controls;

/// <summary>Bluestone's mark: a hexagonal gemstone seen from above, lit from the top left in shades of blue.</summary>
internal sealed class StoneMark : Control
{
    private static readonly Color Darkest = Color.Parse("#061230");
    private static readonly Color Middle = Color.Parse("#2F6FC4");
    private static readonly Color Lightest = Color.Parse("#B4DAFF");

    // Fixed per-facet nudges to the shading, so neighbouring facets catch the light differently.
    private static readonly double[] Sparkle = [0.10, -0.06, 0.04, -0.10, 0.07, -0.03, 0.02, 0.09, -0.08, 0.05, -0.04, 0.06];

    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.Parse("#4DC8E2FF")), 0.75);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var radius = size / 2;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);

        // A pointy-top outline, and a smaller table set off-centre toward the light and turned slightly, as a real cut rarely sits true.
        var outer = Hexagon(centre, radius, 0);
        var table = Hexagon(centre + new Point(-radius * 0.12, -radius * 0.1), radius * 0.52, 9);

        // The crown: each band between the outline and the table is cut into two triangles.
        for (var i = 0; i < 6; i++)
        {
            var next = (i + 1) % 6;
            Facet(context, centre, [outer[i], outer[next], table[i]], Sparkle[2 * i]);
            Facet(context, centre, [outer[next], table[next], table[i]], Sparkle[(2 * i) + 1]);
        }

        // The table, split across by one long facet line: the half facing the light is brighter.
        Facet(context, centre, [table[5], table[0], table[1], table[2]], 0, brightness: 0.82);
        Facet(context, centre, [table[2], table[3], table[4], table[5]], 0, brightness: 0.55);
    }

    private static Point[] Hexagon(Point centre, double radius, double turnDegrees)
    {
        var corners = new Point[6];
        for (var i = 0; i < 6; i++)
        {
            var angle = Math.PI / 180 * (210 + (60 * i) + turnDegrees);
            corners[i] = centre + new Point(radius * Math.Cos(angle), radius * Math.Sin(angle));
        }

        return corners;
    }

    /// <summary>Fills a facet in a blue that is lighter the more it faces the top left.</summary>
    private static void Facet(DrawingContext context, Point centre, Point[] corners, double sparkle, double? brightness = null)
    {
        var mid = new Point(corners.Average(p => p.X), corners.Average(p => p.Y)) - centre;
        var length = Math.Max(1e-6, Math.Sqrt((mid.X * mid.X) + (mid.Y * mid.Y)));
        var facing = ((mid.X * -0.64) + (mid.Y * -0.77)) / length;
        var shade = Math.Clamp(brightness ?? (0.45 + (0.45 * facing) + sparkle), 0, 1);

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

        context.DrawGeometry(new SolidColorBrush(Ramp(shade)), Edge, geometry);
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
