using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// Shared painting for the Machined console controls: cached brushes, pens and gradients, hairlines on the pixel grid,
/// and the recessed surfaces (gutter, well) that hold glyphs, LEDs and readouts. Everything is cached by value, so a
/// control's render allocates nothing after its first frame. UI thread only.
/// </summary>
internal static class ConsolePaint
{
    private static readonly Dictionary<uint, IImmutableSolidColorBrush> SolidBrushes = [];
    private static readonly Dictionary<(uint, double, PenLineCap), IPen> CachedPens = [];
    private static readonly Dictionary<(uint, uint), IImmutableBrush> Verticals = [];
    private static readonly Dictionary<string, Typeface> Typefaces = [];

    public static readonly Color Black = Colors.Black;
    public static readonly Color White = Colors.White;

    public static IImmutableSolidColorBrush Brush(Color color)
    {
        var key = color.ToUInt32();
        if (!SolidBrushes.TryGetValue(key, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            SolidBrushes[key] = brush;
        }

        return brush;
    }

    public static IPen Pen(Color color, double width = 1, PenLineCap cap = PenLineCap.Flat)
    {
        var key = (color.ToUInt32(), width, cap);
        if (!CachedPens.TryGetValue(key, out var pen))
        {
            pen = new ImmutablePen(Brush(color), width, lineCap: cap);
            CachedPens[key] = pen;
        }

        return pen;
    }

    /// <summary>A top-to-bottom gradient across whatever it fills.</summary>
    public static IImmutableBrush Vertical(Color top, Color bottom)
    {
        var key = (top.ToUInt32(), bottom.ToUInt32());
        if (!Verticals.TryGetValue(key, out var brush))
        {
            brush = CreateVertical(top, bottom);
            Verticals[key] = brush;
        }

        return brush;
    }

    /// <summary>A new, uncached <see cref="Vertical"/>, for colours that keep changing (held by the control that draws them).</summary>
    public static IImmutableBrush CreateVertical(Color top, Color bottom) =>
        new ImmutableLinearGradientBrush(
            [new ImmutableGradientStop(0, top), new ImmutableGradientStop(1, bottom)],
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));

    public static Color Alpha(Color color, double alpha) => Palette.Alpha(color, alpha);

    public static Color Mix(Color color, Color target, double amount) => Palette.Blend(color, target, amount);

    public static Color Lighter(Color color, double amount) => Palette.Blend(color, White, amount);

    public static Color Darker(Color color, double amount) => Palette.Blend(color, Black, amount);

    public static Rect Snap(Rect rect) => new(Math.Round(rect.X), Math.Round(rect.Y), Math.Round(rect.Width), Math.Round(rect.Height));

    /// <summary>A 1 DIP hairline on the pixel grid.</summary>
    public static void HLine(DrawingContext context, Color color, double x0, double x1, double y) =>
        context.DrawLine(Pen(color), new Point(x0, Math.Round(y) + 0.5), new Point(x1, Math.Round(y) + 0.5));

    public static void VLine(DrawingContext context, Color color, double x, double y0, double y1) =>
        context.DrawLine(Pen(color), new Point(Math.Round(x) + 0.5, y0), new Point(Math.Round(x) + 0.5, y1));

    /// <summary>A recessed well (readouts, insert list, meter): the darkest surface, a dark rim and a faint lower lip.</summary>
    public static void Well(DrawingContext context, Rect rect, double radius)
    {
        rect = Snap(rect);
        context.DrawRectangle(Brush(Palette.Well), Pen(Alpha(Black, 0.6)), new RoundedRect(rect.Deflate(0.5), radius));
        HLine(context, Alpha(White, 0.04), rect.X + radius, rect.Right - radius, rect.Bottom);
    }

    /// <summary>A recessed gutter (transport, toolbars): darker than its bar, a 1 px inner shadow and a lit lower lip.</summary>
    public static void Gutter(DrawingContext context, Rect rect, double radius)
    {
        rect = Snap(rect);
        var rounded = new RoundedRect(rect, radius);
        context.DrawRectangle(Brush(Palette.Gutter), null, rounded);
        using (context.PushClip(rounded))
        {
            context.FillRectangle(Vertical(Alpha(Black, Palette.GutterDepth), Alpha(Black, 0)), new Rect(rect.X, rect.Y, rect.Width, 4));
            HLine(context, Alpha(Black, 0.6), rect.X, rect.Right, rect.Y);
        }

        context.DrawRectangle(null, Pen(Alpha(Black, 0.5)), new RoundedRect(rect.Deflate(0.5), radius));
        HLine(context, Alpha(White, 0.10), rect.X + radius, rect.Right - radius, rect.Bottom);
    }

    /// <summary>Overhead light on a panel face: a faint cool wash fading out over <paramref name="height"/>.</summary>
    public static void Sheen(DrawingContext context, Rect rect, double height)
    {
        context.FillRectangle(
            Vertical(Alpha(Palette.Sheen, Palette.SheenAlpha), Alpha(Palette.Sheen, 0)),
            new Rect(rect.X, rect.Y, rect.Width, Math.Min(height, rect.Height)));
    }

    public static Point Polar(Point centre, double radius, double degrees) =>
        new(centre.X + (radius * Math.Cos(degrees * Math.PI / 180)), centre.Y + (radius * Math.Sin(degrees * Math.PI / 180)));

    /// <summary>A clockwise arc on screen; angles in degrees from 3 o'clock, y down.</summary>
    public static StreamGeometry Arc(Point centre, double radius, double start, double sweep)
    {
        if (sweep < 0)
        {
            start += sweep;
            sweep = -sweep;
        }

        var geometry = new StreamGeometry();
        using var shape = geometry.Open();
        shape.BeginFigure(Polar(centre, radius, start), false);
        var remaining = sweep;
        var at = start;
        while (remaining > 0.01)
        {
            var step = Math.Min(remaining, 179);
            at += step;
            remaining -= step;
            shape.ArcTo(Polar(centre, radius, at), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        }

        shape.EndFigure(false);
        return geometry;
    }

    /// <summary>The power glyph: an open ring with a stem, <paramref name="size"/> across.</summary>
    public static void Power(DrawingContext context, Point centre, double size, Color color, StreamGeometry ring)
    {
        var pen = Pen(color, Math.Max(1.25, size / 7), PenLineCap.Round);
        var radius = size * 0.40;
        context.DrawGeometry(null, pen, ring);
        context.DrawLine(pen, new Point(centre.X, centre.Y - (radius * 1.15)), new Point(centre.X, centre.Y - (radius * 0.15)));
    }

    /// <summary>Deterministic pseudo-random in [0,1), for brushed grain.</summary>
    public static double Noise(int seed, int i)
    {
        unchecked
        {
            var h = (uint)((seed * 73856093) ^ (i * 19349663));
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (double)0x1000000;
        }
    }

    public static Typeface Typeface(string fontKey, FontWeight weight = FontWeight.Normal)
    {
        var key = fontKey + (int)weight;
        if (!Typefaces.TryGetValue(key, out var typeface))
        {
            typeface = ThemeResources.Find(fontKey) is FontFamily family ? new Typeface(family, FontStyle.Normal, weight) : new Typeface(FontFamily.Default, FontStyle.Normal, weight);
            Typefaces[key] = typeface;
        }

        return typeface;
    }
}
