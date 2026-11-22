using Avalonia.Media;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// Single LED colours the musician can choose (Settings ▸ Display): for now the MIDI LED, lit while notes are held at a
/// chain junction. Controls cache what they build from it and rebuild on <see cref="Changed"/>. UI thread only.
/// </summary>
public static class LedColours
{
    /// <summary>The MIDI LED's default: <see cref="DisplaySettings.DefaultMidiColour"/>, the accent.</summary>
    public static Color DefaultMidi { get; } = Color.Parse(DisplaySettings.DefaultMidiColour);

    /// <summary>The colour of a lit MIDI LED and its caption.</summary>
    public static Color Midi { get; private set; } = DefaultMidi;

    /// <summary>Raised on the UI thread after <see cref="UseMidi"/> changes <see cref="Midi"/>.</summary>
    public static event EventHandler? Changed;

    /// <summary>Uses <paramref name="colour"/> ("#RRGGBB") for the MIDI LED; null or anything else restores the default. Raises nothing when it is the same.</summary>
    public static void UseMidi(string? colour)
    {
        var next = SignalGradient.IsColour(colour) ? Color.Parse(colour!) : DefaultMidi;
        if (next == Midi)
        {
            return;
        }

        Midi = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
