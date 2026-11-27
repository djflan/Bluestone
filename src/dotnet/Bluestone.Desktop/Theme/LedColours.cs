using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// Single LED colours the musician can choose (Settings ▸ Display): the MIDI LED, lit while notes are held at a chain
/// junction, and the peak marker a mixer Peak + RMS meter draws for a current peak inside its lit RMS body. Controls cache what they build from it and rebuild on <see cref="Changed"/>. UI thread only.
/// </summary>
public static class LedColours
{
    private static readonly IPen?[] MidiPens = new IPen?[LedLevels.Steps + 1];
    private static IImmutableSolidColorBrush? _midiCaptionBrush;

    /// <summary>The MIDI LED's default: Dark Sodalite's <see cref="ThemeDefinition.DefaultMidiColor"/>, the accent.</summary>
    public static Color DefaultMidi { get; } = Color.Parse(ThemeDefinition.DefaultMidiColor);

    /// <summary>The colour of a lit MIDI LED and its caption.</summary>
    public static Color Midi { get; private set; } = DefaultMidi;

    /// <summary>The peak marker's default: Dark Sodalite's <see cref="ThemeDefinition.DefaultPeakMarkerColor"/>.</summary>
    public static Color DefaultPeakMarker { get; } = Color.Parse(ThemeDefinition.DefaultPeakMarkerColor);

    /// <summary>The colour of the current-peak marker inside a Peak + RMS meter's lit RMS body.</summary>
    public static Color PeakMarker { get; private set; } = DefaultPeakMarker;

    /// <summary>Whether Peak + RMS meters draw the peak marker at all.</summary>
    public static bool ShowPeakMarker { get; private set; } = true;

    // One brush for the current marker color, so previewing colors never grows a shared cache.
    internal static IImmutableSolidColorBrush PeakMarkerBrush { get; private set; } = new ImmutableSolidColorBrush(DefaultPeakMarker);

    /// <summary>Raised on the UI thread after <see cref="UseMidi"/> or <see cref="UsePeakMarker"/> changes anything.</summary>
    public static event EventHandler? Changed;

    // The port's ink has only the quantized fade steps, shared by every junction and preview for the current MIDI color.
    internal static IPen MidiPen(int step)
    {
        if (MidiPens[step] is not { } pen)
        {
            var idle = Palette.Alpha(Palette.TextMuted, 0.75);
            var lit = Palette.Blend(Midi, Colors.White, 0.1);
            var ink = Palette.Blend(idle, lit, step / (double)LedLevels.Steps);
            pen = new ImmutablePen(new ImmutableSolidColorBrush(ink), 1.3, lineCap: PenLineCap.Round);
            MidiPens[step] = pen;
        }

        return pen;
    }

    internal static IImmutableSolidColorBrush MidiCaptionBrush =>
        _midiCaptionBrush ??= new ImmutableSolidColorBrush(Palette.Alpha(Midi, 0.85));

    /// <summary>Uses <paramref name="colour"/> ("#RRGGBB") for the MIDI LED; null or anything else restores the default. Raises nothing when it is the same.</summary>
    public static void UseMidi(string? colour)
    {
        var next = SignalGradient.IsColour(colour) ? Color.Parse(colour!) : DefaultMidi;
        if (next == Midi)
        {
            return;
        }

        Midi = next;
        Array.Clear(MidiPens);
        _midiCaptionBrush = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the peak marker or not, in <paramref name="colour"/> ("#RRGGBB"; null or anything else restores the default).
    /// Raises nothing when neither changes.
    /// </summary>
    public static void UsePeakMarker(bool show, string? colour)
    {
        var next = SignalGradient.IsColour(colour) ? Color.Parse(colour!) : DefaultPeakMarker;
        if (next == PeakMarker && show == ShowPeakMarker)
        {
            return;
        }

        if (next != PeakMarker)
        {
            PeakMarker = next;
            PeakMarkerBrush = new ImmutableSolidColorBrush(next);
        }

        ShowPeakMarker = show;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
