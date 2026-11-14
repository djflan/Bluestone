using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>How much a signal indicator shows: the junction's slim level, a mixer-strip level, or an expanded view's.</summary>
internal enum SignalIndicatorSize
{
    /// <summary>The Device View junction's indicator: 1 px segments, no scale.</summary>
    Compact,

    /// <summary>2 dB segments with a 0 dB tick, for mixer strips and device panels.</summary>
    Medium,

    /// <summary>1 dB segments with a dB scale, a held-peak readout, and a clip light that stays on until cleared.</summary>
    Large,
}

/// <summary>The shape of a Medium or Large level's LEDs: segments, or round dots (with their own, coarser pitch).</summary>
internal enum LedShape
{
    Bar,
    Round,
}

/// <summary>The colours a signal indicator draws with: the level's gradient, the MIDI lamp, and the scale's text.</summary>
internal readonly record struct SignalIndicatorColors(Color Low, Color Mid, Color Hot, Color Clip, Color Lamp, Color Text);

/// <summary>
/// The shape of a level: one column's segment height and the gap between them, how wide each of its two columns is and the
/// gap between them, and the height the segments are fitted into. The segment count follows from those, as the seam's does.
/// </summary>
internal readonly record struct SignalLevelMetrics(double LevelHeight, double BarWidth, double BarGap, double SegmentHeight, double SegmentGap)
{
    public double Segment => Math.Max(1, SegmentHeight);

    public double Gap => Math.Max(0, SegmentGap);

    /// <summary>How many segments fit in <see cref="LevelHeight"/>.</summary>
    public int Count => Math.Max(1, (int)Math.Floor((LevelHeight + Gap) / (Segment + Gap)));

    /// <summary>The height the segments take: the top one is the level's top edge.</summary>
    public double Height => (Count * Segment) + ((Count - 1) * Gap);

    /// <summary>Both columns, side by side.</summary>
    public double Width => (2 * BarWidth) + BarGap;

    /// <summary>The decibels one segment spans.</summary>
    public double SegmentDecibels => (MeterLevels.CeilingDecibels - MeterLevels.FloorDecibels) / Count;

    /// <summary>The top of segment <paramref name="index"/> (0 at the bottom), for a level whose bottom edge is <paramref name="bottom"/>.</summary>
    public double SegmentTop(double bottom, int index) => bottom - ((index + 1) * Segment) - (index * Gap);

    /// <summary>Whether <paramref name="decibels"/> falls on the boundary between two segments (or the level's top or bottom).</summary>
    public bool LandsOnEdge(double decibels)
    {
        var edge = MeterLevels.Fraction(decibels) * Count;
        return Math.Abs(edge - Math.Round(edge)) < 1e-9;
    }

    /// <summary>The middle of the gap at <paramref name="decibels"/>, which should be on a segment boundary.</summary>
    public double EdgeY(double bottom, double decibels)
    {
        var edge = (int)Math.Round(MeterLevels.Fraction(decibels) * Count);
        return edge <= 0 ? bottom : SegmentTop(bottom, edge - 1) - (Gap / 2);
    }
}

/// <summary>
/// What a level shows, quantised to what it draws: how many segments are lit on each side, the held segment, whether the
/// top (over) segment is lit, whether it is silent or holding a peak, the lamp's glow, the clip light, and the readout (in
/// tenths of a dB, or <see cref="SignalIndicatorRenderer.NoReadout"/>). A view redraws only when this changes.
/// </summary>
internal readonly record struct SignalDisplayState(
    int LitLeft,
    int LitRight,
    int HeldLeft,
    int HeldRight,
    bool OverLeft,
    bool OverRight,
    bool Silent,
    bool Holds,
    double Glow,
    bool ClipLatched,
    int Readout);

/// <summary>
/// Draws a signal indicator (a segmented stereo level and a MIDI lamp) for <see cref="JunctionSeam"/> and
/// <see cref="SignalIndicator"/>. Compact draws each segment, as the seam always has; Medium and Large draw from cached
/// geometry and brushes, so their draw calls do not grow with the segment count. Drawing allocates nothing once cached.
/// </summary>
internal sealed class SignalIndicatorRenderer
{
    /// <summary>The readout shows a dash: nothing is playing.</summary>
    public const int NoReadout = int.MinValue;

