namespace Bluestone.Midi.Files;

/// <summary>Stable diagnostic codes. See docs/midi-files.md for what each means and what Bluestone does.</summary>
public static class SmfDiagnosticCodes
{
    // Reading
    public const string LongHeader = "SMF001";
    public const string TrackCountMismatch = "SMF002";
    public const string TruncatedChunk = "SMF003";
    public const string TrailingBytes = "SMF004";
    public const string UnknownChunk = "SMF005";
    public const string MissingEndOfTrack = "SMF006";
    public const string DataAfterEndOfTrack = "SMF007";
    public const string MalformedEvent = "SMF008";
    public const string RunningStatusAfterSystemEvent = "SMF009";
    public const string SingleTrackFormatWithManyTracks = "SMF010";

    // Importing
    public const string SmpteTimingConverted = "SMF100";
    public const string UnpairedNoteOff = "SMF101";
    public const string UnterminatedNote = "SMF102";
    public const string ZeroLengthNote = "SMF103";
    public const string InvalidTempo = "SMF104";
    public const string InvalidTimeSignature = "SMF105";
    public const string MetronomeSettingsDropped = "SMF106";
    public const string SysExKeptRaw = "SMF107";
    public const string MultiSequenceFlattened = "SMF108";
    public const string ConductorEventOutsideFirstTrack = "SMF109";
    public const string PortAssignmentNotApplied = "SMF110";
    public const string TempoIgnoredForSmpte = "SMF111";
    public const string NameTruncated = "SMF112";

    // Exporting
    public const string MixerStateNotStored = "SMF200";
    public const string TextEncodedAsUtf8 = "SMF201";
    public const string OverlappingNotes = "SMF202";
    public const string AutomationReplacedEvents = "SMF203";

    /// <summary>Device parameter automation has no MIDI form, so it is not in the file (ADR 0024).</summary>
    public const string DeviceAutomationNotExported = "SMF204";
}
