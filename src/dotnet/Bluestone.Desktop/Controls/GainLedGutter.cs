using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A column of round gain LEDs set into a recessed gutter beside a fader: a narrow dark band with rounded ends, an inner
/// shadow from above and a faint lit lip, running the full fader travel. The LEDs light from the bottom up to the gain on
/// the console taper, in the gain gradient (<see cref="MeterGradient.Gain"/>, violet to rose by default). While the LED glow
/// is on (<see cref="Glow"/>) a lit LED is a lens, a hot core going to its saturated edge with a specular, throwing baked
/// light (<see cref="LedLight"/>) on the gutter; off, it is a flat lit dot with a small highlight; unlit LEDs stay dark at <see cref="Palette.UnlitAlpha"/>. An LED fades as it goes dark
/// (<see cref="FadeMs"/>) and warms up as it lights (<see cref="WarmupMs"/>), on <see cref="LedMotion"/>. Purely a
/// display: the fader beside it is the control.
/// </summary>
public sealed class GainLedGutter : Control
{
    /// <summary>The gain in dB that the LEDs light up to.</summary>
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<GainLedGutter, double>(nameof(Value), double.NegativeInfinity);

    /// <summary>The maximum number of LEDs; the visible count follows the available travel at <see cref="MinimumPitch"/>.</summary>
    public static readonly StyledProperty<int> LedCountProperty = AvaloniaProperty.Register<GainLedGutter, int>(nameof(LedCount), 20);

    public static readonly StyledProperty<double> LedRadiusProperty = AvaloniaProperty.Register<GainLedGutter, double>(nameof(LedRadius), 3);

    /// <summary>The fixed centre-to-centre distance between LEDs, in DIPs.</summary>
    public const double MinimumPitch = 20;

    /// <summary>How long an LED takes to go dark, in milliseconds.</summary>
    internal const double FadeMs = 140;

    /// <summary>How long an LED takes to light, in milliseconds.</summary>
    internal const double WarmupMs = 20;

    private const int Stride = LedLevels.Steps + 1;
    private const int LightStride = LedLight.Levels + 1;
    private static readonly IBrush Specular = ConsolePaint.Brush(ConsolePaint.Alpha(ConsolePaint.White, 0.8));

    private Color[] _colors = [];
    private IBrush[] _lit = [];
    private IBrush[] _unlit = [];
    private IBrush[] _lens = [];
    private LedSprite?[] _lights = [];
    private IBrush[] _steps = [];
    private readonly IPen[] _stepRims = new IPen[Stride];
    private readonly IBrush[] _stepHighlights = new IBrush[Stride];
    private int _version = -1;
    private int _glowVersion = -1;
    private double _litRadius = -1;
    private readonly LedLevels _leds;

    static GainLedGutter()
    {
        AffectsRender<GainLedGutter>(ValueProperty, LedCountProperty, LedRadiusProperty, IsEnabledProperty);
        AffectsMeasure<GainLedGutter>(LedRadiusProperty);
    }

    public GainLedGutter() => _leds = new LedLevels(this, FadeMs, WarmupMs);

    /// <summary>How lit each LED is, bottom to top (for tests).</summary>
    internal LedLevels Leds => _leds;

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public int LedCount { get => GetValue(LedCountProperty); set => SetValue(LedCountProperty, value); }

    public double LedRadius { get => GetValue(LedRadiusProperty); set => SetValue(LedRadiusProperty, value); }

    /// <summary>The gutter's width: the LED plus 2.5 DIP of dark band each side.</summary>
    public double GutterWidth => Math.Round((2 * LedRadius) + 5);

    /// <summary>How many LEDs fit the current travel.</summary>
    internal int VisibleCount
    {
        get
        {
            var travel = Bounds.Height - ConsoleRail.TravelTop - ConsoleRail.TravelBottomInset;
            var maximum = Math.Max(1, LedCount);
            if (!double.IsFinite(travel) || travel <= 0)
            {
                return 1;
            }

            var fitting = Math.Floor(travel / MinimumPitch) + 1;
            return Math.Clamp((int)Math.Min(fitting, maximum), 1, maximum);
        }
    }

