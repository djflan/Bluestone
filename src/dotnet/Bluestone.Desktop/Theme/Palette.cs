using Avalonia.Media;

namespace Bluestone.Desktop.Theme;

/// <summary>Colours used by custom-drawn controls. Kept in step with Theme/Bluestone.axaml.</summary>
internal static class Palette
{
    public static readonly Color Base = Color.Parse("#1C1C1E");
    public static readonly Color Panel = Color.Parse("#252527");
    public static readonly Color Raised = Color.Parse("#2E2E31");
    public static readonly Color Divider = Color.Parse("#111113");

    // Arrangement and editor surfaces
    public static readonly Color Lane = Color.Parse("#222224");
    public static readonly Color LaneAlt = Color.Parse("#252528");
    public static readonly Color LaneSelected = Color.Parse("#2D2E33");
    public static readonly Color LaneDivider = Color.Parse("#19191B");
    public static readonly Color AutomationLane = Color.Parse("#1D1D1F");
    public static readonly Color Ruler = Color.Parse("#2B2B2E");
    public static readonly Color RulerEdge = Color.Parse("#3A3A3E");
    public static readonly Color GridBar = Color.Parse("#3C3C41");
    public static readonly Color GridBeat = Color.Parse("#2E2E33");
    public static readonly Color GridStep = Color.Parse("#28282C");

    // Piano roll rows
    public static readonly Color WhiteKeyRow = Color.Parse("#2B2B2F");
    public static readonly Color BlackKeyRow = Color.Parse("#212124");
    public static readonly Color OctaveLine = Color.Parse("#17171A");
    public static readonly Color WhiteKey = Color.Parse("#DADADC");
    public static readonly Color WhiteKeyPressed = Color.Parse("#9FC3EE");
    public static readonly Color BlackKey = Color.Parse("#141416");
    public static readonly Color KeyLabel = Color.Parse("#55555B");

    public static readonly Color Text = Color.Parse("#D9D9DC");
    public static readonly Color TextSecondary = Color.Parse("#A1A1A7");
    public static readonly Color TextMuted = Color.Parse("#6E6E75");
    public static readonly Color Accent = Color.Parse("#4C8DDB");
    public static readonly Color Selection = Color.Parse("#E9EEF6");
    public static readonly Color Playhead = Color.Parse("#E6E6E8");
    public static readonly Color Cycle = Color.Parse("#C9A227");
    public static readonly Color Record = Color.Parse("#E5484D");

    /// <summary>Track colours, muted so dense arrangements stay readable; status is never conveyed by these alone.</summary>
    public static readonly Color[] Tracks =
    [
        Color.Parse("#4F95D9"),
        Color.Parse("#45A97A"),
        Color.Parse("#D49A3A"),
        Color.Parse("#C76563"),
        Color.Parse("#9479CC"),
        Color.Parse("#3EA9AC"),
        Color.Parse("#CF7AAB"),
        Color.Parse("#A3AC48"),
    ];

    public static Color Track(int index) => Tracks[((index % Tracks.Length) + Tracks.Length) % Tracks.Length];

    /// <summary>Mixes <paramref name="color"/> toward <paramref name="target"/> by <paramref name="amount"/> (0-1).</summary>
    public static Color Mix(Color color, Color target, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + ((target.R - color.R) * amount)),
        (byte)Math.Round(color.G + ((target.G - color.G) * amount)),
        (byte)Math.Round(color.B + ((target.B - color.B) * amount)));

    public static Color Lighten(Color color, double amount) => Mix(color, Colors.White, amount);

    public static Color Darken(Color color, double amount) => Mix(color, Colors.Black, amount);
}