    /// <summary>The dB marks a Large level's scale labels, below full scale.</summary>
    public static readonly double[] ScaleMarks = [0, -6, -12, -24, -48];

    private const double LowFraction = 0.4;
    private const double MidFraction = 0.62;
    private const double NoteWidth = 12;
    private const double NoteHeight = 12;
    private const double IdleOpacity = 0.55;

    // Room round a level's cached columns for the halo (a unit each side), and how far the readout's clip tint is lightened.
    private const double LevelPad = 1;
    private const double ReadoutOverLighten = 0.25;
    private static readonly Color NoteOff = Palette.Lighten(Palette.Border, 0.06);
    private static readonly IBrush NoteOffBrush = new SolidColorBrush(NoteOff);
    private static readonly double HotFraction = MeterLevels.Fraction(-6);
    private static readonly double ZeroFraction = MeterLevels.Fraction(0);

    // A MIDI connector in a 12 x 12 box: the ring with its key notch at the top, and the five pins in a horseshoe.
    private static readonly Geometry NoteGlyph = new GeometryGroup
    {
        FillRule = FillRule.EvenOdd,
        Children =
        [
            new EllipseGeometry(new Rect(0, 0, 12, 12)),
            new EllipseGeometry(new Rect(1.25, 1.25, 9.5, 9.5)),
            new RectangleGeometry(new Rect(5.1, 0, 1.8, 1.25)),
            Pin(180),
            Pin(135),
            Pin(90),
            Pin(45),
            Pin(0),
        ],
    };

    private SignalIndicatorColors _colors;
    private (SolidColorBrush[] Lit, SolidColorBrush[] Halo, SolidColorBrush[] Dim)? _segments;
    private SolidColorBrush? _lampBrush;
    private RadialGradientBrush? _haloBrush;
    private LevelCache? _level;
    private SolidColorBrush? _textBrush;
    private SolidColorBrush? _readoutBrush;
    private SolidColorBrush? _latchOn;
    private SolidColorBrush? _latchHalo;
    private SolidColorBrush? _latchOff;
    private (FontFamily Family, double Size, Color Color)? _font;
    private FormattedText[]? _labels;
    private (double X, double Bottom, SignalLevelMetrics Metrics, Geometry Geometry)? _labelGeometry;
    private Geometry? _readout;
    private (int Tenths, double Center, double Top) _readoutKey = (NoReadout, double.NaN, double.NaN);
    private double _readoutWidth;

    /// <summary>The colours to draw with. Setting different ones drops every cached brush.</summary>
    public SignalIndicatorColors Colors
    {
        get => _colors;
        set
        {
            if (_colors != value)
            {
                _colors = value;
                _haloBrush = null;
                _lampBrush = null;
                _segments = null;
                ReleaseLevel();
                _textBrush = null;
                _latchOn = null;
                _latchHalo = null;
                _latchOff = null;
                _labels = null;
                _labelGeometry = null;
                _readout = null;
                _readoutBrush = null;
            }
        }
    }

    /// <summary>
    /// What a level and lamp show, quantised to <paramref name="count"/> segments. Without levels, nothing is lit. The held
    /// segment and the over segment count only where they add to what is lit.
    /// </summary>
    public static SignalDisplayState State(MeterLevels? levels, int count, JunctionLevelStyle style, double glow, bool clipLatched, int readout)
    {
        if (levels is null)
        {
            return new SignalDisplayState(0, 0, -1, -1, false, false, true, false, glow, clipLatched, readout);
        }

        var silent = !levels.IsActive || Math.Max(levels.PeakLeft, levels.PeakRight) <= MeterLevels.FloorDecibels;
        var holds = levels.IsActive && style == JunctionLevelStyle.RmsAndPeak && Math.Max(levels.HoldLeft, levels.HoldRight) > MeterLevels.FloorDecibels;
        if (silent && !holds)
        {
            return new SignalDisplayState(0, 0, -1, -1, false, false, true, false, glow, clipLatched, readout);
        }

        var peakOnly = style == JunctionLevelStyle.Peak;
        var (litLeft, heldLeft, overLeft) = Side(levels.RmsLeft, levels.PeakLeft, levels.HoldLeft);
        var (litRight, heldRight, overRight) = Side(levels.RmsRight, levels.PeakRight, levels.HoldRight);
        return new SignalDisplayState(litLeft, litRight, heldLeft, heldRight, overLeft, overRight, silent, holds, glow, clipLatched, readout);

        (int Lit, int Held, bool Over) Side(double rms, double peak, double hold)
        {
            var lit = silent ? 0 : Math.Min(count, (int)Math.Round(MeterLevels.Fraction(peakOnly ? peak : rms) * count));
            var held = peakOnly || hold <= MeterLevels.FloorDecibels ? -1 : Math.Min(count, (int)Math.Ceiling(MeterLevels.Fraction(hold) * count)) - 1;
            return (lit, held < lit ? -1 : held, peak > 0 && lit < count);
        }
    }

