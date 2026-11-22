using Avalonia.Media;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// The one seam every signal indicator takes its colours from: a fraction of the scale (0 at the floor, 1 at the top)
/// maps to a colour by linear interpolation between stops. The default is the Machined identity's cyan → aqua → violet
/// ladder; the clip lamp is never part of it (<see cref="Palette.Clip"/>). Controls cache their brushes per
/// <see cref="Version"/> and rebuild only when it changes, so a frame never allocates for colour.
/// </summary>
/// <remarks>
/// A persisted user gradient plugs in with one call to <see cref="Use"/> (for example from
/// <c>Bluestone.Presentation.SignalGradient</c>: <c>MeterGradient.Use(gradient.Stops.Select(s =&gt; (s.Position, Color.Parse(s.Colour))))</c>).
/// UI thread only.
/// </remarks>
public static class MeterGradient
{
    /// <summary>The Machined default stops, bottom to top.</summary>
    public static readonly IReadOnlyList<(double Position, Color Color)> Default =
    [
        (0.00, Color.Parse("#34C6F4")),
        (0.40, Color.Parse("#34C6F4")),
        (0.62, Color.Parse("#3FE0D8")),
        (0.78, Color.Parse("#7C6BFF")),
        (0.86, Color.Parse("#B65CFF")),
        (1.00, Color.Parse("#B65CFF")),
    ];

    private static (double Position, Color Color)[] _stops = [.. Default];

    /// <summary>Raised on the UI thread after <see cref="Use"/> changes the stops.</summary>
    public static event EventHandler? Changed;

    /// <summary>Bumped on every change; caches compare it to know when to rebuild.</summary>
    public static int Version { get; private set; }

    public static IReadOnlyList<(double Position, Color Color)> Stops => _stops;

    /// <summary>Replaces the stops (sorted by position; at least one). An empty sequence restores <see cref="Default"/>.</summary>
    public static void Use(IEnumerable<(double Position, Color Color)> stops)
    {
        var next = stops.OrderBy(stop => stop.Position).ToArray();
        _stops = next.Length == 0 ? [.. Default] : next;
        Version++;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The colour at <paramref name="fraction"/> of the scale.</summary>
    public static Color At(double fraction)
    {
        var stops = _stops;
        if (double.IsNaN(fraction) || fraction <= stops[0].Position)
        {
            return stops[0].Color;
        }

        for (var i = 1; i < stops.Length; i++)
        {
            if (fraction <= stops[i].Position)
            {
                var span = stops[i].Position - stops[i - 1].Position;
                return span <= 0 ? stops[i].Color : Palette.Blend(stops[i - 1].Color, stops[i].Color, (fraction - stops[i - 1].Position) / span);
            }
        }

        return stops[^1].Color;
    }
}
