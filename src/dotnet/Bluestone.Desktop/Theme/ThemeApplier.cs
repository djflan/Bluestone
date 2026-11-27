using System.Collections.Immutable;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// Puts a theme's colors (ADR 0053) into the application. Every color is a <see cref="Color"/> resource in
/// Theme/Tokens.axaml (or a "Fluent.*" color of the stock controls' palette in App.axaml); the shipped values are read from
/// there, so Tokens.axaml stays the one source of Dark Sodalite. <see cref="ThemedTokens"/> applies the theme as the tokens
/// load, before any style, window, or code-drawn control reads a color.
/// </summary>
/// <remarks>
/// Brushes are objects that styles hold once they have read them, so a theme never swaps a brush for a new one: it replaces
/// each <see cref="Color"/> resource and then sets the color of every token brush in place. A solid brush follows the color
/// whose key is its own with "Color" added (after a "." when the key is one word); <see cref="SolidSources"/> lists the
/// exceptions. A gradient brush's stops follow <see cref="GradientSources"/>. A shadow is a value, so each in
/// <see cref="ShadowSources"/> is rebuilt from its color tokens; a style setter that already copied a shadow keeps it, which
/// is why a theme applies at startup, before the styles load. UI thread only.
/// </remarks>
internal static class ThemeApplier
{
    /// <summary>The prefix of a stock-control palette color's key, for example "Fluent.Accent".</summary>
    public const string FluentPrefix = "Fluent.";

    /// <summary>Solid brushes that follow a color other than the one their key names.</summary>
    internal static readonly ImmutableDictionary<string, string> SolidSources = ImmutableDictionary.CreateRange(
        StringComparer.Ordinal,
        [
            // A neutral focus ring on controls whose "on" state is already an aqua rim: the primary text color.
            new KeyValuePair<string, string>("State.FocusOnLit", "Text.PrimaryColor"),
        ]);

    /// <summary>Each gradient brush's stops, top to bottom, by the color token each follows.</summary>
    internal static readonly ImmutableDictionary<string, ImmutableArray<string>> GradientSources = ImmutableDictionary.CreateRange(
        StringComparer.Ordinal,
        [
            Stops("Key.Accent.Face", "Key.Accent.TopColor", "Key.Accent.BottomColor"),
            Stops("Key.Record.Face", "Key.Record.TopColor", "Key.Record.BottomColor"),
            Stops("Key.Cycle.Face", "Key.Cycle.TopColor", "Key.Cycle.BottomColor"),
            Stops("Key.Solo.Face", "Key.Solo.TopColor", "Key.Solo.BottomColor"),
            Stops("DevicePanel.SelectedFill", "DevicePanel.SelectedFill.TopColor", "DevicePanel.SelectedFill.MiddleColor", "DevicePanel.SelectedFill.BottomColor"),
            Stops("Gradient.Bar", "Gradient.Bar.TopColor", "Gradient.Bar.BottomColor"),
            Stops("Gradient.Header", "Gradient.Header.TopColor", "Gradient.Header.BottomColor"),
            Stops("Gradient.Button", "Gradient.Button.TopColor", "Gradient.Button.BottomColor"),
        ]);

    /// <summary>Each shadow's layers, in order, by the color token each is drawn in.</summary>
    internal static readonly ImmutableDictionary<string, ImmutableArray<string>> ShadowSources = ImmutableDictionary.CreateRange(
        StringComparer.Ordinal,
        [
            Stops("Shadow.Focus", "Shadow.FocusColor"),
            Stops("Shadow.Popover", "Shadow.PopoverColor", "Shadow.PopoverContactColor"),
            Stops("Shadow.AccentGlow", "Shadow.AccentGlowColor"),
            Stops("Shadow.AccentGlowTight", "Shadow.AccentGlowTightColor"),
            Stops("Shadow.Inset", "Shadow.InsetColor"),
            Stops("Shadow.RecordGlow", "Shadow.RecordGlowColor"),
        ]);

