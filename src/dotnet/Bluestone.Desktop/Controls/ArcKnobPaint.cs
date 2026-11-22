using Avalonia;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The Machined knob, shared by every drawn knob: a dark track ring over 270°, a coloured value arc with a restrained
/// glow and a thin highlight, a satin-metal cap with a soft shadow, and a short dark pointer. Bipolar knobs draw
/// the arc from their zero point (the top centre for pan). Brushes, pens and glow layers come from the shared caches; the arc geometries are
/// rebuilt only when the value or size changes (<see cref="ArcKnobCache"/>).
/// </summary>
internal static class ArcKnobPaint
{
    public const double Start = 135;
    public const double Sweep = 270;
    private const double ArcTier = 1;

    private static readonly Color CapTop = Color.Parse("#A3A8B1");
    private static readonly Color CapBottom = Color.Parse("#4A4E56");
    private static readonly Color FaceTop = Color.Parse("#7C818A");
    private static readonly Color FaceBottom = Color.Parse("#5C6068");
    private static readonly Color Pointer = Color.Parse("#101317");

    public static double TrackWidth(double radius) => radius >= 16 ? 3 : 2.5;

    public static void Draw(DrawingContext context, Point centre, double radius, double fraction, double origin, Color arc, bool dim, ArcKnobCache cache)
    {
        if (dim)
        {
            arc = ConsolePaint.Mix(arc, Palette.TextMuted, 0.75);
        }

        var trackRadius = radius - 2;
        var width = TrackWidth(radius);
        var (track, value) = cache.Geometries(centre, trackRadius, fraction, origin);
        context.DrawGeometry(null, ConsolePaint.Pen(ConsolePaint.Darker(Palette.Well, 0.2), width + 1.5, PenLineCap.Round), track);
        context.DrawGeometry(null, ConsolePaint.Pen(ConsolePaint.Lighter(Palette.Panel, 0.08), width, PenLineCap.Round), track);
        if (value is not null)
        {
            if (!dim)
            {
                Glow.Geometry(context, value, width, arc, ArcTier);
            }

            context.DrawGeometry(null, ConsolePaint.Pen(arc, width, PenLineCap.Round), value);
            if (!dim)
            {
                context.DrawGeometry(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Lighter(arc, 0.6), 0.55), 1, PenLineCap.Round), value);
            }
        }

        var capRadius = radius - width - 3.5;
        context.DrawEllipse(ConsolePaint.Brush(ConsolePaint.Alpha(ConsolePaint.Black, 0.45)), null, new Point(centre.X, centre.Y + 1.5), capRadius + 0.5, capRadius + 0.5);
        context.DrawEllipse(ConsolePaint.Vertical(CapTop, CapBottom), null, centre, capRadius, capRadius);
        context.DrawEllipse(ConsolePaint.Vertical(FaceTop, FaceBottom), null, centre, capRadius - 1.6, capRadius - 1.6);
        context.DrawEllipse(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.White, 0.18), 0.8), centre, capRadius - 2.2, capRadius - 2.2);

        var angle = Start + (Sweep * Math.Clamp(fraction, 0, 1));
        context.DrawLine(
            ConsolePaint.Pen(dim ? Palette.TextMuted : Pointer, 2, PenLineCap.Round),
            ConsolePaint.Polar(centre, capRadius * 0.25, angle),
            ConsolePaint.Polar(centre, capRadius - 1.5, angle));
    }
}

/// <summary>A knob's arc geometries, kept until its centre, size or value changes.</summary>
internal sealed class ArcKnobCache
{
    private Point _centre;
    private double _radius = -1;
    private double _fraction = double.NaN;
    private double _origin;
    private StreamGeometry? _track;
    private StreamGeometry? _value;

    public (StreamGeometry Track, StreamGeometry? Value) Geometries(Point centre, double radius, double fraction, double origin)
    {
        if (_track is null || centre != _centre || radius != _radius)
        {
            _track = ConsolePaint.Arc(centre, radius, ArcKnobPaint.Start, ArcKnobPaint.Sweep);
            _fraction = double.NaN;
        }

        if (!fraction.Equals(_fraction) || !origin.Equals(_origin))
        {
            var from = ArcKnobPaint.Start + (ArcKnobPaint.Sweep * Math.Clamp(origin, 0, 1));
            var to = ArcKnobPaint.Start + (ArcKnobPaint.Sweep * Math.Clamp(fraction, 0, 1));
            var sweep = Math.Abs(to - from);
            _value = sweep > 0.5 ? ConsolePaint.Arc(centre, radius, Math.Min(from, to), sweep) : null;
        }

        _centre = centre;
        _radius = radius;
        _fraction = fraction;
        _origin = origin;
        return (_track, _value);
    }
}
