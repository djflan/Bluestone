using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Application.Editing;

/// <summary>
/// Undoable edits of clip loops (see <see cref="ClipLoop"/>). Edits that would make a clip play more than
/// <see cref="NoteClip.MaxArrangedEvents"/> events are refused with an explanation.
/// </summary>
public static class ClipLoopCommands
{
    /// <summary>
    /// The loop a clip gets when looping is first turned on: what the clip plays now, so its sound does not change until
    /// it is made longer. An audio clip's loop stops at the end of its file.
    /// </summary>
    public static ClipLoop DefaultLoop(Clip clip, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(sequence);
        var start = clip.ContentOffset.Value;
        var length = clip.Length.Value;
        if (clip is AudioClip audio && audio.Source.SampleRate > 0 && clip.Origin >= 0)
        {
            var tempo = sequence.TempoMap;
            var origin = tempo.TimeAt(new Tick(clip.Origin));
            var fileEnd = tempo.TickAt(origin + TimeSpan.FromSeconds(audio.Source.Frames / (double)audio.Source.SampleRate)).Value - clip.Origin;
            if (fileEnd > start)
            {
                length = Math.Min(length, fileEnd - start);
            }
        }

        return new ClipLoop(new Tick(start), new TickSpan(Math.Max(1, length)));
    }

    /// <summary>Sets a clip's loop exactly, on or off. Null removes it.</summary>
    public static IProjectCommand SetLoop(TrackId track, ClipId clip, ClipLoop? loop, string label = "Change Clip Loop") =>
        ProjectCommands.EditTrack(label, track, t =>
            t.FindClip(clip) is { } found && !Equals(found.Loop, loop) ? t.WithClip(found with { Loop = loop }) : t);

    /// <summary>
    /// Turns looping on or off for every clip in <paramref name="clips"/>, in one step. A clip turned on for the first
    /// time gets <see cref="DefaultLoop"/>; turning off keeps the loop for the next time.
    /// </summary>
    public static IProjectCommand SetEnabled(IReadOnlyCollection<(TrackId Track, ClipId Clip)> clips, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var label = (enabled, clips.Count == 1) switch
        {
            (true, true) => "Loop Clip",
            (true, false) => "Loop Clips",
            (false, true) => "Stop Looping Clip",
            (false, false) => "Stop Looping Clips",
        };
        return new ProjectCommand(label, p =>
        {
            var sequence = p.Sequence;
            foreach (var group in clips.GroupBy(c => c.Track))
            {
                if (sequence.FindTrack(group.Key) is not { } track)
                {
                    continue;
                }

                var ids = group.Select(c => c.Clip).ToHashSet();
                var changed = track.Clips.Select(c => ids.Contains(c.Id) ? Enabled(c, enabled, sequence) : c).ToList();
                if (!changed.SequenceEqual(track.Clips))
                {
                    sequence = sequence.WithTrack(track.WithClips(changed));
                }
            }

            return ReferenceEquals(sequence, p.Sequence) ? p : p with { Sequence = sequence };
        });
    }

    /// <summary>
    /// Extends or shortens a clip with its loop repeating (the arranger's loop handle): turns looping on with
    /// <see cref="DefaultLoop"/> if needed, then moves the end to <paramref name="until"/>, stopping at the next clip.
    /// </summary>
    public static IProjectCommand ExtendLooping(TrackId track, ClipId clip, Tick until) =>
        new ProjectCommand("Extend Clip Loop", p =>
        {
            if (p.Sequence.FindTrack(track) is not { } t || t.FindClip(clip) is not { } found)
            {
                return p;
            }

            var (_, after) = t.Neighbours(clip);
            var end = Math.Max(found.Start.Value + 1, after is { } next ? Math.Min(until.Value, next.Value) : until.Value);
            var looped = Enabled(found, true, p.Sequence) with { Length = new TickSpan(end - found.Start.Value) };
            return looped == found ? p : p with { Sequence = p.Sequence.WithTrack(t.WithClip(looped)) };
        });

    /// <summary>
    /// Sets the loop's length, keeping its start (the arranger's first repeat marker). Looping is turned on with the
    /// default loop first if needed.
    /// </summary>
    public static IProjectCommand SetLength(TrackId track, ClipId clip, TickSpan length) =>
        new ProjectCommand("Change Clip Loop Length", p =>
        {
            if (length.Value < 1 || p.Sequence.FindTrack(track) is not { } t || t.FindClip(clip) is not { } found)
            {
                return p;
            }

            var loop = Enabled(found, true, p.Sequence).Loop!;
            var changed = found with { Loop = new ClipLoop(loop.Start, length) };
            return changed == found ? p : p with { Sequence = p.Sequence.WithTrack(t.WithClip(changed)) };
        });

    /// <summary>
    /// Turns <paramref name="clips"/>' note clips into ordinary content that plays the same: repeats written out, no
    /// loop (see <see cref="NoteClip.Consolidated"/>). Audio clips are consolidated by writing a file
    /// (<c>AudioConsolidation</c>), not here.
    /// </summary>
    public static IProjectCommand ConsolidateNotes(TrackId track, IReadOnlyCollection<ClipId> clips) =>
        ProjectCommands.EditTrack(clips.Count == 1 ? "Consolidate Clip" : "Consolidate Clips", track, t =>
        {
            var changed = t.Clips.Select(c => clips.Contains(c.Id) && c is NoteClip { IsLooping: true } notes ? notes.Consolidated() : c).ToList();
            return changed.SequenceEqual(t.Clips) ? t : t.WithClips(changed);
        });

    private static Clip Enabled(Clip clip, bool enabled, Sequence sequence) => clip.Loop switch
    {
        { } loop => clip with { Loop = loop.WithEnabled(enabled) },
        null when enabled => clip with { Loop = DefaultLoop(clip, sequence) },
        _ => clip,
    };
}