    /// <summary>The held peak a Large readout shows, in tenths of a dB, or <see cref="NoReadout"/> when nothing is playing.</summary>
    public static int ReadoutValue(MeterLevels? levels)
    {
        if (levels is not { IsActive: true })
        {
            return NoReadout;
        }

        var hold = Math.Max(levels.HoldLeft, levels.HoldRight);
        return hold <= MeterLevels.FloorDecibels ? NoReadout : (int)Math.Round(hold * 10);
    }

    /// <summary>A readout's words: "-12.4", "+1.2" (over full scale, with its sign), "0.0", or "—".</summary>
    public static string ReadoutText(int tenths) => tenths switch
    {
        NoReadout => "—",
        > 0 => string.Create(CultureInfo.InvariantCulture, $"+{tenths / 10.0:0.0}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{tenths / 10.0:0.0}"),
    };

    /// <summary>
    /// What an indicator carries now, in words, for tooltips and screen readers: its label (when it has one), the notes,
    /// the level, where it is also sent, and a clip light that is on. The seam and the standalone indicator share it.
    /// </summary>
    public static string Describe(string? label, bool carriesNotes, bool notesHeld, bool carriesAudio, MeterLevels? levels, string? routes, bool clipLatched)
    {
        var parts = new List<string>();
        if (label is not null)
        {
            parts.Add(label);
        }

        if (carriesNotes)
        {
            parts.Add(notesHeld ? "notes held" : "no notes");
        }

        if (carriesAudio)
        {
            var peak = levels is null ? MeterLevels.FloorDecibels : Math.Max(levels.PeakLeft, levels.PeakRight);
            parts.Add(levels is not { IsActive: true } || peak <= MeterLevels.FloorDecibels ? "silent"
                : string.Create(CultureInfo.InvariantCulture, $"peak {peak:0.0} dB{(peak > 0 ? ", clipping" : string.Empty)}"));
        }

        if (!string.IsNullOrEmpty(routes))
        {
            parts.Add($"also sent to {routes}");
        }

        if (clipLatched)
        {
            parts.Add("clip light on — press Enter or click to clear");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>The height of a line of the scale's text, for layout.</summary>
    public double TextHeight(FontFamily family, double size)
    {
        EnsureText(family, size);
        return Math.Ceiling(_labels![0].Height);
    }

    /// <summary>The width the widest readout ("-00.0") takes, so layout can keep it centred over the level.</summary>
    public double ReadoutWidth(FontFamily family, double size)
    {
        EnsureText(family, size);
        return _readoutWidth;
    }

    /// <summary>The MIDI lamp in <paramref name="box"/>: unlit, or glowing by <paramref name="glow"/> (0 to 1).</summary>
    public void DrawLamp(DrawingContext context, Rect box, double glow)
    {
        var height = box.Height;
        var scale = height / NoteHeight;
        var color = _colors.Lamp;
        if (glow > 0)
        {
            // Lit, it glows: a soft halo behind it, and the note itself fading back to unlit as the notes let go.
            _haloBrush ??= new RadialGradientBrush
            {
                GradientStops = [new GradientStop(Color.FromArgb(120, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(40, color.R, color.G, color.B), 0.45), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)],
            };
            using (context.PushOpacity(glow))
            {
                context.DrawEllipse(_haloBrush, null, box.Center, height * 0.75, height * 0.75);
            }

            _lampBrush ??= new SolidColorBrush(color);
            _lampBrush.Color = Palette.Mix(NoteOff, Palette.Lighten(color, 0.12), glow);
        }

        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(box.X, box.Y)))
        {
            context.DrawGeometry(glow > 0 ? _lampBrush : NoteOffBrush, null, NoteGlyph);
        }
    }

