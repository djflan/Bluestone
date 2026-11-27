using System.Text.RegularExpressions;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Keeps colors in the tokens (ADR 0036, ADR 0052, ADR 0053): views and styles name a token, never a raw color, so every
/// color follows the theme. Code that still draws in raw colors is listed below, file by file, with why; a new raw color
/// fails here, and so does a list entry left behind when its colors move into tokens.
/// </summary>
public sealed partial class RawColorLintTests
{
    /// <summary>XAML that may hold a raw color, with how many and why. None: every view and style uses tokens.</summary>
    private static readonly Dictionary<string, (int Count, string Reason)> XamlAllowed = new(StringComparer.Ordinal);

    /// <summary>
    /// Code that still draws in raw colors, with how many and why. These do not follow the theme; they are the design guide's
    /// Known gaps until their colors move into tokens (then shrink the count, or remove the entry).
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Reason)> CodeAllowed = new(StringComparer.Ordinal)
    {
        ["Controls/StoneMark.cs"] = (54, "The Bluestone mark is brand artwork (a fixed mineral palette), not interface color: it never follows a theme."),
        ["Views/SplashWindow.cs"] = (5, "The splash is drawn over the brand marble before the workspace exists: fixed scrim and text colors for that artwork."),
        ["Controls/TimeGrid.cs"] = (8, "Known gap: the ruler's cycle strip, selected edge, ticks, text, and drag feedback predate the R6 tokens."),
        ["Controls/PianoRollView.cs"] = (7, "Known gap: the piano roll's row pen, key edge and label, corner, velocity lane, and note text predate the R6 tokens."),
        ["Controls/ArcKnobPaint.cs"] = (5, "Known gap: the knob cap and face shading and its pointer are fixed material colors, not yet tokens."),
        ["Controls/ConsoleFader.cs"] = (6, "Known gap: the fader cap's body shading is a fixed material color, not yet tokens."),
        ["Controls/ColourWheel.cs"] = (3, "Known gap: the color wheel's lip, ticks, and thumb keyline are neutral white and black overlays, not yet tokens."),
        ["Controls/BrightnessFader.cs"] = (1, "Known gap: the brightness fader cap's shadow is a fixed black overlay, not yet a token."),
        ["Controls/TimelineView.cs"] = (1, "Known gap: region name text drawn on track colors is a fixed near-white, not yet a token."),
        ["Controls/MarbleSlab.cs"] = (1, "The marble's fallback fill, drawn only if the marble artwork cannot load (brand artwork, as the splash)."),
        ["Views/Shortcuts.cs"] = (1, "Known gap: the shortcuts window's background predates the R6 tokens."),
    };

    private static readonly string[] CodeFolders = ["Controls", "Views", "Theme"];

    private static string Desktop => Repo.File("src/dotnet/Bluestone.Desktop");

    [Fact]
    public void ViewsAndStyles_UseColorTokens_NotRawColors()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Desktop, "Views"), "*.axaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Desktop, "Controls"), "*.axaml", SearchOption.AllDirectories))
            .Append(Path.Combine(Desktop, "Theme", "Bluestone.axaml"))
            .Append(Path.Combine(Desktop, "App.axaml"));
        var found = Sites(files, text => XamlComment().Replace(text, m => new string('\n', m.Value.Count(c => c == '\n'))), XamlColor());

        // App.axaml's Fluent palette is the stock controls' part of the theme: its values are read as the theme's
        // "Fluent.*" colors (ThemeApplierTests), so it is the one place outside the tokens that writes colors.
        found.Remove("App.axaml");
        Check(found, XamlAllowed);
    }

    [Fact]
    public void CodeDrawnControls_ReadColorsFromThePalette_NotRawColors()
    {
        var files = CodeFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Desktop, folder), "*.cs", SearchOption.AllDirectories));
        var found = Sites(files, text => CodeComment().Replace(text, m => new string('\n', m.Value.Count(c => c == '\n'))), CodeColor());
        Check(found, CodeAllowed);
    }

    /// <summary>Every raw color in <paramref name="files"/>, by file under Bluestone.Desktop, as "line: text".</summary>
    private static Dictionary<string, List<string>> Sites(IEnumerable<string> files, Func<string, string> stripComments, Regex color)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var lines = stripComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in color.Matches(lines[i]))
                {
                    var relative = Path.GetRelativePath(Desktop, file).Replace(Path.DirectorySeparatorChar, '/');
                    (found.TryGetValue(relative, out var list) ? list : found[relative] = []).Add($"{i + 1}: {match.Value}");
                }
            }
        }

        return found;
    }

    private static void Check(Dictionary<string, List<string>> found, Dictionary<string, (int Count, string Reason)> allowed)
    {
        var problems = new List<string>();
        foreach (var (file, sites) in found.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!allowed.TryGetValue(file, out var allowance))
            {
                problems.Add($"{file} has raw colors; use a color token (Theme/Tokens.axaml, or Palette in code):\n    {string.Join("\n    ", sites)}");
            }
            else if (sites.Count > allowance.Count)
            {
                problems.Add($"{file} has {sites.Count} raw colors, more than the {allowance.Count} allowed ({allowance.Reason}); use a color token:\n    {string.Join("\n    ", sites)}");
            }
            else if (sites.Count < allowance.Count)
            {
                problems.Add($"{file} has {sites.Count} raw colors, fewer than the {allowance.Count} allowed: lower its count here (and in the design guide's Known gaps).");
            }
        }

        problems.AddRange(allowed.Keys.Where(file => !found.ContainsKey(file)).Select(file => $"{file} has no raw colors left: remove it from the list here (and from the design guide's Known gaps)."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // A color literal in XAML: "#RGB" forms of 6 or 8 hex digits, as an attribute value or element text.
    [GeneratedRegex("#[0-9A-Fa-f]{8}\\b|#[0-9A-Fa-f]{6}\\b")]
    private static partial Regex XamlColor();

    // A color written in code: a "#RRGGBB" or "#AARRGGBB" string, or Color.FromRgb/FromArgb with only number literals.
    [GeneratedRegex("\"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6})\"|Color\\.From(?:Argb|Rgb)\\(\\s*\\d+\\s*(?:,\\s*\\d+\\s*){2,3}\\)")]
    private static partial Regex CodeColor();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();

    [GeneratedRegex("//[^\\n]*|/\\*.*?\\*/", RegexOptions.Singleline)]
    private static partial Regex CodeComment();
}
