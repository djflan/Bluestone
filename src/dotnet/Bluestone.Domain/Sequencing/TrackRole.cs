namespace Bluestone.Domain.Sequencing;

/// <summary>
/// The workflow a track is for (ADR 0021). A role sets defaults and presentation and decides what a
/// track may be asked to hold. It never changes what a track is: every track is one <see cref="Track"/>
/// with clips, automation, and (separately) a device chain.
/// </summary>
public enum TrackRole
{
    /// <summary>Note-based content. May host instruments and MIDI effects, or route to others or to hardware.</summary>
    Instrument,

    /// <summary>Audio content. May host audio effects and routes to mixer channels and buses.</summary>
    Audio,

    /// <summary>Both note and audio content on one track.</summary>
    Hybrid,

    /// <summary>Hosts shared processing that other tracks route into. Holds no clips.</summary>
    Effect,

    /// <summary>Organizes other tracks and carries their combined processing and routing. Holds no clips.</summary>
    Group,
}
