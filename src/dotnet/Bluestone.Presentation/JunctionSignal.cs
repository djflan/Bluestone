using Bluestone.Audio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluestone.Presentation;

/// <summary>
/// One junction of the chain a Device View shows (ADR 0037): the point before the first device, or after a device. It
/// carries the junction's signal indicators, as Bitwig's device panel shows them between devices: a note lamp that is lit
/// while notes are held there, and a small stereo level for its audio. Updated in place on the UI thread by
/// <see cref="MainViewModel.OnFrame"/>.
/// </summary>
public sealed partial class JunctionSignal : ObservableObject
{
    /// <summary>
    /// The junction's key: the chain's ID for its input (the track's, for a track with no devices yet), the device's ID for
    /// the point after it. A junction shown for another point starts its level from nothing, not from the old one's hold.
    /// </summary>
    public Guid Key
    {
        get;
        internal set
        {
            if (field != value)
            {
                field = value;
                Levels.Reset();
            }
        }
    }

    /// <summary>Where it is, in words: "Into the chain", or "After Polysynth".</summary>
    [ObservableProperty]
    public partial string Label { get; internal set; } = string.Empty;

    /// <summary>Notes can reach this point (something before it may play or pass them).</summary>
    [ObservableProperty]
    public partial bool CanCarryNotes { get; internal set; }

    /// <summary>Audio can be present at this point; otherwise the level is disabled and dark grey.</summary>
    [ObservableProperty]
    public partial bool CanCarryAudio { get; internal set; }

    /// <summary>Notes are held here now: during playback by the compiled song, or by live input on the track.</summary>
    [ObservableProperty]
    public partial bool NotesHeld { get; private set; }

    /// <summary>Where this point is also sent by connections ("Strings, MU2000 B"), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRouted))]
    public partial string Routes { get; internal set; } = string.Empty;

    /// <summary>A connection takes signal from this point.</summary>
    public bool IsRouted => Routes.Length > 0;

    /// <summary>The Browser's next insert goes into the gap here (chosen with the junction's +).</summary>
    [ObservableProperty]
    public partial bool IsInsertionPoint { get; internal set; }

    /// <summary>The audio level here, falling to the floor when audio is off or the junction is not in the engine's graph.</summary>
    public MeterLevels Levels { get; } = new();

    internal void Update(bool notesHeld, MeterReading? level, long timestamp)
    {
        NotesHeld = notesHeld && CanCarryNotes;
        Levels.Update(CanCarryAudio ? level : null, timestamp);
    }
}