    /// <summary>The lamp's box, as wide as the glyph is for <paramref name="height"/>, centred on <paramref name="center"/>.</summary>
    public static Rect LampBox(double center, double top, double height)
    {
        var width = height * NoteWidth / NoteHeight;
        return new Rect(center - (width / 2), top, width, height);
    }

    /// <summary>
    /// The Compact level, segment by segment: the left column's left edge at <paramref name="left"/> and its bottom edge at
    /// <paramref name="bottom"/>.
    /// </summary>
    public void DrawCompactLevel(DrawingContext context, double left, double bottom, SignalLevelMetrics metrics, MeterLevels levels, JunctionLevelStyle style)
    {
        var bar = metrics.BarWidth;
        var segment = metrics.Segment;
        var gap = metrics.Gap;
        var count = metrics.Count;
        var segments = Segments(count);
        var silent = !levels.IsActive || Math.Max(levels.PeakLeft, levels.PeakRight) <= MeterLevels.FloorDecibels;

        // The average mode holds the peak for a moment (MeterLevels' hold), so it stays up through a quiet reading.
        var holds = levels.IsActive && style == JunctionLevelStyle.RmsAndPeak && Math.Max(levels.HoldLeft, levels.HoldRight) > MeterLevels.FloorDecibels;

        // Like a hardware LED meter: every segment is there, unlit ones only a dim tint of their colour; idle, dimmer still.
        using (context.PushOpacity(silent ? IdleOpacity : 1))
        {
            for (var i = 0; i < count; i++)
            {
                var y = SegmentTop(i);
                context.DrawRectangle(segments.Dim[i], null, new Rect(left, y, bar, segment), 0.5, 0.5);
                context.DrawRectangle(segments.Dim[i], null, new Rect(left + bar + metrics.BarGap, y, bar, segment), 0.5, 0.5);
            }
        }

        if (silent && !holds)
        {
            return;
        }

        Side(levels.RmsLeft, levels.PeakLeft, levels.HoldLeft, left);
        Side(levels.RmsRight, levels.PeakRight, levels.HoldRight, left + bar + metrics.BarGap);

        double SegmentTop(int index) => bottom - ((index + 1) * segment) - (index * gap);

        void Side(double rms, double peak, double hold, double x)
        {
            // Lit up to the RMS with the held peak as one segment above it (or lit to the peak), and the top segment while
            // the peak is over full scale. An indicator shows what is happening now: unlike the mixer's clip light, the
            // over does not latch.
            var clipped = peak > 0;
            var peakOnly = style == JunctionLevelStyle.Peak;
            var lit = silent ? 0 : (int)Math.Round(MeterLevels.Fraction(peakOnly ? peak : rms) * count);
            var held = peakOnly || hold <= MeterLevels.FloorDecibels ? -1 : Math.Min(count, (int)Math.Ceiling(MeterLevels.Fraction(hold) * count)) - 1;
            for (var i = 0; i < count; i++)
            {
                if (i < lit || i == held || (clipped && i == count - 1))
                {
                    var cell = new Rect(x, SegmentTop(i), bar, segment);
                    context.DrawRectangle(segments.Halo[i], null, cell.Inflate(new Thickness(1, 0.4)), 1, 1);
                    context.DrawRectangle(segments.Lit[i], null, cell, 0.5, 0.5);
                }
            }
        }
    }

