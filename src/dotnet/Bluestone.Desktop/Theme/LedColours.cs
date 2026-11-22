using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Controls;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// Single LED colours the musician can choose (Settings ▸ Display): for now the MIDI LED, lit while notes are held at a
/// chain junction. Controls cache what they build from it and rebuild on <see cref="Changed"/>. UI thread only.
/// </summary>
public static class LedColours
{
    private static readonly IPen?[] MidiPens = new IPen?[LedLevels.Steps + 1];
    private static IImmutableSolidColorBrush? _midiCaptionBrush;

    /// <summary>The MIDI LED's default: <see cref="DisplaySettings.DefaultMidiColour"/>, the accent.</summary>
    public static Color DefaultMidi { get; } = Color.Parse(DisplaySettings.DefaultMidiColour);

    /// <summary>The colour of a lit MIDI LED and its caption.</summary>
    public static Color Midi { get; private set; } = DefaultMidi;

    /// <summary>Raised on the UI thread after <see cref="UseMidi"/> changes <see cref="Midi"/>.</summary>
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
}
