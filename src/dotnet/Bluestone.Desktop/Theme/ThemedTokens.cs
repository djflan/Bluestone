using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// The design tokens (Theme/Tokens.axaml) with the theme's colors applied (ADR 0053): Theme/Bluestone.axaml merges this
/// instead of the tokens file, so the theme is in place before any style below it, any window, or any code-drawn control
/// (whose static brushes read <see cref="Palette"/> as their types load) reads a color.
/// </summary>
public sealed class ThemedTokens : ResourceDictionary
{
    /// <summary>The tokens file, as shipped: Dark Sodalite's colors.</summary>
    public static readonly Uri Source = new("avares://Bluestone.Desktop/Theme/Tokens.axaml");

    public ThemedTokens()
    {
        var tokens = (ResourceDictionary)AvaloniaXamlLoader.Load(Source);
        ThemeApplier.Startup(tokens);
        MergedDictionaries.Add(tokens);
    }
}