    /// <summary>
    /// A Medium or Large level, its top-left corner at <paramref name="origin"/>, drawn from <paramref name="state"/>. Its
    /// dim, glowing, and lit columns are drawn once, at <paramref name="scale"/> pixels a unit, into cached bitmaps; each
    /// frame draws the dim column whole, then slices of the others (the lit runs, the held segments, and the over segments),
    /// each through a rectangle clip. Bitmaps and solid clips cost Avalonia nothing a frame, and the number of calls does
    /// not depend on the segment count (a gradient fill would copy its stops every frame).
    /// </summary>
    public void DrawLevel(DrawingContext context, Point origin, SignalLevelMetrics metrics, LedShape shape, in SignalDisplayState state, double scale)
    {
        var cache = Level(metrics, shape, scale);
        var whole = new Rect(-LevelPad, -LevelPad, metrics.Width + (2 * LevelPad), metrics.Height + (2 * LevelPad));
        using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
        {
            using (context.PushOpacity(state.Silent ? IdleOpacity : 1))
            {
                context.DrawImage(cache.Dim, Pixels(cache.Dim), whole);
            }

            if (state.Silent && !state.Holds)
            {
                return;
            }

            Runs(context, cache.Halo, whole, metrics, state, 1, 0.4);
            Runs(context, cache.Lit, whole, metrics, state, 0, 0);
        }
    }

    /// <summary>A small tick on the scale, <paramref name="length"/> long, at the gap for <paramref name="decibels"/>.</summary>
    public void DrawTick(DrawingContext context, double x, double bottom, SignalLevelMetrics metrics, double decibels, double length)
    {
        _textBrush ??= new SolidColorBrush(_colors.Text);
        context.FillRectangle(_textBrush, new Rect(x, metrics.EdgeY(bottom, decibels) - 0.5, length, 1));
    }

    /// <summary>
    /// The Large scale's labels (0, 6, 12, 24, 48), left-aligned at <paramref name="x"/>, each centred on its mark: their
    /// outlines are built once (for the font, colours, and place) and filled with a cached brush, as text costs a frame.
    /// </summary>
    public void DrawScaleLabels(DrawingContext context, double x, double bottom, SignalLevelMetrics metrics, FontFamily family, double size)
    {
        EnsureText(family, size);
        if (_labelGeometry is not { } cached || cached.X != x || cached.Bottom != bottom || cached.Metrics != metrics)
        {
            var group = new GeometryGroup { FillRule = FillRule.NonZero };
            for (var i = 0; i < ScaleMarks.Length; i++)
            {
                var label = _labels![i];
                if (label.BuildGeometry(new Point(x, Math.Round(metrics.EdgeY(bottom, ScaleMarks[i]) - (label.Height / 2)))) is { } outline)
                {
                    group.Children.Add(outline);
                }
            }

            cached = (x, bottom, metrics, group);
            _labelGeometry = cached;
        }

        context.DrawGeometry(_textBrush, null, cached.Geometry);
    }

    /// <summary>
    /// The held-peak readout, centred on <paramref name="center"/>. Its outline is rebuilt only when its value (or place)
    /// changes. Over full scale it keeps its sign and takes a light tint of the clip colour, bright enough to read.
    /// </summary>
    public void DrawReadout(DrawingContext context, double center, double top, int tenths, FontFamily family, double size)
    {
        EnsureText(family, size);
        _readoutBrush ??= new SolidColorBrush(ReadoutOverColor(_colors.Clip));
        var over = tenths is > 0 and not NoReadout;
        if (_readout is null || _readoutKey != (tenths, center, top))
        {
            var text = new FormattedText(ReadoutText(tenths), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family), size, _textBrush);
            _readout = text.BuildGeometry(new Point(Math.Max(0, Math.Round(center - (text.Width / 2))), top)) ?? new GeometryGroup();
            _readoutKey = (tenths, center, top);
        }

