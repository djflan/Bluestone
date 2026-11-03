namespace Bluestone.Benchmarks;

/// <summary>The release timing targets of ADR 0043, in one place so every probe judges against the same numbers.</summary>
internal static class Targets
{
    /// <summary>MIDI: lateness of a message, at the 99.9th percentile and in the worst case.</summary>
    public const double MidiLatenessMs = 2.0;

    /// <summary>
    /// Live MIDI to audio: what Bluestone adds beyond the audio device. That is the time from the input's timestamp until
    /// it is heard, less the device's reported output latency and one device buffer period (the wait for the next
    /// callback, which the chosen buffer size imposes on any host), at the 99.9th percentile and in the worst case.
    /// </summary>
    public const double LiveAddedMs = 2.0;

    /// <summary>Live MIDI thru (input to output) added by Bluestone beyond the operating system's own MIDI hop.</summary>
    public const double ThruAddedMs = 2.0;
}
