using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// The one seam every signal indicator takes its colours from: a fraction of the scale (0 at the floor, 1 at the top)
/// maps to a colour through the current <see cref="SignalGradient"/>, whose <see cref="SignalGradient.Evaluate"/> is the
/// one interpolator (so a hard edge falls the same way here, in the editor, and in the saved preference). The default is
/// <see cref="SignalGradient.Default"/>; the clip lamp is never part of it (<see cref="Palette.Clip"/>).
/// </summary>
/// <remarks>
/// Controls cache their brushes per <see cref="Version"/> and rebuild only when it changes, so a frame never allocates for
/// colour. Brushes for gradient colours come from <see cref="Brush"/> and <see cref="GlowBrushes"/>, whose caches are
/// emptied on every change, so editing the gradient never grows the shared, never-evicted caches in
/// <c>ConsolePaint</c> and <see cref="Glow"/>. UI thread only.
/// </remarks>
public static class MeterGradient
{
    private static readonly Dictionary<uint, IImmutableSolidColorBrush> SolidBrushes = [];
    private static readonly Dictionary<(uint, double), IImmutableSolidColorBrush[]> Glows = [];
    private static SignalGradient _gradient = SignalGradient.Default;
    private static (double Position, Color Color)[] _stops = StopsOf(SignalGradient.Default);

    /// <summary>The default stops, bottom to top: <see cref="SignalGradient.Default"/>'s.</summary>
    public static IReadOnlyList<(double Position, Color Color)> Default { get; } = StopsOf(SignalGradient.Default);

    /// <summary>Raised on the UI thread after <see cref="Use(SignalGradient)"/> changes the gradient.</summary>
    public static event EventHandler? Changed;

    /// <summary>Bumped on every change; caches compare it to know when to rebuild.</summary>
    public static int Version { get; private set; }

    /// <summary>The gradient in use.</summary>
    public static SignalGradient Gradient => _gradient;

    public static IReadOnlyList<(double Position, Color Color)> Stops => _stops;

    /// <summary>Uses <paramref name="gradient"/> (null restores the default). Nothing changes, and nothing is raised, when it is the same.</summary>
    public static void Use(SignalGradient? gradient)
    {
        gradient ??= SignalGradient.Default;
        if (gradient == _gradient)
        {
            return;
        }

        _gradient = gradient;
        _stops = StopsOf(gradient);
        SolidBrushes.Clear();
        Glows.Clear();
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Uses these stops (their alpha is ignored). A single stop is a flat colour; none, or a set a <see cref="SignalGradient"/>
    /// cannot hold, restores the default.
    /// </summary>
    public static void Use(IEnumerable<(double Position, Color Color)> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        var list = stops.Select(stop => new SignalGradientStop($"#{stop.Color.R:X2}{stop.Color.G:X2}{stop.Color.B:X2}", stop.Position)).ToList();
        if (list.Count == 1)
        {
            list.Add(list[0]);
        }

        Use(SignalGradient.TryCreate(list, out var gradient) ? gradient : null);
    }

    /// <summary>The colour at <paramref name="fraction"/> of the scale.</summary>
    public static Color At(double fraction)
    {
        var (r, g, b) = _gradient.Evaluate(fraction);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>A solid brush for a colour drawn from the gradient, cached until the gradient changes.</summary>
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

    /// <summary>The glow layers (<see cref="Glow.CreateBrushes"/>) for a colour drawn from the gradient, cached until the gradient changes.</summary>
    public static IImmutableSolidColorBrush[] GlowBrushes(Color color, double tier)
    {
        var key = (color.ToUInt32(), tier);
        if (!Glows.TryGetValue(key, out var brushes))
        {
            brushes = Glow.CreateBrushes(color, tier);
            Glows[key] = brushes;
        }

        return brushes;
    }

    /// <summary>How many brushes the gradient caches hold now (for tests).</summary>
    internal static int CachedBrushCount => SolidBrushes.Count + Glows.Count;

    private static (double Position, Color Color)[] StopsOf(SignalGradient gradient) =>
        [.. gradient.Stops.Select(stop => (stop.Position, Color.Parse(stop.Colour)))];
}