        context.DrawGeometry(over ? _readoutBrush : _textBrush, null, _readout);
    }

    /// <summary>The readout's colour over full scale: the clip colour lightened until it reads at 4.5:1 on a panel.</summary>
    public static Color ReadoutOverColor(Color clip) => Palette.Lighten(clip, ReadoutOverLighten);

    /// <summary>The clip light: dim, or lit with a halo once a peak has passed full scale, until it is cleared.</summary>
    public void DrawClipLatch(DrawingContext context, Rect box, bool latched)
    {
        var clip = _colors.Clip;
        if (latched)
        {
            _latchHalo ??= new SolidColorBrush(Color.FromArgb(95, clip.R, clip.G, clip.B));
            _latchOn ??= new SolidColorBrush(Palette.Lighten(clip, 0.12));
            context.DrawRectangle(_latchHalo, null, box.Inflate(new Thickness(1, 1)), 1.5, 1.5);
            context.DrawRectangle(_latchOn, null, box, 1, 1);
        }
        else
        {
            _latchOff ??= new SolidColorBrush(Palette.Mix(Palette.MeterTrack, clip, 0.18));
            context.DrawRectangle(_latchOff, null, box, 1, 1);
        }
    }

    // Slices of a cached column for both sides, inflated by (dx, dy) for the halo: the run from the bottom, the held segment,
    // and the top segment while the peak is over full scale. Each is the whole column drawn through a rectangle clip.
    private static void Runs(DrawingContext context, RenderTargetBitmap column, Rect whole, SignalLevelMetrics metrics, in SignalDisplayState state, double dx, double dy)
    {
        var bottom = metrics.Height;
        Column(context, state.LitLeft, state.HeldLeft, state.OverLeft, 0);
        Column(context, state.LitRight, state.HeldRight, state.OverRight, metrics.BarWidth + metrics.BarGap);

        void Column(DrawingContext context, int lit, int held, bool over, double x)
        {
            var left = x - dx;
            var width = metrics.BarWidth + (2 * dx);
            if (lit > 0)
            {
                var top = metrics.SegmentTop(bottom, lit - 1) - dy;
                Slice(new Rect(left, top, width, bottom + dy - top));
            }

            if (held >= 0)
            {
                Slice(new Rect(left, metrics.SegmentTop(bottom, held) - dy, width, metrics.Segment + (2 * dy)));
            }

            if (over)
            {
                Slice(new Rect(left, metrics.SegmentTop(bottom, metrics.Count - 1) - dy, width, metrics.Segment + (2 * dy)));
            }

            void Slice(Rect rect)
            {
                using (context.PushClip(rect))
                {
                    context.DrawImage(column, Pixels(column), whole);
                }
            }
        }
    }

    // A bitmap's whole area as a source rectangle: Avalonia's Skia backend takes source rectangles in pixels (the bitmap's
    // Size is in units, half its pixels at 2x, and would draw only its top-left quarter).
    private static Rect Pixels(RenderTargetBitmap bitmap) => new(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

    // A pin of the connector, at an angle round its centre (0 to the right, 90 straight down).
    private static EllipseGeometry Pin(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var center = new Point(6 + (3.1 * Math.Cos(radians)), 6.4 + (3.1 * Math.Sin(radians)) - 0.9);
        return new EllipseGeometry(new Rect(center.X - 0.85, center.Y - 0.85, 1.7, 1.7));
    }

    // The colours a segment is lit, glows, and rests in, from its colour on the level's gradient.
    private static Color LitColor(Color color) => Palette.Lighten(color, 0.12);

    private static Color HaloColor(Color color) => Color.FromArgb(95, color.R, color.G, color.B);

    private static Color DimColor(Color color) => Palette.Mix(Palette.MeterTrack, color, 0.3);

    // A brush that changes colour at each boundary between segments, from the bottom up, in the level's own coordinates
    // (its top at 0, its bottom at metrics.Height), so one fill colours every segment it covers.
    private LinearGradientBrush Stepped(SignalLevelMetrics metrics, Func<Color, Color> shade)
    {
        var count = metrics.Count;
        var height = metrics.Height;
        var stops = new GradientStops { new GradientStop(shade(SegmentColor((0.5) / count)), 0) };
        for (var i = 0; i < count - 1; i++)
        {
            var offset = (((i + 1) * metrics.Segment) + ((i + 0.5) * metrics.Gap)) / height;
            stops.Add(new GradientStop(shade(SegmentColor((i + 0.5) / count)), offset));
            stops.Add(new GradientStop(shade(SegmentColor((i + 1.5) / count)), offset));
        }

        stops.Add(new GradientStop(shade(SegmentColor((count - 0.5) / count)), 1));
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, height, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            GradientStops = stops,
        };
    }

    private LevelCache Level(SignalLevelMetrics metrics, LedShape shape, double scale)
    {
        if (_level is { } cached && cached.Metrics == metrics && cached.Shape == shape && cached.Scale == scale)
        {
            return cached;
        }

        var segments = new GeometryGroup { FillRule = FillRule.NonZero };
        var halos = new GeometryGroup { FillRule = FillRule.NonZero };
        var bottom = metrics.Height;
        for (var column = 0; column < 2; column++)
        {
            var x = column * (metrics.BarWidth + metrics.BarGap);
            for (var i = 0; i < metrics.Count; i++)
            {
                var cell = new Rect(x, metrics.SegmentTop(bottom, i), metrics.BarWidth, metrics.Segment);
                if (shape == LedShape.Round)
                {
                    segments.Children.Add(new EllipseGeometry(cell));
                    halos.Children.Add(new EllipseGeometry(cell.Inflate(1)));
                }
                else
                {
                    segments.Children.Add(new RectangleGeometry(cell, 0.5, 0.5));
                    halos.Children.Add(new RectangleGeometry(cell.Inflate(new Thickness(1, 0.4)), 1, 1));
                }
            }
        }

        ReleaseLevel();
        _level = new LevelCache(
            metrics,
            shape,
            scale,
            Column(segments, Stepped(metrics, DimColor)),
            Column(halos, Stepped(metrics, HaloColor)),
            Column(segments, Stepped(metrics, LitColor)));
        return _level;

        // The column's every segment, filled with the brush, in a bitmap with room for the halo round it.
        RenderTargetBitmap Column(Geometry geometry, IBrush brush)
        {
            var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling((metrics.Width + (2 * LevelPad)) * scale), (int)Math.Ceiling((metrics.Height + (2 * LevelPad)) * scale)),
                new Vector(96 * scale, 96 * scale));
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushTransform(Matrix.CreateTranslation(LevelPad, LevelPad)))
            {
                context.DrawGeometry(brush, null, geometry);
            }

            return bitmap;
        }
    }

    private void ReleaseLevel()
    {
        if (_level is { } level)
        {
            level.Dim.Dispose();
            level.Halo.Dispose();
            level.Lit.Dispose();
            _level = null;
        }
    }

    private void EnsureText(FontFamily family, double size)
    {
        var font = (family, size, _colors.Text);
        if (_labels is not null && _font == font)
        {
            return;
        }

        _font = font;
        _textBrush ??= new SolidColorBrush(_colors.Text);
        var typeface = new Typeface(family);
        _labels = new FormattedText[ScaleMarks.Length];
        for (var i = 0; i < ScaleMarks.Length; i++)
        {
            _labels[i] = new FormattedText(Math.Abs(ScaleMarks[i]).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, _textBrush);
        }

        _readoutWidth = Math.Ceiling(new FormattedText("-00.0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, _textBrush).Width);
        _labelGeometry = null;
        _readout = null;
    }

    // A colour for each segment from the bottom up: low, through mid and hot (about -6 dB), to clip at 0 dB and over.
    private (SolidColorBrush[] Lit, SolidColorBrush[] Halo, SolidColorBrush[] Dim) Segments(int count)
    {
        if (_segments is { } cached && cached.Lit.Length == count)
        {
            return cached;
        }

        var lit = new SolidColorBrush[count];
        var halo = new SolidColorBrush[count];
        var dim = new SolidColorBrush[count];
        for (var i = 0; i < count; i++)
        {
            var color = SegmentColor((i + 0.5) / count);
            lit[i] = new SolidColorBrush(LitColor(color));
            halo[i] = new SolidColorBrush(HaloColor(color));
            dim[i] = new SolidColorBrush(DimColor(color));
        }

        _segments = (lit, halo, dim);
        return _segments.Value;
    }

    private Color SegmentColor(double fraction)
    {
        var low = _colors.Low;
        var mid = _colors.Mid;
        var hot = _colors.Hot;
        var clip = _colors.Clip;
        return fraction switch
        {
            < LowFraction => low,
            < MidFraction => Palette.Mix(low, mid, (fraction - LowFraction) / (MidFraction - LowFraction)),
            _ when fraction < HotFraction => Palette.Mix(mid, hot, (fraction - MidFraction) / (HotFraction - MidFraction)),
            _ when fraction < ZeroFraction => Palette.Mix(hot, clip, (fraction - HotFraction) / (ZeroFraction - HotFraction)),
            _ => clip,
        };
    }

    private sealed record LevelCache(SignalLevelMetrics Metrics, LedShape Shape, double Scale, RenderTargetBitmap Dim, RenderTargetBitmap Halo, RenderTargetBitmap Lit);
}
