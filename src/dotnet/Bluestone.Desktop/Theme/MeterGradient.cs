using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>Which family of indicators a control takes its gradient from.</summary>
public enum MeterGradientTarget
{
    /// <summary>The mixer's meters (the default).</summary>
    Mixer,

    /// <summary>A device chain's junction and Out meters.</summary>
    Chain,

    /// <summary>The gain LEDs beside a fader.</summary>
    Gain,
}

/// <summary>
/// The seam a family of signal indicators takes its colours from: a fraction of the scale (0 at the floor, 1 at the top)
/// maps to a colour through the current <see cref="SignalGradient"/>, whose <see cref="SignalGradient.Evaluate"/> is the
/// one interpolator (so a hard edge falls the same way here, in the editor, and in the saved preference). There is one per
/// family (<see cref="Mixer"/>, <see cref="Chain"/>, <see cref="Gain"/>), each with its own version, event, and caches, so
/// editing one never invalidates the others. The clip lamp is never part of a gradient (<see cref="Palette.Clip"/>).
/// </summary>
/// <remarks>
/// Controls cache their brushes per instance and <see cref="Version"/> and rebuild only when either changes, so a frame
/// never allocates for colour. Brushes for gradient colours come from <see cref="Brush"/> and <see cref="GlowBrushes"/>,
/// whose caches are emptied on every change of that gradient, so editing it never grows the shared, never-evicted caches in
/// <c>ConsolePaint</c> and <see cref="Glow"/>. UI thread only.
/// </remarks>
public sealed class MeterGradient
{
    private readonly Dictionary<uint, IImmutableSolidColorBrush> _solidBrushes = [];
    private readonly Dictionary<(uint, double), IImmutableSolidColorBrush[]> _glows = [];
    private readonly SignalGradient _default;
    private SignalGradient _gradient;
    private (double Position, Color Color)[] _stops;

    private MeterGradient(string name, SignalGradient defaultGradient)
    {
        Name = name;
        _default = defaultGradient;
        _gradient = defaultGradient;
        _stops = StopsOf(defaultGradient);
        Default = StopsOf(defaultGradient);
    }

    /// <summary>The mixer's meters: <see cref="SignalGradient.Default"/> unless changed.</summary>
    public static MeterGradient Mixer { get; } = new("Mixer", SignalGradient.Default);

    /// <summary>A device chain's junction and Out meters: <see cref="SignalGradient.Default"/> unless changed.</summary>
    public static MeterGradient Chain { get; } = new("Chain", SignalGradient.Default);

    /// <summary>The gain LEDs: <see cref="SignalGradient.GainDefault"/> unless changed.</summary>
    public static MeterGradient Gain { get; } = new("Gain", SignalGradient.GainDefault);

    /// <summary>The instance for <paramref name="target"/>.</summary>
    public static MeterGradient For(MeterGradientTarget target) => target switch
    {
        MeterGradientTarget.Chain => Chain,
        MeterGradientTarget.Gain => Gain,
        _ => Mixer,
    };

    public string Name { get; }

    /// <summary>The default stops, bottom to top.</summary>
    public IReadOnlyList<(double Position, Color Color)> Default { get; }

    /// <summary>Raised on the UI thread after <see cref="Use(SignalGradient)"/> changes this gradient; the sender is this instance.</summary>
    public event EventHandler? Changed;

    /// <summary>Bumped on every change; caches compare it (with the instance) to know when to rebuild.</summary>
    public int Version { get; private set; }

    /// <summary>The gradient in use.</summary>
    public SignalGradient Gradient => _gradient;

    public IReadOnlyList<(double Position, Color Color)> Stops => _stops;

    /// <summary>Uses <paramref name="gradient"/> (null restores the default). Nothing changes, and nothing is raised, when it is the same.</summary>
    public void Use(SignalGradient? gradient)
    {
        gradient ??= _default;
        if (gradient == _gradient)
        {
            return;
        }

        _gradient = gradient;
        _stops = StopsOf(gradient);
        _solidBrushes.Clear();
        _glows.Clear();
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Uses these stops (their alpha is ignored). A single stop is a flat colour; none, or a set a <see cref="SignalGradient"/>
    /// cannot hold, restores the default.
    /// </summary>
    public void Use(IEnumerable<(double Position, Color Color)> stops)
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
    public Color At(double fraction)
    {
        var (r, g, b) = _gradient.Evaluate(fraction);
        return Color.FromRgb(r, g, b);
    }

    /// <summary>A solid brush for a colour drawn from this gradient, cached until it changes.</summary>
    public IImmutableSolidColorBrush Brush(Color color)
    {
        var key = color.ToUInt32();
        if (!_solidBrushes.TryGetValue(key, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _solidBrushes[key] = brush;
        }

        return brush;
    }

    /// <summary>The glow layers (<see cref="Glow.CreateBrushes"/>) for a colour drawn from this gradient, cached until it changes.</summary>
    public IImmutableSolidColorBrush[] GlowBrushes(Color color, double tier)
    {
        var key = (color.ToUInt32(), tier);
        if (!_glows.TryGetValue(key, out var brushes))
        {
            brushes = Glow.CreateBrushes(color, tier);
            _glows[key] = brushes;
        }

        return brushes;
    }

    public override string ToString() => Name;

    /// <summary>How many brushes this gradient's caches hold now (for tests).</summary>
    internal int CachedBrushCount => _solidBrushes.Count + _glows.Count;

    private static (double Position, Color Color)[] StopsOf(SignalGradient gradient) =>
        [.. gradient.Stops.Select(stop => (stop.Position, Color.Parse(stop.Colour)))];
}
