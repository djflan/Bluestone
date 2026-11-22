using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A column of round gain LEDs set into a recessed gutter beside a fader: a narrow dark band with rounded ends, an inner
/// shadow from above and a faint lit lip, running the full fader travel. The LEDs light from the bottom up to the gain on
/// the console taper, in the gain gradient (<see cref="MeterGradient.Gain"/>, violet to rose by default), each with a tight glow and a
/// small specular dot; unlit LEDs stay dark at <see cref="Palette.UnlitAlpha"/>. An LED fades as it goes dark
/// (<see cref="FadeMs"/>) and warms up as it lights (<see cref="WarmupMs"/>), on <see cref="LedMotion"/>. Purely a
/// display: the fader beside it is the control.
/// </summary>
public sealed class GainLedGutter : Control
{
    /// <summary>The gain in dB that the LEDs light up to.</summary>
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<GainLedGutter, double>(nameof(Value), double.NegativeInfinity);

    /// <summary>The number of LEDs on a full-height travel; fewer are drawn when they would sit closer than <see cref="MinimumPitch"/>.</summary>
    public static readonly StyledProperty<int> LedCountProperty = AvaloniaProperty.Register<GainLedGutter, int>(nameof(LedCount), 20);

    public static readonly StyledProperty<double> LedRadiusProperty = AvaloniaProperty.Register<GainLedGutter, double>(nameof(LedRadius), 3);

    /// <summary>The closest the LED centres may sit, so a short travel shows fewer LEDs rather than a solid bar.</summary>
    public const double MinimumPitch = 8;

    /// <summary>How long an LED takes to go dark, in milliseconds.</summary>
    internal const double FadeMs = 140;

    /// <summary>How long an LED takes to light, in milliseconds.</summary>
    internal const double WarmupMs = 20;

    private const double LitTier = 0.8;
    private const int Stride = LedLevels.Steps + 1;

    private Color[] _colors = [];
    private IBrush[] _lit = [];
    private IBrush[] _unlit = [];
    private IImmutableSolidColorBrush[][] _glows = [];
    private IBrush[] _steps = [];
    private IImmutableSolidColorBrush[][] _stepGlows = [];
    private readonly IPen[] _stepRims = new IPen[Stride];
    private readonly IBrush[] _stepHighlights = new IBrush[Stride];
    private int _version = -1;
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
            return Math.Clamp(Math.Min(LedCount, (int)Math.Floor(travel / MinimumPitch) + 1), 2, Math.Max(2, LedCount));
        }
    }

    /// <summary>How many LEDs are lit for the current gain.</summary>
    internal int LitCount
    {
        get
        {
            var count = VisibleCount;
            var fraction = ConsoleRail.Position(Value);
            var lit = 0;
            for (var k = 0; k < count; k++)
            {
                if (k / (double)(count - 1) <= fraction + 1e-6 && fraction > 0)
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
        var fraction = Value > MixerRail.GainFloor ? ConsoleRail.Position(Value) : -1;
        var highlight = _stepHighlights[LedLevels.Steps];
        var rim = _stepRims[0];
        using (context.PushOpacity(IsEnabled ? 1 : 0.5))
        {
            for (var k = 0; k < count; k++)
            {
                var f = k / (double)(count - 1);
                var centre = new Point(x, Math.Round(bottom - (f * (bottom - top))) + 0.5);
                _leds.Set(k, f <= fraction + 1e-6, now, fresh);
                var step = _leds.Step(k);
                if (step == LedLevels.Steps)
                {
                    Glow.Ellipse(context, centre, radius, _glows[k]);
                    context.DrawEllipse(_lit[k], null, centre, radius, radius);
                    context.DrawEllipse(highlight, null, new Point(centre.X - 0.5, centre.Y - 0.6), 0.8, 0.8);
                }
                else if (step == 0)
                {
                    context.DrawEllipse(_unlit[k], rim, centre, radius, radius);
                }
                else
                {
                    // Between dark and lit: glow, fill, rim, and dot each part of the way, all prebuilt in EnsureColors.
                    Glow.Ellipse(context, centre, radius, _stepGlows[(k * Stride) + step]);
                    context.DrawEllipse(_steps[(k * Stride) + step], _stepRims[step], centre, radius, radius);
                    context.DrawEllipse(_stepHighlights[step], null, new Point(centre.X - 0.5, centre.Y - 0.6), 0.8, 0.8);
                }
            }
        }
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
    /// The gain gradient sampled once per LED, at its fraction of the travel, with every brush, glow and pen a step of the
    /// fade or warm-up draws, rebuilt only when the count or the gradient changes, so a draw part-way allocates nothing more than one at rest. The brushes
    /// come from <see cref="MeterGradient.Gain"/>'s caches, which empty on each change.
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

        if (_colors.Length == count && _version == gradient.Version)
        {
            return;
        }

        _version = gradient.Version;
        _colors = new Color[count];
        _lit = new IBrush[count];
        _unlit = new IBrush[count];
        _glows = new IImmutableSolidColorBrush[count][];
        _steps = new IBrush[count * Stride];
        _stepGlows = new IImmutableSolidColorBrush[count * Stride][];
        for (var k = 0; k < count; k++)
        {
            var color = Swatch(k / (double)(count - 1));
            _colors[k] = color;
            _lit[k] = gradient.Brush(color);
            _unlit[k] = gradient.Brush(ConsolePaint.Alpha(color, Palette.UnlitAlpha));
            _glows[k] = gradient.GlowBrushes(color, LitTier);
            for (var step = 0; step <= LedLevels.Steps; step++)
            {
                _steps[(k * Stride) + step] = gradient.Brush(ConsolePaint.Alpha(color, Palette.UnlitAlpha + ((1 - Palette.UnlitAlpha) * step / LedLevels.Steps)));
                _stepGlows[(k * Stride) + step] = gradient.GlowBrushes(color, LitTier * step / LedLevels.Steps);
            }
        }
    }

    /// <summary>The gain gradient at <paramref name="fraction"/> of the travel, bottom to top.</summary>
    internal static Color Swatch(double fraction) => MeterGradient.Gain.At(fraction);

    /// <summary>The LED colours drawn now, bottom to top (for tests).</summary>
    internal IReadOnlyList<Color> Colors => _colors;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MeterGradient.Gain.Changed += OnGradientChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MeterGradient.Gain.Changed -= OnGradientChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnGradientChanged(object? sender, EventArgs e) => InvalidateVisual();
}