    /// <summary>The stock controls' palette colors a theme holds (the ones App.axaml sets), by their "Fluent.*" name.</summary>
    internal static readonly ImmutableArray<(string Name, Func<ColorPaletteResources, Color> Get, Action<ColorPaletteResources, Color> Set)> FluentColors =
    [
        ("Accent", p => p.Accent, (p, c) => p.Accent = c),
        ("RegionColor", p => p.RegionColor, (p, c) => p.RegionColor = c),
        ("AltHigh", p => p.AltHigh, (p, c) => p.AltHigh = c),
        ("AltMedium", p => p.AltMedium, (p, c) => p.AltMedium = c),
        ("AltMediumHigh", p => p.AltMediumHigh, (p, c) => p.AltMediumHigh = c),
        ("AltLow", p => p.AltLow, (p, c) => p.AltLow = c),
        ("AltMediumLow", p => p.AltMediumLow, (p, c) => p.AltMediumLow = c),
        ("BaseHigh", p => p.BaseHigh, (p, c) => p.BaseHigh = c),
        ("BaseMediumHigh", p => p.BaseMediumHigh, (p, c) => p.BaseMediumHigh = c),
        ("BaseMedium", p => p.BaseMedium, (p, c) => p.BaseMedium = c),
        ("BaseMediumLow", p => p.BaseMediumLow, (p, c) => p.BaseMediumLow = c),
        ("BaseLow", p => p.BaseLow, (p, c) => p.BaseLow = c),
        ("ChromeAltLow", p => p.ChromeAltLow, (p, c) => p.ChromeAltLow = c),
        ("ChromeHigh", p => p.ChromeHigh, (p, c) => p.ChromeHigh = c),
        ("ChromeMedium", p => p.ChromeMedium, (p, c) => p.ChromeMedium = c),
        ("ChromeMediumLow", p => p.ChromeMediumLow, (p, c) => p.ChromeMediumLow = c),
        ("ChromeLow", p => p.ChromeLow, (p, c) => p.ChromeLow = c),
        ("ListLow", p => p.ListLow, (p, c) => p.ListLow = c),
        ("ListMedium", p => p.ListMedium, (p, c) => p.ListMedium = c),
        ("ErrorText", p => p.ErrorText, (p, c) => p.ErrorText = c),
    ];

    /// <summary>The design tokens the application loaded, with the theme applied; null until <see cref="ThemedTokens"/> loads.</summary>
    internal static ResourceDictionary? Tokens { get; private set; }

    /// <summary>
    /// Applies the startup theme to <paramref name="tokens"/> as they load: Dark Sodalite is read from them (and the stock
    /// controls' palette), and the application chooses the theme to use over it (App.ChooseTheme: the theme file, or the
    /// shipped theme under test).
    /// </summary>
    internal static void Startup(ResourceDictionary tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        Tokens = tokens;
        var app = CurrentApp;
        var shipped = ThemeDefinition.DarkSodalite(Colors(tokens, app));
        var theme = (app as App)?.ChooseTheme(shipped) ?? shipped;
        Apply(theme, tokens, app);
    }

