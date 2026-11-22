using Avalonia.Media;

namespace Bluestone.Desktop.Theme;

/// <summary>Values used by custom-drawn controls. Kept in step with Theme/Tokens.axaml by a UI test.</summary>
internal static class Palette
{
    public static readonly Color Base = Color.Parse("#12151A");
    public static readonly Color Panel = Color.Parse("#181C22");
    public static readonly Color Sunken = Color.Parse("#0C0F13");
    public static readonly Color Raised = Color.Parse("#20252D");
    public static readonly Color Divider = Color.Parse("#0A0C0F");

    // Arrangement and editor surfaces
    public static readonly Color Lane = Color.Parse("#12151A");
    public static readonly Color LaneAlt = Color.Parse("#14171C");
    public static readonly Color LaneSelected = Color.Parse("#1A2128");
    public static readonly Color LaneDivider = Color.Parse("#0A0C0F");
    public static readonly Color AutomationLane = Color.Parse("#101318");
    public static readonly Color Ruler = Color.Parse("#1C2027");
    public static readonly Color RulerEdge = Color.Parse("#2A2F37");
    public static readonly Color GridBar = Color.Parse("#262A31");
    public static readonly Color GridBeat = Color.Parse("#1C2026");
    public static readonly Color GridStep = Color.Parse("#171A1F");

    // Piano roll rows
    public static readonly Color WhiteKeyRow = Color.Parse("#1A1E24");
    public static readonly Color BlackKeyRow = Color.Parse("#14171C");
    public static readonly Color OctaveLine = Color.Parse("#0A0C0F");
    public static readonly Color WhiteKey = Color.Parse("#DADADC");
    public static readonly Color WhiteKeyPressed = Color.Parse("#8FE2F8");
    public static readonly Color BlackKey = Color.Parse("#0C0F13");
    public static readonly Color KeyLabel = Color.Parse("#4E535B");

    public static readonly Color Text = Color.Parse("#DADCE0");
    public static readonly Color TextSecondary = Color.Parse("#A0A4AB");
    public static readonly Color TextMuted = Color.Parse("#6C7078");
    public static readonly Color Accent = Color.Parse("#3CCBF4");
    public static readonly Color Selection = Color.Parse("#E9EEF6");
    public static readonly Color Playhead = Color.Parse("#E6E6E8");
    public static readonly Color Cycle = Color.Parse("#3CCBF4");
    public static readonly Color Record = Color.Parse("#E5484D");
    public static readonly Color Focus = Color.Parse("#63D5F6");
    public static readonly Color Field = Color.Parse("#11141A");
    public static readonly Color Border = Color.Parse("#2E333B");

    // Knobs (Knob.* tokens): a dark body inside a ring of thin radial ticks; unlit ticks clear 3:1 on the panel.
    public static readonly Color KnobBody = Color.Parse("#2A2F37");
    public static readonly Color KnobTick = Color.Parse("#6C7078");

    // What a device works on (Signal.* tokens)
    public static readonly Color SignalNote = Color.Parse("#D7A24A");
    public static readonly Color SignalInstrument = Color.Parse("#9C82D9");
    public static readonly Color SignalAudio = Color.Parse("#4FB3B0");
    public static readonly Color SignalOther = Color.Parse("#8A8A92");

    // Levels (Meter.* tokens)
    public static readonly Color MeterLow = Color.Parse("#3CC8FF");
    public static readonly Color MeterMid = Color.Parse("#3D5BFF");
    public static readonly Color MeterHot = Color.Parse("#FF2E93");
    public static readonly Color MeterClip = Color.Parse("#FF3036");
    public static readonly Color MeterTrack = Color.Parse("#0C0F13");
    // Mixer-only DSP display (kept separate from Device View junction meters).
    public static readonly Color MixerMeterTrack = Color.Parse("#0C0F13");
    public static readonly Color MixerMeterFill = Color.Parse("#4FB3B0");
    public static readonly Color MixerMeterHot = Color.Parse("#E0A43A");
    public static readonly Color MixerMeterPeakHold = Color.Parse("#F2F2F2");
    public static readonly Color MixerMeterWarming = Color.Parse("#E0A43A");
    public static readonly Color MixerMeterUnavailable = Color.Parse("#6E6E75");
    public static readonly Color JunctionLamp = Lighten(MeterLow, 0.25);

    // The Machined console (Console.* tokens): recessed gutters and wells, square-key faces, and the lamps they hold.
    public static readonly Color PanelHeader = Color.Parse("#20252D");
    public static readonly Color Gutter = Color.Parse("#11141A");
    public static readonly Color Well = Color.Parse("#0C0F13");
    public static readonly Color Key = Color.Parse("#2E333B");
    public static readonly Color Sheen = Color.Parse("#DCE8FF");
    public static readonly Color Slot = Color.Parse("#050506");
    public static readonly Color SelectedText = Color.Parse("#F4F7FA");
    public static readonly Color Mute = Color.Parse("#4C8DDB");
    public static readonly Color Solo = Color.Parse("#E3C345");
    public static readonly Color SoloInk = Color.Parse("#1A1608");
    public static readonly Color Clip = Color.Parse("#FF3640");
    public static readonly Color[] GainLeds = [Color.Parse("#6B4BE0"), Color.Parse("#A45CF2"), Color.Parse("#E070C8"), Color.Parse("#FFA3C4")];
    public const double GlowAlpha = 0.28;
    public const double GlowReach = 4;
    public const double SheenAlpha = 0.035;
    public const double GutterDepth = 0.22;
    public const double UnlitAlpha = 0.13;

    /// <summary>Track colours, muted so dense arrangements stay readable; status is never conveyed by these alone.</summary>
    public static readonly Color[] Tracks =
    [
        Color.Parse("#3D7FD6"),
        Color.Parse("#3FA86B"),
        Color.Parse("#C99A2E"),
        Color.Parse("#C8534F"),
        Color.Parse("#8A63D2"),
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

    /// <summary>Mixes every channel, alpha included, the way the identity study painted.</summary>
    public static Color Blend(Color color, Color target, double amount) => Color.FromArgb(
        (byte)Math.Round(color.A + ((target.A - color.A) * amount)),
        (byte)Math.Round(color.R + ((target.R - color.R) * amount)),
        (byte)Math.Round(color.G + ((target.G - color.G) * amount)),
        (byte)Math.Round(color.B + ((target.B - color.B) * amount)));

    /// <summary><paramref name="color"/> at <paramref name="alpha"/> of its own opacity.</summary>
    public static Color Alpha(Color color, double alpha) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * color.A), color.R, color.G, color.B);
}
