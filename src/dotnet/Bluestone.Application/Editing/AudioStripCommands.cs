using Bluestone.Domain.Mixing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Application.Editing;

/// <summary>Undoable edits to track-owned and Master audio strips.</summary>
public static class AudioStripCommands
{
    /// <summary>Sets a track strip's fader in dB; consecutive edits to the same fader merge into one undo step.</summary>
    public static IProjectCommand SetTrackGain(TrackId track, double decibels, string? mergeKey = null) =>
        EditTrack("Change Track Audio Gain", track, value => EditTrackStrip(value, strip => strip with { GainDecibels = decibels }),
            mergeKey ?? $"audio-strip:{track}:gain");

    /// <summary>Sets a track strip's pan; consecutive edits to the same control merge into one undo step.</summary>
    public static IProjectCommand SetTrackPan(TrackId track, double pan, string? mergeKey = null) =>
        EditTrack("Change Track Audio Pan", track, value => EditTrackStrip(value, strip => strip with { Pan = pan }),
            mergeKey ?? $"audio-strip:{track}:pan");

    /// <summary>Mutes or unmutes the audio strip, independently of the track's event/content mute.</summary>
    public static IProjectCommand SetTrackMuted(TrackId track, bool muted) =>
        EditTrack(muted ? "Mute Track Audio" : "Unmute Track Audio", track,
            value => EditTrackStrip(value, strip => strip.IsMuted == muted ? strip : strip with { IsMuted = muted }), null);

    /// <summary>Solos or unsolos the audio strip, independently of the track's event/content solo.</summary>
    public static IProjectCommand SetTrackSoloed(TrackId track, bool soloed) =>
        EditTrack(soloed ? "Solo Track Audio" : "Unsolo Track Audio", track,
            value => EditTrackStrip(value, strip => strip.IsSoloed == soloed ? strip : strip with { IsSoloed = soloed }), null);

    /// <summary>Sets the Master fader in dB; consecutive edits merge into one undo step.</summary>
    public static IProjectCommand SetMasterGain(double decibels, string? mergeKey = null) =>
        EditMaster("Change Master Audio Gain", strip => strip with { GainDecibels = decibels }, mergeKey ?? "master-audio-strip:gain");

    /// <summary>Sets the Master pan; consecutive edits merge into one undo step.</summary>
    public static IProjectCommand SetMasterPan(double pan, string? mergeKey = null) =>
        EditMaster("Change Master Audio Pan", strip => strip with { Pan = pan }, mergeKey ?? "master-audio-strip:pan");

    public static IProjectCommand SetMasterMuted(bool muted) =>
        EditMaster(muted ? "Mute Master Audio" : "Unmute Master Audio",
            strip => strip.IsMuted == muted ? strip : strip with { IsMuted = muted }, null);

    /// <summary>Stores Master solo state. It is separate from all track content/audio solo state.</summary>
    public static IProjectCommand SetMasterSoloed(bool soloed) =>
        EditMaster(soloed ? "Solo Master Audio" : "Unsolo Master Audio",
            strip => strip.IsSoloed == soloed ? strip : strip with { IsSoloed = soloed }, null);

    private static Track EditTrackStrip(Track track, Func<AudioStrip, AudioStrip> edit)
    {
        var strip = track.AudioStrip ?? throw new CommandRefusedException($"\"{track.Name}\" has no audio strip to edit.");
        var changed = edit(strip);
        return Equals(strip, changed) ? track : track.WithAudioStrip(changed);
    }

    private static ProjectCommand EditTrack(string label, TrackId id, Func<Track, Track> edit, string? mergeKey) =>
        new(label, project =>
        {
            if (project.Sequence.FindTrack(id) is not { } track)
            {
                return project;
            }

            var changed = edit(track);
            return ReferenceEquals(track, changed) ? project : project with { Sequence = project.Sequence.WithTrack(changed) };
        })
        {
            MergeKey = mergeKey,
        };

    private static ProjectCommand EditMaster(string label, Func<AudioStrip, AudioStrip> edit, string? mergeKey) =>
        new(label, project =>
        {
            var changed = edit(project.MasterStrip);
            return Equals(project.MasterStrip, changed) ? project : project with { MasterStrip = changed };
        })
        {
            MergeKey = mergeKey,
        };
}
