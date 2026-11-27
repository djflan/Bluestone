using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bluestone.Tests.Unit.Playback;

namespace Bluestone.Tests.Unit.Architecture;

/// <summary>
/// Keeps docs/design-language.md true (ADR 0052): every token it names exists in Theme/Tokens.axaml (or, for icons,
/// Theme/Bluestone.axaml), and every control in
/// Bluestone.Desktop/Controls is in its catalog.
/// </summary>
public sealed partial class DesignLanguageGuideTests
{
    private static readonly string Desktop = Path.Combine(PlanDump.RepositoryRoot, "src", "dotnet", "Bluestone.Desktop");

    private static readonly string[] ThemeFiles = ["Tokens.axaml", "Bluestone.axaml"];

    private static readonly string Guide = File.ReadAllText(Path.Combine(PlanDump.RepositoryRoot, "docs", "design-language.md"));

    [Fact]
    public void EveryTokenTheGuideNames_ExistsInTheTokens()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var keys = ThemeFiles
            .SelectMany(file => XDocument.Load(Path.Combine(Desktop, "Theme", file)).Descendants())
            .Select(e => (string?)e.Attribute(x + "Key"))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var families = keys.Select(k => k.Split('.')[0]).ToHashSet(StringComparer.Ordinal);

        // A code span whose first part is a token family ("Surface.Base", "Accent") names a token; wildcards and ellipses
        // ("Surface.*", "Track.Color0…") name a family and are checked by their fixed prefix.
        var missing = CodeSpan().Matches(Guide)
            .Select(m => m.Groups[1].Value)
            .Where(span => families.Contains(span.Split('.')[0]) && !span.Contains('/', StringComparison.Ordinal))
            .Where(span => !(span.Contains('*', StringComparison.Ordinal) || span.EndsWith('…')
                ? keys.Any(k => k.StartsWith(span.Split('*', '…')[0], StringComparison.Ordinal))
                : keys.Contains(span)))
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, $"docs/design-language.md names tokens the theme lacks: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryControl_IsInTheGuidesCatalog()
    {
        var controls = Directory.GetFiles(Path.Combine(Desktop, "Controls"), "*.cs")
            .Select(path => Path.GetFileName(path).Split('.')[0])
            .Distinct(StringComparer.Ordinal);

        var missing = controls.Where(name => !Guide.Contains($"`{name}`", StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0, $"Add these controls to the catalog in docs/design-language.md: {string.Join(", ", missing)}");
    }

    [GeneratedRegex("`([A-Za-z][A-Za-z0-9.*…]*)`")]
    private static partial Regex CodeSpan();
}
