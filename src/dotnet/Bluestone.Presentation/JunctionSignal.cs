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
    /// <summary>The junction's key: the chain's ID for its input, the device's ID for the point after it.</summary>
    public Guid Key { get; internal set; }

    /// <summary>Notes can reach this point (something before it may play or pass them).</summary>
    [ObservableProperty]
    public partial bool CanCarryNotes { get; internal set; }

    /// <summary>Audio can be present at this point; the level is shown only where it can.</summary>
    [ObservableProperty]
    public partial bool CanCarryAudio { get; internal set; }

    /// <summary>Notes are held here now: during playback by the compiled song, or by live input on the track.</summary>
    [ObservableProperty]
    public partial bool NotesHeld { get; private set; }

    /// <summary>The audio level here, falling to the floor when audio is off or the junction is not in the engine's graph.</summary>
    public MeterLevels Levels { get; } = new();

    internal void Update(bool notesHeld, MeterReading? level, long timestamp)
    {
        NotesHeld = notesHeld && CanCarryNotes;
        Levels.Update(CanCarryAudio ? level : null, timestamp);
    }
}
