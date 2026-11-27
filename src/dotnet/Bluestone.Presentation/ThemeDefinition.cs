using System.Collections.Immutable;

namespace Bluestone.Presentation;

/// <summary>A theme's overall color scheme. Internal data: it describes the theme, and stock controls follow it.</summary>
public enum ThemeVariant
{
    /// <summary>Light surfaces, dark text.</summary>
    Light,

    /// <summary>Mid-gray surfaces.</summary>
    Midtoned,

    /// <summary>Dark surfaces, light text (Dark Sodalite).</summary>
    Dark,
}

/// <summary>
/// A theme (ADR 0053): every UI color, keyed by its color token (the exact Color resource key in Theme/Tokens.axaml, or
/// "Fluent.*" for the stock controls' palette), the three meter gradients, the MIDI LED color, and the peak marker color. A theme changes values
/// only; roles, geometry, type, and rules belong to the design language (docs/design-language.md).
/// </summary>
public sealed record ThemeDefinition
{
    /// <summary>The theme Bluestone ships with, and the default.</summary>
    public const string DarkSodaliteName = "Dark Sodalite";

    /// <summary>The MIDI LED color Dark Sodalite ships with: the accent.</summary>
    public const string DefaultMidiColor = "#3CCBF4";

    /// <summary>The peak marker color Dark Sodalite ships with: white, brighter than any lit cell.</summary>
    public const string DefaultPeakMarkerColor = "#FFFFFF";

    /// <summary>The theme's name, for example "Dark Sodalite".</summary>
    public required string Name { get; init; }

    /// <summary>The overall scheme.</summary>
    public ThemeVariant Variant { get; init; } = ThemeVariant.Dark;

    /// <summary>Every color, "#RRGGBB" or "#AARRGGBB" in upper case, by color token.</summary>
    public ImmutableSortedDictionary<string, string> Colors { get; init; } = ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The colors the mixer's meters run through.</summary>
    public SignalGradient MixerGradient { get; init; } = SignalGradient.Default;

    /// <summary>The colors a device chain's junction meters run through.</summary>
    public SignalGradient ChainGradient { get; init; } = SignalGradient.Default;

    /// <summary>The colors the gain LEDs beside a fader run through.</summary>
    public SignalGradient GainGradient { get; init; } = SignalGradient.GainDefault;

    /// <summary>The color of a lit MIDI LED, "#RRGGBB".</summary>
    public string MidiColor { get; init; } = DefaultMidiColor;

    /// <summary>The color of the current-peak marker inside a Peak + RMS meter's lit RMS body, "#RRGGBB".</summary>
    public string PeakMarkerColor { get; init; } = DefaultPeakMarkerColor;

    /// <summary>Dark Sodalite with <paramref name="colors"/> (the values in Theme/Tokens.axaml) and the shipped gradients.</summary>
    public static ThemeDefinition DarkSodalite(IEnumerable<KeyValuePair<string, string>> colors) => new()
    {
        Name = DarkSodaliteName,
        Variant = ThemeVariant.Dark,
        Colors = NormalizeColors(colors),
    };

    /// <summary>The file a theme is saved in: its name in lower case, words joined by hyphens ("dark-sodalite.json").</summary>
    public static string FileName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(char.IsAsciiLetterOrDigit)]).ToLowerInvariant())
            .Where(word => word.Length > 0);
        return string.Join('-', words) + ".json";
    }

    /// <summary>Whether <paramref name="color"/> is "#RRGGBB" or "#AARRGGBB" (either case).</summary>
    public static bool IsColor(string? color) =>
        color is { Length: 7 or 9 } && color[0] == '#' && color.AsSpan(1).ContainsAnyExcept(HexDigits) is false;

    /// <summary>Whether <see cref="Colors"/> holds the same keys and values as <paramref name="other"/>'s (gradients and the single LED colors aside).</summary>
    public bool SameColorsAs(ThemeDefinition other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Colors.Count == other.Colors.Count && Colors.All(pair => other.Colors.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    public bool Equals(ThemeDefinition? other) =>
        other is not null
        && Name == other.Name
        && Variant == other.Variant
        && SameColorsAs(other)
        && MixerGradient == other.MixerGradient
        && ChainGradient == other.ChainGradient
        && GainGradient == other.GainGradient
        && MidiColor == other.MidiColor
        && PeakMarkerColor == other.PeakMarkerColor;

    public override int GetHashCode() => HashCode.Combine(Name, Variant, Colors.Count, MixerGradient, ChainGradient, GainGradient, MidiColor, PeakMarkerColor);

    internal static ImmutableSortedDictionary<string, string> NormalizeColors(IEnumerable<KeyValuePair<string, string>> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in colors)
        {
            if (string.IsNullOrWhiteSpace(key) || !IsColor(value))
            {
                throw new ArgumentException($"Theme color \"{key}\" must be \"#RRGGBB\" or \"#AARRGGBB\" (got \"{value}\").", nameof(colors));
            }

            builder[key] = value.ToUpperInvariant();
        }

        return builder.ToImmutable();
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789ABCDEFabcdef");
}
