using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// The Machined glow: light a lit lamp, arc, or key throws on its surroundings, drawn as stacked low-alpha shapes (no
/// blur) whose composite falls off as α·e^(−r/τ), with τ a third of the reach. The tier scales the base alpha
/// (<see cref="Palette.GlowAlpha"/>) for brighter or quieter light. Layer opacities and the brushes and pens that paint
/// them are cached per colour and tier, so drawing a halo allocates nothing after the first time. UI thread only.
/// </summary>
internal static class Glow
{
    private const double Tau = 1.0 / 3;
    private const double TightBoost = 1.25;
    private static readonly Dictionary<double, double[]> Layers = [];
    private static readonly Dictionary<(uint, double), IImmutableSolidColorBrush[]> Brushes = [];
    private static readonly Dictionary<(uint, double, double), IPen[]> Pens = [];

    public static double Reach => Palette.GlowReach;

    /// <summary>Per-layer opacities, innermost first; layer i (1-based) grows by reach·i/n.</summary>
    public static double[] LayersFor(double tier)
    {
        if (Layers.TryGetValue(tier, out var cached))
        {
            return cached;
        }

        var reach = Reach;
        var alpha = Palette.GlowAlpha * tier;
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
            var brushes = BrushesFor(color, tier);
            var reach = Reach;
            pens = new IPen[brushes.Length];
            for (var i = 0; i < brushes.Length; i++)
            {
                pens[i] = new ImmutablePen(brushes[i], width + (2 * reach * (i + 1) / brushes.Length), lineCap: PenLineCap.Round);
            }

            Pens[key] = pens;
        }

        return pens;
    }

    public static void Rect(DrawingContext context, Rect rect, double radius, Color color, double tier) =>
        Rect(context, rect, radius, BrushesFor(color, tier));

    /// <summary>A glow around <paramref name="rect"/> in layers the caller holds (from <see cref="CreateBrushes"/> or <see cref="MeterGradient.GlowBrushes"/>).</summary>
    public static void Rect(DrawingContext context, Rect rect, double radius, IImmutableSolidColorBrush[] brushes)
    {
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
            var n = Math.Max(4, (int)Math.Ceiling(reach * 2));
            var tau = reach * Tau;
            var peak = Math.Min(0.95, alpha * TightBoost);
            var target = new double[n + 2];
            for (var i = 1; i <= n; i++)
            {
                target[i] = peak * Math.Exp(-(reach * (i - 0.5) / n) / tau);
            }

            cached = new IPen[n];
            for (var i = n; i >= 1; i--)
            {
                var layer = Math.Clamp(1 - ((1 - target[i]) / (1 - target[i + 1])), 0, 1);
                cached[i - 1] = new ImmutablePen(new ImmutableSolidColorBrush(Palette.Alpha(color, layer)), 0);
            }

            Pens[key] = cached;
        }

        for (var i = cached.Length; i >= 1; i--)
        {
            var grow = reach * i / cached.Length;
            context.DrawRectangle(cached[i - 1].Brush, null, new RoundedRect(rect.Inflate(grow), radius + grow));
        }
    }

    public static void Ellipse(DrawingContext context, Point centre, double radius, Color color, double tier) =>
        Ellipse(context, centre, radius, BrushesFor(color, tier));

    /// <summary>A glow around a circle in layers the caller holds (from <see cref="CreateBrushes"/> or <see cref="MeterGradient.GlowBrushes"/>).</summary>
    public static void Ellipse(DrawingContext context, Point centre, double radius, IImmutableSolidColorBrush[] brushes)
    {
        for (var i = brushes.Length; i >= 1; i--)
        {
            var grow = Reach * i / brushes.Length;
            context.DrawEllipse(brushes[i - 1], null, centre, radius + grow, radius + grow);
        }
    }

    public static void Line(DrawingContext context, Point from, Point to, double width, Color color, double tier)
    {
        var pens = PensFor(color, tier, width);
        for (var i = pens.Length; i >= 1; i--)
        {
            context.DrawLine(pens[i - 1], from, to);
        }
    }

    public static void Geometry(DrawingContext context, Geometry geometry, double width, Color color, double tier)
    {
        var pens = PensFor(color, tier, width);
        for (var i = pens.Length; i >= 1; i--)
        {
            context.DrawGeometry(null, pens[i - 1], geometry);
        }
    }
}