    /// <summary>
    /// Every color in <paramref name="tokens"/> and the stock controls' palette, by theme key: each <see cref="Color"/>
    /// resource by its key, and each of <see cref="FluentColors"/> as "Fluent.&lt;name&gt;". Read before a theme is applied,
    /// these are Dark Sodalite.
    /// </summary>
    internal static ImmutableSortedDictionary<string, string> Colors(ResourceDictionary tokens, Avalonia.Application app)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(app);
        var colors = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var key in tokens.Keys.OfType<string>().ToList())
        {
            if (tokens.TryGetValue(key, out var value) && value is Color color)
            {
                colors[key] = Hex(color);
            }
        }

        var palette = Fluent(app, Avalonia.Styling.ThemeVariant.Dark);
        foreach (var (name, get, _) in FluentColors)
        {
            colors[FluentPrefix + name] = Hex(get(palette));
        }

        return colors.ToImmutable();
    }

    /// <summary>
    /// Applies <paramref name="theme"/>'s colors and variant to the loaded tokens. Keys the application does not have are
    /// ignored. At startup this runs before anything reads a color; run later (as tests do), brushes follow at once, but
    /// shadows a style has already copied do not.
    /// </summary>
    public static void Apply(ThemeDefinition theme) =>
        Apply(theme, Tokens ?? throw new InvalidOperationException("The design tokens have not loaded."), CurrentApp);

    internal static void Apply(ThemeDefinition theme, ResourceDictionary tokens, Avalonia.Application app)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(app);

        // Every resource is created now (resources can load on first use), so the brushes below are the ones styles hold.
        var keys = tokens.Keys.OfType<string>().ToList();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            values[key] = tokens.TryGetValue(key, out var value) ? value : null;
        }

        foreach (var (key, hex) in theme.Colors)
        {
            if (values.TryGetValue(key, out var value) && value is Color && Color.TryParse(hex, out var color))
            {
                tokens[key] = color;
                values[key] = color;
            }
        }

        foreach (var key in keys)
        {
            switch (values[key])
            {
                case SolidColorBrush solid when values.GetValueOrDefault(SolidSource(key)) is Color color:
                    solid.Color = color;
                    break;
                case GradientBrush gradient when GradientSources.TryGetValue(key, out var stops) && stops.Length == gradient.GradientStops.Count:
                    for (var i = 0; i < stops.Length; i++)
                    {
                        if (values.GetValueOrDefault(stops[i]) is Color stop)
                        {
                            gradient.GradientStops[i].Color = stop;
                        }
                    }

                    break;
            }
        }

        foreach (var (key, layers) in ShadowSources)
        {
            if (values.GetValueOrDefault(key) is BoxShadows shadow && Shadow(shadow, layers, values) is { } themed)
            {
                tokens[key] = themed;
            }
        }

        var variant = Variant(theme.Variant);
        var palette = Fluent(app, variant);
        foreach (var (name, _, set) in FluentColors)
        {
            if (theme.Colors.TryGetValue(FluentPrefix + name, out var hex) && Color.TryParse(hex, out var color))
            {
                set(palette, color);
            }
        }

        app.RequestedThemeVariant = variant;
    }

    /// <summary>The color token <paramref name="key"/> as the application has it now: what code-drawn controls paint with.</summary>
    public static Color Token(string key) =>
        Tokens is null ? throw new InvalidOperationException("The design tokens have not loaded: colors are read once the application has started.")
        : Tokens.TryGetValue(key, out var value) && value is Color color ? color
        : throw new InvalidOperationException($"There is no color token \"{key}\" in Theme/Tokens.axaml.");

    /// <summary>The Avalonia scheme a theme variant runs under: Light for light themes, Dark for midtoned and dark ones.</summary>
    public static Avalonia.Styling.ThemeVariant Variant(Presentation.ThemeVariant variant) =>
        variant == Presentation.ThemeVariant.Light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;

    /// <summary>
    /// The color token a solid brush follows: <see cref="SolidSources"/>, else its key with "Color" added, after a "." when
    /// the key is one word ("Surface.Base" follows "Surface.BaseColor"; "Accent" follows "Accent.Color").
    /// </summary>
    internal static string SolidSource(string brushKey) =>
        SolidSources.TryGetValue(brushKey, out var source) ? source
        : brushKey.Contains('.', StringComparison.Ordinal) ? brushKey + "Color"
        : brushKey + ".Color";

    /// <summary>"#RRGGBB" for an opaque color, otherwise "#AARRGGBB", in upper case.</summary>
    public static string Hex(Color color) => color.A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");

    private static Avalonia.Application CurrentApp =>
        Avalonia.Application.Current ?? throw new InvalidOperationException("A theme needs the running application.");

    private static ColorPaletteResources Fluent(Avalonia.Application app, Avalonia.Styling.ThemeVariant variant)
    {
        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault()
            ?? throw new InvalidOperationException("App.axaml's FluentTheme must come before Theme/Bluestone.axaml, which applies the theme.");
        if (!fluent.Palettes.TryGetValue(variant, out var palette))
        {
            palette = new ColorPaletteResources();
            fluent.Palettes[variant] = palette;
        }

        return palette;
    }

    private static BoxShadows? Shadow(BoxShadows shadow, ImmutableArray<string> layers, Dictionary<string, object?> values)
    {
        if (shadow.Count != layers.Length)
        {
            return null;
        }

        var rebuilt = new BoxShadow[shadow.Count];
        for (var i = 0; i < shadow.Count; i++)
        {
            rebuilt[i] = shadow[i];
            if (values.GetValueOrDefault(layers[i]) is Color color)
            {
                rebuilt[i].Color = color;
            }
        }

        return rebuilt.Length == 1 ? new BoxShadows(rebuilt[0]) : new BoxShadows(rebuilt[0], rebuilt[1..]);
    }

    private static KeyValuePair<string, ImmutableArray<string>> Stops(string key, params string[] colors) => new(key, [.. colors]);
}