    /// <summary>How many LEDs are lit for the current gain.</summary>
    internal int LitCount
    {
        get
        {
            var count = VisibleCount;
            var fraction = GainPosition(Value);
            var travel = Bounds.Height - ConsoleRail.TravelTop - ConsoleRail.TravelBottomInset;
            var lit = 0;
            for (var k = 0; k < count; k++)
            {
                if (LedTarget(k, fraction, travel) > 0)
                {
                    lit++;
                }
            }

            return lit;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(GutterWidth + 4, 0);

    public override void Render(DrawingContext context)
    {
        var top = ConsoleRail.TravelTop;
        var bottom = Bounds.Height - ConsoleRail.TravelBottomInset;
        if (bottom - top < 8)
        {
            return;
        }

        var width = GutterWidth;
        var x = Math.Round(Bounds.Width / 2) - 0.5;
        Gutter(context, new Rect(x - (width / 2), top - (width / 2) - 2, width, bottom - top + width + 4));

        var count = VisibleCount;
        EnsureColors(count);
        var fresh = _leds.Resize(count);
        var now = LedMotion.Now;
        var radius = LedRadius;
        var fraction = GainPosition(Value);
        var travel = bottom - top;
        var highlight = _stepHighlights[LedLevels.Steps];
        var rim = _stepRims[0];
        var glow = Glow.IsOn;
        using (context.PushOpacity(IsEnabled ? 1 : 0.5))
        {
            // The light first, every LED's, so a neighbour's spill never lies over a lens.
            for (var k = 0; k < count; k++)
            {
                var f = CentreFraction(k, travel);
                _leds.Set(k, LedTarget(k, fraction, travel), now, fresh);
                var level = glow ? LedLight.Level(_leds.Step(k)) : 0;
                if (level > 0 && _lights[(k * LightStride) + level] is { } light)
                {
                    var centre = Centre(x, bottom, top, f);
                    light.Draw(context, new Rect(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius));
                }
            }

            for (var k = 0; k < count; k++)
            {
                var f = CentreFraction(k, travel);
                var centre = Centre(x, bottom, top, f);
                var step = _leds.Step(k);
                if (step == LedLevels.Steps && glow)
                {
                    // A physical LED: a hot core going to the saturated edge, and the lens's specular.
                    context.DrawEllipse(_lens[k], null, centre, radius, radius);
                    context.DrawEllipse(Specular, null, new Point(centre.X - (radius * 0.3), centre.Y - (radius * 0.36)), radius * 0.3, radius * 0.24);
                }
                else if (step == LedLevels.Steps)
                {
                    context.DrawEllipse(_lit[k], null, centre, radius, radius);
                    context.DrawEllipse(highlight, null, new Point(centre.X - 0.5, centre.Y - 0.6), 0.8, 0.8);
                }
                else if (step == 0)
                {
                    context.DrawEllipse(_unlit[k], rim, centre, radius, radius);
                }
                else
                {
                    // Between dark and lit: fill, rim, and dot each part of the way, all prebuilt in EnsureColors.
                    context.DrawEllipse(_steps[(k * Stride) + step], _stepRims[step], centre, radius, radius);
                    context.DrawEllipse(_stepHighlights[step], null, new Point(centre.X - 0.5, centre.Y - 0.6), 0.8, 0.8);
                }
            }
        }
    }

    private static Point Centre(double x, double bottom, double top, double f) => new(x, Math.Round(bottom - (f * (bottom - top))) + 0.5);

    /// <summary>The position of a fixed-pitch LED as a fraction of the available travel.</summary>
    internal static double CentreFraction(int index, double travel) =>
        double.IsFinite(travel) && travel > 0 ? Math.Clamp((index * MinimumPitch) / travel, 0, 1) : 0;

    /// <summary>The finite fader position on the console taper, or a negative sentinel when no LEDs should be lit.</summary>
    private static double GainPosition(double value) => double.IsFinite(value) && value > MixerRail.GainFloor ? ConsoleRail.Position(value) : -1;

    /// <summary>The target brightness for fixed-pitch LEDs, keeping the lowest lit and progressively lighting the next.</summary>
    private static double LedTarget(int index, double fraction, double travel)
    {
        if (fraction < 0 || !double.IsFinite(travel) || travel <= 0)
        {
            return 0;
        }

        if (index == 0)
        {
            return 1;
        }

        return Math.Clamp(((fraction * travel) - ((index - 1) * MinimumPitch)) / MinimumPitch, 0, 1);
    }

    /// <summary>The recessed band: darker than the well, shadowed from above and on the left, with a faint lit lip.</summary>
    private static void Gutter(DrawingContext context, Rect rect)
    {
        rect = ConsolePaint.Snap(rect);
        var radius = rect.Width / 2;
        var band = new RoundedRect(rect, radius);
        context.DrawRectangle(ConsolePaint.Brush(ConsolePaint.Mix(Palette.Well, ConsolePaint.Black, 0.35)), null, band);
        using (context.PushClip(band))
        {
            context.FillRectangle(ConsolePaint.Vertical(ConsolePaint.Alpha(ConsolePaint.Black, 0.55), ConsolePaint.Alpha(ConsolePaint.Black, 0)), new Rect(rect.X, rect.Y, rect.Width, 10));
            context.FillRectangle(ConsolePaint.Brush(ConsolePaint.Alpha(ConsolePaint.Black, 0.35)), new Rect(rect.X, rect.Y, 1.5, rect.Height));
        }

        context.DrawRectangle(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.75)), new RoundedRect(rect.Deflate(0.5), radius - 0.5));
        context.DrawRectangle(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.White, 0.045)), new RoundedRect(rect.Inflate(0.5), radius + 0.5));
    }

    /// <summary>
    /// The gain gradient sampled across the visible ladder, with every brush, glow and pen a step of the fade or warm-up
    /// draws, rebuilt only when the count, radius, gradient or glow setting changes, so a draw part-way allocates nothing
    /// more than one at rest. The brushes come from <see cref="MeterGradient.Gain"/>'s caches, which empty on each change.
    /// </summary>
    private void EnsureColors(int count)
    {
        var gradient = MeterGradient.Gain;
        if (_stepRims[0] is null)
        {
            for (var step = 0; step <= LedLevels.Steps; step++)
            {
                var lit = step / (double)LedLevels.Steps;
                _stepRims[step] = ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.5 * (1 - lit)), 0.6);
                _stepHighlights[step] = ConsolePaint.Brush(ConsolePaint.Alpha(ConsolePaint.White, 0.35 * lit));
            }
        }

        var radius = LedRadius;
        if (_colors.Length == count && _version == gradient.Version && _glowVersion == Glow.Version && _litRadius == radius)
        {
            return;
        }

        _version = gradient.Version;
        _glowVersion = Glow.Version;
        _litRadius = radius;
        _colors = new Color[count];
        _lit = new IBrush[count];
        _unlit = new IBrush[count];
        _lens = new IBrush[count];
        _lights = new LedSprite?[count * LightStride];
        _steps = new IBrush[count * Stride];
        var size = new Size(2 * radius, 2 * radius);
        for (var k = 0; k < count; k++)
        {
            var fraction = count > 1 ? k / (double)(count - 1) : 0;
            var color = Swatch(fraction);
            _colors[k] = color;
            _lit[k] = gradient.Brush(color);
            _unlit[k] = gradient.Brush(ConsolePaint.Alpha(color, Palette.UnlitAlpha));
            _lens[k] = Lens(color);
            for (var step = 0; step <= LedLevels.Steps; step++)
            {
                _steps[(k * Stride) + step] = gradient.Brush(ConsolePaint.Alpha(color, Palette.UnlitAlpha + ((1 - Palette.UnlitAlpha) * step / LedLevels.Steps)));
            }

            // The light at each level a fade passes through, baked now so a first fade allocates nothing.
            for (var level = 1; level <= LedLight.Levels; level++)
            {
                _lights[(k * LightStride) + level] = gradient.Light(color, size, radius, ellipse: true, level / (double)LedLight.Levels);
            }
        }
    }

    /// <summary>A lit LED's face: near-white at the hot core, the saturated colour at the edge.</summary>
    private static IBrush Lens(Color color) => new RadialGradientBrush
    {
        GradientOrigin = new RelativePoint(0.42, 0.4, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Palette.Lighten(color, 0.8), 0),
            new GradientStop(Palette.Lighten(color, 0.4), 0.4),
            new GradientStop(Palette.Lighten(color, 0.08), 0.78),
            new GradientStop(color, 1),
        },
    }.ToImmutable();

    /// <summary>The gain gradient at <paramref name="fraction"/> of the travel, bottom to top.</summary>
    internal static Color Swatch(double fraction) => MeterGradient.Gain.At(fraction);

    /// <summary>The LED colours drawn now, bottom to top (for tests).</summary>
    internal IReadOnlyList<Color> Colors => _colors;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeterGradient.Gain.Changed += OnGradientChanged;
        Glow.Changed += OnGradientChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MeterGradient.Gain.Changed -= OnGradientChanged;
        Glow.Changed -= OnGradientChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnGradientChanged(object? sender, EventArgs e) => InvalidateVisual();
}
