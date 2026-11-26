using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Bluestone.Desktop.Controls;
using AvaloniaApplication = Avalonia.Application;

namespace Bluestone.Tests.Ui;

public sealed class ThemeFontTests
{
    [Theory]
    [InlineData("Inter-OFL.txt", "The Inter Project Authors")]
    [InlineData("JetBrainsMono-OFL.txt", "The JetBrains Mono Project Authors")]
    [InlineData("NotoMusic-OFL.txt", "The Noto Project Authors")]
    public void TheBuildOutput_ContainsReadableFontLicenseNotices(string fileName, string authors)
    {
        // The Desktop project's output content is copied alongside its assembly in this test build.
        var notice = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "licenses", fileName));
        Assert.Contains(authors, notice, StringComparison.Ordinal);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", notice, StringComparison.Ordinal);
        Assert.Contains("TERMINATION", notice, StringComparison.Ordinal);
    }

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

    [Theory]
    [InlineData(FontWeight.Normal)]
    [InlineData(FontWeight.Medium)]
    [InlineData(FontWeight.SemiBold)]
    [InlineData(FontWeight.Bold)]
    public Task TheMonoToken_ResolvesTheRequestedRealWeight(FontWeight weight) => HeadlessApp.RunAsync(() =>
    {
        var typeface = new Typeface(Family("Font.Mono"), FontStyle.Normal, weight);
        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
        Assert.StartsWith("JetBrains Mono", glyphTypeface.FamilyName, StringComparison.Ordinal);
        Assert.Equal(weight, glyphTypeface.Weight);
        Assert.Equal(FontSimulations.None, glyphTypeface.FontSimulations);
        Assert.True(glyphTypeface.CharacterToGlyphMap.TryGetGlyph('4', out var glyph));
        Assert.NotEqual((ushort)0, glyph);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheUiToken_ShapesASharpAccidentalWithNotoMusic() => HeadlessApp.RunAsync(() =>
    {
        AssertSharpShapedBy(new Typeface(Family("Font.Ui")), "Noto Music");
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMonoToken_ShapesASharpAccidentalWithoutAMissingGlyph() => HeadlessApp.RunAsync(() =>
    {
        AssertSharpShapedBy(new Typeface(Family("Font.Mono")), null);
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheMonoToken_FallsBackToNotoForAMusicalSymbolMissingFromJetBrainsMono() => HeadlessApp.RunAsync(() =>
    {
        AssertSymbolShapedBy(new Typeface(Family("Font.Mono")), "𝄞", 0x1D11E, "Noto Music");
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheRealWindowAndItsStyledControls_UseTheFontTokens() => HeadlessApp.RunAsync(async () =>
    {
        await using var app = await AppUnderTest.OpenAsync();
        AssertSharpShapedBy(new Typeface(app.Window.FontFamily), "Noto Music");
        var ui = Family("Font.Ui");
        var mono = Family("Font.Mono");

        Assert.Equal(ui, app.Window.FontFamily);
        var label = app.Window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("fieldLabel"));
        Assert.Equal(ui, label.FontFamily);
        AssertSharpShapedBy(new Typeface(label.FontFamily, FontStyle.Normal, label.FontWeight), "Noto Music");

        var tempo = app.Find<TextBox>("TempoBox");
        Assert.Equal(mono, tempo.FontFamily);
        AssertSharpShapedBy(new Typeface(tempo.FontFamily, FontStyle.Normal, tempo.FontWeight), null);

        var readout = app.Window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("keysValue"));
        Assert.Equal(mono, readout.FontFamily);
        Assert.Equal(FontWeight.Medium, readout.FontWeight);
        AssertSharpShapedBy(new Typeface(readout.FontFamily, FontStyle.Normal, readout.FontWeight), null);
    });

    [Theory]
    [InlineData(typeof(PianoRollView), "Face", FontWeight.Medium)]
    [InlineData(typeof(TimeGrid), "Face", FontWeight.Medium)]
    [InlineData(typeof(TimelineView), "RegionFace", FontWeight.SemiBold)]
    public Task TheCustomDrawnText_UsesTheUiToken(Type control, string fieldName, FontWeight weight) => HeadlessApp.RunAsync(() =>
    {
        // These cached faces feed the controls' FormattedText for note, ruler and region labels.
        var field = control.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var face = Assert.IsType<Typeface>(field.GetValue(null));
        AssertSharpShapedBy(face, "Noto Music");
        Assert.Equal(Family("Font.Ui"), face.FontFamily);
        Assert.Equal(weight, face.Weight);
        return Task.CompletedTask;
    });

    private static FontFamily Family(string key)
    {
        var app = Assert.IsType<Bluestone.Desktop.App>(AvaloniaApplication.Current);
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant ?? ThemeVariant.Dark, out var resource));
        return Assert.IsType<FontFamily>(resource);
    }

    private static void AssertSharpShapedBy(Typeface face, string? family) =>
        AssertSymbolShapedBy(face, "♯", 0x266F, family);

    private static void AssertSymbolShapedBy(Typeface face, string symbol, int codepoint, string? family)
    {
        using var layout = new TextLayout($"C{symbol}4", face, 12, Brushes.White);
        var sharp = Assert.Single(
            layout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>(),
            run => run.ShapedBuffer.Text.ToString().Contains(symbol, StringComparison.Ordinal));
        if (family is not null)
        {
            Assert.Equal(family, sharp.ShapedBuffer.GlyphTypeface.FamilyName);
        }

        Assert.True(sharp.ShapedBuffer.GlyphTypeface.CharacterToGlyphMap.TryGetGlyph(codepoint, out var glyph));
        Assert.NotEqual((ushort)0, glyph);
        Assert.NotEmpty(sharp.GlyphRun.GlyphInfos);
        Assert.All(sharp.GlyphRun.GlyphInfos, info => Assert.NotEqual((ushort)0, info.GlyphIndex));
    }
}
