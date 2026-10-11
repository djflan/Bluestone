namespace Bluestone.Domain.Sequencing;

/// <summary>
/// The order in which simultaneous events are dispatched. Events at the same tick are ordered by
/// phase, then by their position in the track (or by track order when merging tracks).
/// See docs/adr/0003-deterministic-event-ordering.md.
/// </summary>
public enum EventPhase
{
    /// <summary>Notes ending at this tick release before anything else changes.</summary>
    NoteOff = 0,

    /// <summary>Non-sounding meta information such as text or markers.</summary>
    Meta = 1,

    /// <summary>System exclusive and raw escaped messages, e.g. device initialization.</summary>
    SystemExclusive = 2,

    /// <summary>Bank select (CC 0 and 32), which must precede a program change to take effect.</summary>
    BankSelect = 3,

    ProgramChange = 4,

    /// <summary>Other controllers, RPN/NRPN, pitch bend, pressure, and channel mode messages.</summary>
    Control = 5,

    /// <summary>Notes starting at this tick sound last, after the state they depend on is set.</summary>
    NoteOn = 6,
}
