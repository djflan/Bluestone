using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// The Machined glow: light a lit lamp, arc, or key throws on its surroundings, drawn as stacked low-alpha shapes (no
/// blur) whose composite falls off as α·e^(−r/τ), with τ a third of the reach. The tier scales the base alpha
/// (<see cref="Palette.GlowAlpha"/>) for brighter or quieter light. Layer opacities and the brushes and pens that paint
/// them are cached per colour and tier, so drawing a halo allocates nothing after the first time. UI thread only.
/// </summary>
/// <remarks>
/// The musician's LED glow setting (Settings ▸ Display ▸ LEDs) reaches every lamp through <see cref="Use"/>:
/// <see cref="Strength"/> scales the tiers here, the blooms (<see cref="Bloom"/>), the physical-LED light
/// (<see cref="LedLight"/>), and the transport's glow (<see cref="GlowEffects"/>). Off, or at strength 0, every one of them
/// draws nothing at all: no layers, no blur, no bitmaps. A selected card's halo (<see cref="Halo(DrawingContext, Rect, double, Color, double, double)"/>)
/// marks selection, not light, and is not affected.
/// </remarks>
internal static class Glow
{
    private const double Tau = 1.0 / 3;
    private const double TightBoost = 1.25;
    private static readonly Dictionary<double, double[]> Layers = [];
    private static readonly Dictionary<(uint, double), IImmutableSolidColorBrush[]> Brushes = [];
    private static readonly Dictionary<(uint, double, double), IPen[]> Pens = [];
    private static readonly Dictionary<(uint, double), IImmutableBrush> Blooms = [];

    public static double Reach => Palette.GlowReach;

    /// <summary>Whether lamps glow at all: on, and at a strength above 0. Off, every glow draw returns at once.</summary>
    public static bool IsOn { get; private set; } = true;

    /// <summary>The musician's glow strength, a multiplier from 0 to 2 (1 is the default look).</summary>
    public static double Strength { get; private set; } = 1;

    /// <summary>Bumped on every change; controls holding glow they built compare it to know when to rebuild.</summary>
    public static int Version { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Use"/> changes the glow; every lamp redraws.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Turns the glow on or off and sets its strength (0 to 2, 1 the default; values outside are clamped). Empties every
    /// cache that depends on them and redraws every lamp. Nothing changes, and nothing is raised, when both are the same.
    /// </summary>
    public static void Use(bool on, double strength)
    {
        strength = double.IsFinite(strength) ? Math.Round(Math.Clamp(strength, 0, 2), 2) : 1;
        var isOn = on && strength > 0;
        if (isOn == IsOn && strength == Strength)
        {
            return;
        }

        IsOn = isOn;
        Strength = strength;
        Layers.Clear();
        Brushes.Clear();
        Pens.Clear();
        Blooms.Clear();
        LedLight.Clear();
        MeterGradient.Mixer.ClearLight();
        MeterGradient.Chain.ClearLight();
        MeterGradient.Gain.ClearLight();
        GlowEffects.Current.Refresh();
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
        RedrawAll();
    }

    // Glow outside the LEDs (a knob's arc, a fader's index) has no subscription of its own: redraw every window once.
    private static void RedrawAll()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { Windows: var windows })
        {
            return;
        }

