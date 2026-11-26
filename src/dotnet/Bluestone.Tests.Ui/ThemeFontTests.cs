using System.Globalization;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaApplication = Avalonia.Application;

namespace Bluestone.Tests.Ui;

public sealed class ThemeFontTests
{
    [Fact]
    public Task TheAppTheme_ResolvesInterJetBrainsMono_AndNotoMusicForTheSharpAccidental() => HeadlessApp.RunAsync(() =>
    {
        var app = Assert.IsType<Bluestone.Desktop.App>(AvaloniaApplication.Current);

        Assert.True(app.TryGetResource("Font.Ui", app.ActualThemeVariant ?? ThemeVariant.Dark, out var uiResource));
        var uiFamily = Assert.IsType<FontFamily>(uiResource);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(uiFamily), out var uiTypeface));
        Assert.Equal("Inter", uiTypeface.FamilyName);

        Assert.True(app.TryGetResource("Font.Mono", app.ActualThemeVariant ?? ThemeVariant.Dark, out var monoResource));
        var monoFamily = Assert.IsType<FontFamily>(monoResource);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(monoFamily), out var monoTypeface));
        Assert.Equal("JetBrains Mono", monoTypeface.FamilyName);

        Assert.True(FontManager.Current.TryMatchCharacter(
            0x266F,
            FontStyle.Normal,
            FontWeight.Normal,
            FontStretch.Normal,
            uiFamily,
            CultureInfo.InvariantCulture,
            out var accidentalTypeface));
        Assert.True(FontManager.Current.TryGetGlyphTypeface(accidentalTypeface, out var accidentalGlyphTypeface));
        Assert.Equal("Noto Music", accidentalGlyphTypeface.FamilyName);
        Assert.True(accidentalGlyphTypeface.CharacterToGlyphMap.TryGetGlyph(0x266F, out var accidentalGlyph));
        Assert.NotEqual((ushort)0, accidentalGlyph);

        return Task.CompletedTask;
    });
}
