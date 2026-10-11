namespace Bluestone.Playback;

public sealed record PlaybackOptions
{
    public static readonly PlaybackOptions Default = new();

    /// <summary>
    /// How far ahead of due time messages are handed to endpoints that support scheduled delivery.
    /// Larger values tolerate scheduling hiccups; smaller values make edits audible sooner.
    /// </summary>
    public TimeSpan LookAhead { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The longest the engine sleeps while playing, so <see cref="PlaybackEngine.Position"/> keeps moving
    /// smoothly through sparse passages (for example when only a soloed track is sounding).
    /// </summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Dispatches later than this count as late in <see cref="TimingStatistics"/>.</summary>
    public TimeSpan LateThreshold { get; init; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Note-ons this far past due (after a stall, for example) are skipped rather than sounded out of
    /// time. Controllers, program changes, SysEx, and releases are always sent so device state stays right.
    /// </summary>
    public TimeSpan MaxNoteLateness { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Capacity of the sounding-note table. Note-ons beyond it are dropped and counted.</summary>
    public int MaxActiveNotes { get; init; } = 2048;
}
