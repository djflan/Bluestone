using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Bluestone.Desktop.Controls;

/// <summary>Bluestone's mark: a hexagonal gemstone seen from above, lit from the top left in shades of blue.</summary>
internal sealed class StoneMark : Control
{
    private const int Sides = 6;

    private static readonly Color Darkest = Color.Parse("#061230");
    private static readonly Color Middle = Color.Parse("#2F6FC4");
    private static readonly Color Lightest = Color.Parse("#B4DAFF");

    // Fixed per-facet nudges to the shading, so neighbouring facets catch the light differently.
    private static readonly double[] Sparkle =
        [0.10, -0.08, 0.05, -0.11, 0.08, -0.03, 0.12, -0.06, 0.02, 0.09, -0.10, 0.06, -0.04];

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

        // The table is 0.618 of the stone's size, set toward the light by 0.146 (0.382²) and turned slightly, as a real cut rarely sits true.
        var outer = Polygon(centre, radius, 0);
        var table = Polygon(centre + (new Point(-0.64, -0.77) * radius * 0.146), radius * 0.618, 9);

        // The crown: each band between the outline and the table is cut into two triangles, three where the light falls and on
        // the darkest band, split at golden-ratio points so the cuts do not halve anything.
        var facet = 0;
        double Next() => Sparkle[facet++ % Sparkle.Length];
        var facing = Enumerable.Range(0, Sides).Select(i => Facing(centre, outer[i], outer[(i + 1) % Sides], table[(i + 1) % Sides], table[i])).ToArray();
        var darkest = Array.IndexOf(facing, facing.Min());
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            if (facing[i] > 0.3 || i == darkest)
            {
                var split = outer[i] + ((outer[next] - outer[i]) * (i == darkest ? 0.618 : 0.382));
                Facet(context, centre, [outer[i], split, table[i]], Next());
                Facet(context, centre, [split, outer[next], table[next]], Next());
                Facet(context, centre, [split, table[next], table[i]], Next());
            }
            else
            {
                Facet(context, centre, [outer[i], outer[next], table[i]], Next());
                Facet(context, centre, [outer[next], table[next], table[i]], Next());
            }
        }

        // The table repeats the cut one level in: a core 0.618 of the table, moved and turned by the same steps again, ringed by
        // a softer band of facets so the middle reads as one face catching the light.
        var core = Polygon(centre + (new Point(-0.64, -0.77) * radius * 0.146 * 1.618), radius * 0.382, 18);
        for (var i = 0; i < Sides; i++)
        {
            var next = (i + 1) % Sides;
            Point[] first = [table[i], table[next], core[i]];
            Point[] second = [table[next], core[next], core[i]];
            Facet(context, centre, first, 0, brightness: 0.62 + (0.22 * Facing(centre, first)) + (0.5 * Next()));
            Facet(context, centre, second, 0, brightness: 0.62 + (0.22 * Facing(centre, second)) + (0.5 * Next()));
        }

        Facet(context, centre, core, 0, brightness: 0.8);
    }

    private static Point[] Polygon(Point centre, double radius, double turnDegrees)
    {
        var corners = new Point[Sides];
        for (var i = 0; i < Sides; i++)
        {
            var angle = Math.PI / 180 * (210 + (360.0 / Sides * i) + turnDegrees);
            corners[i] = centre + new Point(radius * Math.Cos(angle), radius * Math.Sin(angle));
        }

        return corners;
    }

    /// <summary>How directly the middle of <paramref name="corners"/> faces the light, from -1 (away) to 1.</summary>
    private static double Facing(Point centre, params Point[] corners)
    {
        var mid = new Point(corners.Average(p => p.X), corners.Average(p => p.Y)) - centre;
        var length = Math.Max(1e-6, Math.Sqrt((mid.X * mid.X) + (mid.Y * mid.Y)));
        return ((mid.X * -0.64) + (mid.Y * -0.77)) / length;
    }

    /// <summary>Fills a facet in a blue that is lighter the more it faces the top left; facets facing the light vary more.</summary>
    private static void Facet(DrawingContext context, Point centre, Point[] corners, double sparkle, double? brightness = null)
    {
        var facing = Facing(centre, corners);
        var shade = Math.Clamp(brightness ?? (0.45 + (0.45 * facing) + (sparkle * (1 + (1.5 * Math.Max(0, facing))))), 0, 1);

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

        // Each facet darkens away from the light, top left to bottom right.
        var color = Ramp(shade);
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
        context.DrawGeometry(fill, Edge, geometry);
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