        foreach (var window in windows)
        {
            foreach (var visual in window.GetVisualDescendants())
            {
                visual.InvalidateVisual();
            }
        }
    }

    /// <summary>
    /// A soft radial bloom (a lamp's light from behind a glyph or label) at <paramref name="alpha"/> times the strength,
    /// cached per colour; null while the glow is off.
    /// </summary>
    public static IImmutableBrush? Bloom(Color color, double alpha)
    {
        if (!IsOn)
        {
            return null;
        }

        var key = (color.ToUInt32(), alpha);
        if (!Blooms.TryGetValue(key, out var bloom))
        {
            var peak = Math.Min(0.95, alpha * Strength);
            bloom = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Palette.Alpha(color, peak), 0),
                    new GradientStop(Palette.Alpha(color, peak * 0.35), 0.45),
                    new GradientStop(Palette.Alpha(color, 0), 1),
                },
            }.ToImmutable();
            Blooms[key] = bloom;
        }

        return bloom;
    }

    /// <summary>Per-layer opacities, innermost first; layer i (1-based) grows by reach·i/n.</summary>
    public static double[] LayersFor(double tier)
    {
        if (Layers.TryGetValue(tier, out var cached))
        {
            return cached;
        }

        var reach = Reach;
        var alpha = Palette.GlowAlpha * tier * (IsOn ? Strength : 0);
        if (reach <= 0.2 || alpha <= 0.005)
        {
            return Layers[tier] = [];
        }

        var n = Math.Max(4, (int)Math.Ceiling(reach * 2));
        var tau = reach * Tau;
        var peak = Math.Min(0.95, alpha * TightBoost);
        var target = new double[n + 2];
        for (var i = 1; i <= n; i++)
        {
            target[i] = peak * Math.Exp(-(reach * (i - 0.5) / n) / tau);
        }

        var layers = new double[n];
        for (var i = n; i >= 1; i--)
        {
            layers[i - 1] = Math.Clamp(1 - ((1 - target[i]) / (1 - target[i + 1])), 0, 1);
        }

        return Layers[tier] = layers;
    }

    /// <summary>
    /// The layers for a fixed theme colour, cached for the life of the app. Colours that change at run time (the meter
    /// gradient's) take <see cref="MeterGradient.GlowBrushes"/> instead, whose cache is emptied when they change.
    /// </summary>
    public static IImmutableSolidColorBrush[] BrushesFor(Color color, double tier)
    {
        var key = (color.ToUInt32(), tier);
        if (!Brushes.TryGetValue(key, out var brushes))
        {
            brushes = CreateBrushes(color, tier);
            Brushes[key] = brushes;
        }

        return brushes;
    }

    /// <summary>New, uncached layers for <paramref name="color"/> at <paramref name="tier"/>, innermost first.</summary>
    public static IImmutableSolidColorBrush[] CreateBrushes(Color color, double tier) =>
        [.. LayersFor(tier).Select(alpha => new ImmutableSolidColorBrush(Palette.Alpha(color, alpha)))];

    private static IPen[] PensFor(Color color, double tier, double width)
    {
        var key = (color.ToUInt32(), tier, width);
        if (!Pens.TryGetValue(key, out var pens))
        {
            pens = CreatePens(BrushesFor(color, tier), width);
            Pens[key] = pens;
        }

        return pens;
    }

    /// <summary>New, uncached line pens for <paramref name="color"/>: for a colour the user is still choosing, held by its control.</summary>
    public static IPen[] CreatePens(Color color, double tier, double width) => CreatePens(CreateBrushes(color, tier), width);

    private static IPen[] CreatePens(IImmutableSolidColorBrush[] brushes, double width)
    {
        var reach = Reach;
        var pens = new IPen[brushes.Length];
        for (var i = 0; i < brushes.Length; i++)
        {
            pens[i] = new ImmutablePen(brushes[i], width + (2 * reach * (i + 1) / brushes.Length), lineCap: PenLineCap.Round);
        }

        return pens;
    }

    public static void Rect(DrawingContext context, Rect rect, double radius, Color color, double tier) =>
        Rect(context, rect, radius, BrushesFor(color, tier));

    /// <summary>A glow around <paramref name="rect"/> in layers the caller holds (from <see cref="CreateBrushes"/> or <see cref="MeterGradient.GlowBrushes"/>).</summary>
    public static void Rect(DrawingContext context, Rect rect, double radius, IImmutableSolidColorBrush[] brushes)
    {
        if (!IsOn)
        {
            return;
        }

        for (var i = brushes.Length; i >= 1; i--)
        {
            var grow = Reach * i / brushes.Length;
            context.DrawRectangle(brushes[i - 1], null, new RoundedRect(rect.Inflate(grow), radius + grow));
        }
    }

    /// <summary>A wide, soft halo with its own reach and alpha (a selected card's light on the desk), cached like the tiers.</summary>
    public static void Halo(DrawingContext context, Rect rect, double radius, Color color, double alpha, double reach)
    {
        var key = (color.ToUInt32(), -alpha, reach);
        if (!Pens.TryGetValue(key, out var cached))
        {
            cached = [.. CreateHalo(color, alpha, reach).Select(brush => (IPen)new ImmutablePen(brush, 0))];
            Pens[key] = cached;
        }

        for (var i = cached.Length; i >= 1; i--)
        {
            var grow = reach * i / cached.Length;
            context.DrawRectangle(cached[i - 1].Brush, null, new RoundedRect(rect.Inflate(grow), radius + grow));
        }
    }

    /// <summary>New, uncached <see cref="Halo(DrawingContext, Rect, double, Color, double, double)"/> layers, innermost first, for a colour that keeps changing.</summary>
    public static IImmutableSolidColorBrush[] CreateHalo(Color color, double alpha, double reach)
    {
        var n = Math.Max(4, (int)Math.Ceiling(reach * 2));
        var tau = reach * Tau;
        var peak = Math.Min(0.95, alpha * TightBoost);
        var target = new double[n + 2];
        for (var i = 1; i <= n; i++)
        {
            target[i] = peak * Math.Exp(-(reach * (i - 0.5) / n) / tau);
        }

        var layers = new IImmutableSolidColorBrush[n];
        for (var i = n; i >= 1; i--)
        {
            var layer = Math.Clamp(1 - ((1 - target[i]) / (1 - target[i + 1])), 0, 1);
            layers[i - 1] = new ImmutableSolidColorBrush(Palette.Alpha(color, layer));
        }

        return layers;
    }

    /// <summary>A halo in layers the caller holds (from <see cref="CreateHalo"/>).</summary>
    public static void Halo(DrawingContext context, Rect rect, double radius, IImmutableSolidColorBrush[] layers, double reach)
    {
        for (var i = layers.Length; i >= 1; i--)
        {
            var grow = reach * i / layers.Length;
            context.DrawRectangle(layers[i - 1], null, new RoundedRect(rect.Inflate(grow), radius + grow));
        }
    }

    public static void Ellipse(DrawingContext context, Point centre, double radius, Color color, double tier) =>
        Ellipse(context, centre, radius, BrushesFor(color, tier));

    /// <summary>A glow around a circle in layers the caller holds (from <see cref="CreateBrushes"/> or <see cref="MeterGradient.GlowBrushes"/>).</summary>
    public static void Ellipse(DrawingContext context, Point centre, double radius, IImmutableSolidColorBrush[] brushes)
    {
        if (!IsOn)
        {
            return;
        }

        for (var i = brushes.Length; i >= 1; i--)
        {
            var grow = Reach * i / brushes.Length;
            context.DrawEllipse(brushes[i - 1], null, centre, radius + grow, radius + grow);
        }
    }

    public static void Line(DrawingContext context, Point from, Point to, double width, Color color, double tier)
    {
        if (!IsOn)
        {
            return;
        }

        var pens = PensFor(color, tier, width);
        for (var i = pens.Length; i >= 1; i--)
        {
            context.DrawLine(pens[i - 1], from, to);
        }
    }

    /// <summary>A line glow in pens the caller holds (from <see cref="CreatePens(Color, double, double)"/>).</summary>
    public static void Line(DrawingContext context, Point from, Point to, IPen[] pens)
    {
        if (!IsOn)
        {
            return;
        }

        for (var i = pens.Length; i >= 1; i--)
        {
            context.DrawLine(pens[i - 1], from, to);
        }
    }

    public static void Geometry(DrawingContext context, Geometry geometry, double width, Color color, double tier)
    {
        if (!IsOn)
        {
            return;
        }

        var pens = PensFor(color, tier, width);
        for (var i = pens.Length; i >= 1; i--)
        {
            context.DrawGeometry(null, pens[i - 1], geometry);
        }
    }
}
