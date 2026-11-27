using Avalonia.Media;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// Colors and console constants for custom-drawn controls. Every color is read from its token in Theme/Tokens.axaml as the
/// application has it once the theme is applied (Theme/ThemeApplier.cs, at startup before any window), so code-drawn
/// controls follow the theme as styles do. Never add a raw color here: add a token and read it.
/// </summary>
internal static class Palette
{
    public static readonly Color Base = Token("Surface.BaseColor");
    public static readonly Color Panel = Token("Surface.PanelColor");
    public static readonly Color Sunken = Token("Surface.SunkenColor");
    public static readonly Color Raised = Token("Surface.RaisedColor");
    public static readonly Color Divider = Token("Line.DividerColor");

    // Arrangement and editor surfaces
    public static readonly Color Lane = Token("Lane.BackgroundColor");
    public static readonly Color LaneAlt = Token("Lane.AlternateColor");
    public static readonly Color LaneSelected = Token("Lane.SelectedColor");
    public static readonly Color LaneDivider = Token("Lane.DividerColor");
    public static readonly Color AutomationLane = Token("Lane.AutomationColor");
    public static readonly Color Ruler = Token("Lane.RulerColor");
    public static readonly Color RulerEdge = Token("Lane.RulerEdgeColor");
    public static readonly Color GridBar = Token("Grid.BarColor");
    public static readonly Color GridBeat = Token("Grid.BeatColor");
    public static readonly Color GridStep = Token("Grid.StepColor");

    // Piano roll rows
    public static readonly Color WhiteKeyRow = Token("PianoRoll.WhiteKeyRowColor");
    public static readonly Color BlackKeyRow = Token("PianoRoll.BlackKeyRowColor");
    public static readonly Color OctaveLine = Token("PianoRoll.OctaveLineColor");
    public static readonly Color WhiteKey = Token("PianoRoll.WhiteKeyColor");
    public static readonly Color WhiteKeyPressed = Token("PianoRoll.WhiteKeyPressedColor");
    public static readonly Color BlackKey = Token("PianoRoll.BlackKeyColor");
    public static readonly Color KeyLabel = Token("PianoRoll.KeyLabelColor");

    public static readonly Color Text = Token("Text.PrimaryColor");
    public static readonly Color TextSecondary = Token("Text.SecondaryColor");
    public static readonly Color TextMuted = Token("Text.MutedColor");
    public static readonly Color Accent = Token("Accent.Color");
    public static readonly Color Selection = Token("Lane.SelectionColor");
    public static readonly Color Playhead = Token("Lane.PlayheadColor");
    public static readonly Color Cycle = Token("Transport.CycleColor");
    public static readonly Color Record = Token("State.ErrorColor");
    public static readonly Color Focus = Token("State.FocusColor");
    public static readonly Color Field = Token("Surface.FieldColor");
    public static readonly Color Border = Token("Line.BorderColor");
    public static readonly Color BorderStrong = Token("Line.BorderStrongColor");
    public static readonly Color Edge = Token("Line.EdgeColor");
    public static readonly Color Overlay = Token("Surface.OverlayColor");

    // What a device works on (Signal.* tokens)
    public static readonly Color SignalNote = Token("Signal.NoteColor");
    public static readonly Color SignalInstrument = Token("Signal.InstrumentColor");
    public static readonly Color SignalAudio = Token("Signal.AudioColor");
    public static readonly Color SignalOther = Token("Signal.OtherColor");

    // Levels (Meter.* tokens)
    public static readonly Color MeterLow = Token("Meter.LowColor");
    public static readonly Color MeterMid = Token("Meter.MidColor");
    public static readonly Color MeterHot = Token("Meter.HotColor");
    public static readonly Color MeterClip = Token("Meter.ClipColor");
    public static readonly Color MeterTrack = Token("Meter.TrackColor");
    // Mixer-only DSP display (kept separate from Device View junction meters).
    public static readonly Color MixerMeterTrack = Token("MixerMeter.TrackColor");
    public static readonly Color MixerMeterFill = Token("MixerMeter.FillColor");
    public static readonly Color MixerMeterHot = Token("MixerMeter.HotColor");
    public static readonly Color MixerMeterPeakHold = Token("MixerMeter.PeakHoldColor");
    public static readonly Color MixerMeterWarming = Token("MixerMeter.WarmingColor");
    public static readonly Color MixerMeterUnavailable = Token("MixerMeter.UnavailableColor");
    public static readonly Color JunctionLamp = Lighten(MeterLow, 0.25);

    // The Machined console (Console.* tokens): recessed gutters and wells, square-key faces, and the lamps they hold.
    public static readonly Color PanelHeader = Token("Console.PanelHeaderColor");
    public static readonly Color Gutter = Token("Console.GutterColor");
    public static readonly Color Well = Token("Console.WellColor");
    public static readonly Color Key = Token("Console.KeyColor");
    public static readonly Color Sheen = Token("Console.SheenColor");
    public static readonly Color Slot = Token("Console.SlotColor");
    public static readonly Color SelectedText = Token("Console.SelectedTextColor");
    public static readonly Color Mute = Token("Track.MuteColor");
    public static readonly Color Solo = Token("Track.SoloColor");
    public static readonly Color SoloInk = Token("Console.SoloInkColor");
    public static readonly Color Clip = Token("Console.ClipColor");
    public const double GlowAlpha = 0.28;
    public const double GlowReach = 4;
    public const double SheenAlpha = 0.035;
    public const double GutterDepth = 0.22;
    public const double UnlitAlpha = 0.13;

    /// <summary>Track colors (Track.Swatch1-8), muted so dense arrangements stay readable; status is never conveyed by these alone.</summary>
    public static readonly Color[] Tracks =
    [
        Token("Track.Swatch1Color"),
        Token("Track.Swatch2Color"),
        Token("Track.Swatch3Color"),
        Token("Track.Swatch4Color"),
        Token("Track.Swatch5Color"),
        Token("Track.Swatch6Color"),
        Token("Track.Swatch7Color"),
        Token("Track.Swatch8Color"),
    ];

    /// <summary>The color token <paramref name="key"/> as the application has it now.</summary>
    internal static Color Token(string key) => ThemeApplier.Token(key);

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
