using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Application.Editing;

/// <summary>The editing operations Bluestone offers, as undoable commands.</summary>
public static class ProjectCommands
{
    public static IProjectCommand RenameProject(string name) =>
        new ProjectCommand("Rename Project", p => p.Name == name ? p : p with { Name = name });

    public static IProjectCommand AddTrack(Track track) =>
        new ProjectCommand("Add Track", p => p with { Sequence = p.Sequence.WithTrack(track) });

    /// <summary>
    /// Removes a track with its chain and every connection from or to it (see <see cref="Project.WithoutTrack"/>).
    /// A group's members first move to the group around it, or out of groups, with their own outputs back (ADR 0030).
    /// </summary>
    public static IProjectCommand RemoveTrack(TrackId track) =>
        new ProjectCommand("Delete Track", p => p.Sequence.FindTrack(track) is null ? p : GroupCommands.Dissolve(p, track, BuiltInDevices.Find).WithoutTrack(track));

    public static IProjectCommand RenameTrack(TrackId track, string name) =>
        EditTrack("Rename Track", track, t => t.Name == name ? t : t.WithName(name));

    public static IProjectCommand SetMuted(TrackId track, bool muted) =>
        EditTrack(muted ? "Mute Track" : "Unmute Track", track, t => t.IsMuted == muted ? t : t.WithMuted(muted));

    public static IProjectCommand SetSoloed(TrackId track, bool soloed) =>
        EditTrack(soloed ? "Solo Track" : "Unsolo Track", track, t => t.IsSoloed == soloed ? t : t.WithSoloed(soloed));

    public static IProjectCommand AddEvent(TrackId track, ClipId clip, TrackEvent trackEvent) =>
        EditEvents(track, clip, trackEvent is NoteEvent ? "Add Note" : "Add Event", [], [trackEvent]);

    public static IProjectCommand RemoveEvent(TrackId track, EventId id) => RemoveEvents(track, [id]);

    public static IProjectCommand ReplaceEvent(TrackId track, ClipId clip, TrackEvent trackEvent) =>
        EditEvents(track, clip, "Edit Event", [], [trackEvent]);

    /// <summary>
    /// Removes <paramref name="remove"/> and adds <paramref name="add"/> (at timeline positions) in a single
    /// step, for an editor working on <paramref name="clip"/>. An event in both is replaced; removing finds
    /// events in whichever clip holds them, and ignores events not on the track.
    /// </summary>
    /// <remarks>
    /// An edited event (one replacing an event with its ID) stays in its clip, unless it played and is
    /// moved out of that clip. Any other added event goes to the note clip where it starts; one that starts
    /// between clips goes to <paramref name="clip"/> when that clip borders the same gap, or else to a new
    /// clip in its gap (the first new clip takes <paramref name="clip"/>'s ID if the track has no such
    /// clip yet). Clips grow, to bar lines, to show what they receive, but never into a neighbouring clip
    /// or before tick 0; a note reaching past that is kept whole but plays only to the clip's end. An
    /// edit that does not move an event or change its length (velocity, channel) never grows a clip, nor
    /// does editing hidden content, so a trimmed clip stays trimmed.
    /// </remarks>
    public static IProjectCommand EditEvents(TrackId track, ClipId clip, string label, IEnumerable<EventId> remove, IEnumerable<TrackEvent> add)
    {
        var removed = remove.ToHashSet();
        var added = add.ToList();
        removed.UnionWith(added.Select(e => e.Id));
        return EditTrack(label, track, (t, meter) =>
        {
            // Where each edited event lives now, found in one pass over the track.
            var replacing = added.Select(e => e.Id).ToHashSet();
            var homes = new Dictionary<EventId, (NoteClip Clip, TrackEvent Event)>();
            foreach (var c in t.Clips.OfType<NoteClip>())
            {
                foreach (var e in c.Content.Items)
                {
                    if (replacing.Contains(e.Id))
                    {
                        homes[e.Id] = (c, e);
                    }
                }
            }

            var pinned = new Dictionary<EventId, ClipId>();
            var steady = new HashSet<EventId>();
            foreach (var e in added)
            {
                if (homes.TryGetValue(e.Id, out var home))
                {
                    var shown = home.Clip.Shows(home.Event);
                    if (!shown || home.Clip.Contains(e.Position))
                    {
                        pinned.Add(e.Id, home.Clip.Id);
                    }

                    var start = home.Event.Position.Value + home.Clip.Origin;
                    if (!shown || (start == e.Position.Value && start + (home.Event.EndPosition - home.Event.Position).Value == e.EndPosition.Value))
                    {
                        steady.Add(e.Id);
                    }
                }
            }

            var without = Without(t, removed);
            return added.Count == 0 && ReferenceEquals(without, t) ? t : Route(without, clip, added, meter, pinned, steady);
        });
    }

    /// <summary>Replaces events (matched by ID) with edited versions, e.g. the result of an <see cref="EventEdits"/> operation.</summary>
    public static IProjectCommand ReplaceEvents(TrackId track, ClipId clip, string label, IReadOnlyCollection<TrackEvent> events) =>
        events.Count == 0 ? new ProjectCommand(label, p => p) : EditEvents(track, clip, label, [], events);

    public static IProjectCommand AddEvents(TrackId track, ClipId clip, string label, IReadOnlyCollection<TrackEvent> events) =>
        EditEvents(track, clip, label, [], events);

    /// <summary>Removes events from whichever of the track's clips hold them.</summary>
    public static IProjectCommand RemoveEvents(TrackId track, IReadOnlyCollection<EventId> events, string? label = null) =>
        EditTrack(label ?? (events.Count == 1 ? "Delete Event" : "Delete Events"), track, (t, _) => Without(t, events.ToHashSet()));

    /// <summary>
    /// Adds a recorded take. With <paramref name="replaceRange"/>, the events that play starting inside that
    /// range are removed first, except meta events (replace recording); otherwise the take is merged
    /// (overdub). The take's events go into clips as <see cref="EditEvents"/> places them, starting from the
    /// clip where the take starts, so nothing already on the track is lost.
    /// </summary>
    public static IProjectCommand Record(TrackId track, IReadOnlyCollection<TrackEvent> take, TickRange? replaceRange) =>
        EditTrack("Record", track, (t, meter) =>
        {
            var cleared = t;
            if (replaceRange is { } range)
            {
                var replaced = t.Clips.OfType<NoteClip>()
                    .SelectMany(c => c.Arrange())
                    .Where(e => e.Position >= range.Start && e.Position < range.End && e is not MetaEvent)
                    .Select(e => e.Id)
                    .ToHashSet();
                cleared = Without(t, replaced);
            }

            if (take.Count == 0)
            {
                return cleared;
            }

            var start = cleared.ClipAt(take.Min(e => e.Position))?.Id ?? ClipId.New();
            return Route(cleared, start, [.. take], meter, [], []);
        });

    /// <summary>
    /// Sets a track's output as the inspector shows it (see <see cref="TrackOutputs.Write"/>). Refused when it would
    /// add a routing error, such as sending events from a track whose role carries none.
    /// </summary>
    public static IProjectCommand SetTrackOutput(TrackId track, TrackOutput output, DeviceDefinitionLookup? definitions = null) =>
        RoutingGuard.Command("Change Routing", definitions ?? BuiltInDevices.Find, p => Equals(TrackOutputs.Read(p, track), output) ? p : TrackOutputs.Write(p, track, output));

    public static IProjectCommand SetLoop(TickRange? loop) =>
        new ProjectCommand(loop is null ? "Clear Loop" : "Set Loop", p => Equals(p.Loop, loop) ? p : p with { Loop = loop });

    /// <summary>Sets the tempo in effect from <paramref name="position"/> onward.</summary>
    public static IProjectCommand SetTempo(Tick position, Tempo tempo) =>
        new ProjectCommand("Change Tempo", p => p.Sequence.TempoMap.TempoAt(position) == tempo && p.Sequence.TempoMap.Changes.Any(c => c.Position == position)
            ? p
            : p with { Sequence = p.Sequence.WithTempoMap(p.Sequence.TempoMap.With(new TempoChange(position, tempo))) });

    /// <summary>Sets the time signature from <paramref name="position"/> (a bar line) onward.</summary>
    /// <exception cref="ArgumentException">A beat of this signature is not a whole number of ticks at the project's resolution.</exception>
    public static IProjectCommand SetTimeSignature(Tick position, TimeSignature signature) =>
        new ProjectCommand("Change Time Signature", p =>
        {
            var meter = p.Sequence.MeterMap;
            if (meter.Changes.Any(c => c.Position == position && c.Signature == signature))
            {
                return p;
            }

            var changes = meter.Changes.Where(c => c.Position != position).Append(new MeterChange(position, signature));
            return p with { Sequence = p.Sequence.WithMeterMap(new MeterMap(meter.Ppqn, changes)) };
        });

    /// <summary>Applies several commands as one undoable step.</summary>
    public static IProjectCommand Batch(string label, IEnumerable<IProjectCommand> commands)
    {
        var list = commands.ToList();
        return new ProjectCommand(label, p => list.Aggregate(p, (current, command) => command.Apply(current)));
    }

    /// <summary>Removes tracks, their chains, and their connections in one step.</summary>
    public static IProjectCommand RemoveTracks(IReadOnlyCollection<TrackId> tracks) =>
        Batch(tracks.Count == 1 ? "Delete Track" : "Delete Tracks", tracks.Select(RemoveTrack));

    /// <summary>
    /// Copies each track directly below the original, with fresh track, clip, event, and lane IDs. Its chain
    /// is copied with fresh device IDs, its automation of its own devices follows the copies, and its outgoing
    /// and parked connections are copied. Connections into it are not: the copy is a new destination nobody chose.
    /// </summary>
    public static IProjectCommand DuplicateTracks(IReadOnlyCollection<TrackId> tracks) =>
        new ProjectCommand(tracks.Count == 1 ? "Duplicate Track" : "Duplicate Tracks", p =>
        {
            var result = p;
            foreach (var id in tracks)
            {
                if (result.Sequence.FindTrack(id) is not { } track)
                {
                    continue;
                }

                var name = track.Name.Length + 5 <= Track.MaxNameLength ? track.Name + " copy" : track.Name;
                var copyId = TrackId.New();
                var devices = new Dictionary<DeviceId, DeviceId>();
                if (result.ChainOf(id) is { } chain)
                {
                    var copies = chain.Devices.Select(d => d.Duplicate()).ToList();
                    for (var i = 0; i < copies.Count; i++)
                    {
                        devices[chain.Devices[i].Id] = copies[i].Id;
                    }

                    result = result.WithChain(DeviceChain.Create(ChainOwner.ForTrack(copyId), chain.Name) with { Devices = [.. copies] });
                }

                var copy = new Track(
                    copyId,
                    name,
                    track.Clips.Select(c => c.CopyTo(c.Start)),
                    track.IsMuted,
                    track.IsSoloed,
                    track.Automation.Select(l => new AutomationLane(
                        AutomationLaneId.New(),
                        !l.Target.IsMidi && devices.TryGetValue(l.Target.Device, out var device) ? AutomationTarget.ForDevice(device, l.Target.DeviceParameter) : l.Target,
                        l.Points)),
                    track.Role,
                    track.Group,
                    track.IsFolded);
                var index = result.Sequence.Tracks.IndexOf(track) + 1;
                result = result with { Sequence = result.Sequence.InsertTrack(index, copy) };
                var outgoing = result.Connections
                    .Where(c => c.Source == SignalNode.Track(id) || (c.Source.Kind == SignalNodeKind.Device && devices.ContainsKey(c.Source.AsDevice())))
                    .Select(c => c with
                    {
                        Id = ConnectionId.New(),
                        Source = c.Source.Kind == SignalNodeKind.Device ? SignalNode.Device(devices[c.Source.AsDevice()]) : SignalNode.Track(copyId),
                    })
                    .ToList();
                var parked = result.ParkedConnections
                    .Where(c => c.Source == SignalNode.Track(id))
                    .Select(c => c with { Id = ConnectionId.New(), Source = SignalNode.Track(copyId) })
                    .ToList();
                result = result with { Connections = result.Connections.AddRange(outgoing), ParkedConnections = result.ParkedConnections.AddRange(parked) };
            }

            return result;
        });

    /// <summary>Mutes or unmutes several tracks in one step.</summary>
    public static IProjectCommand SetMuted(IReadOnlyCollection<TrackId> tracks, bool muted) =>
        Batch(muted ? "Mute Tracks" : "Unmute Tracks", tracks.Select(t => SetMuted(t, muted)));

    public static IProjectCommand SetSoloed(IReadOnlyCollection<TrackId> tracks, bool soloed) =>
        Batch(soloed ? "Solo Tracks" : "Unsolo Tracks", tracks.Select(t => SetSoloed(t, soloed)));

    /// <summary>A command that edits one track, or does nothing if the track is gone or the edit changes nothing.</summary>
    internal static ProjectCommand EditTrack(string label, TrackId id, Func<Track, Track> edit) =>
        EditTrack(label, id, (track, _) => edit(track));

    private static ProjectCommand EditTrack(string label, TrackId id, Func<Track, MeterMap, Track> edit) =>
        new(label, p =>
        {
            if (p.Sequence.FindTrack(id) is not { } track)
            {
                return p;
            }

            var edited = edit(track, p.Sequence.MeterMap);
            return ReferenceEquals(edited, track) ? p : p with { Sequence = p.Sequence.WithTrack(edited) };
        });

    /// <summary>The track with <paramref name="ids"/> removed from every clip holding them, or the same track if none does.</summary>
    private static Track Without(Track track, HashSet<EventId> ids)
    {
        var changed = false;
        var clips = track.Clips.Select(c =>
        {
            if (c is NoteClip notes && notes.Content.Items.Any(e => ids.Contains(e.Id)))
            {
                changed = true;
                return notes.EditTimeline(ids, []);
            }

            return c;
        }).ToList();
        return changed ? track.WithClips(clips) : track;
    }

    /// <summary>
    /// Adds events (at timeline positions) to clips as <see cref="EditEvents"/> describes: those in
    /// <paramref name="pinned"/> stay in their clip, and those in <paramref name="steady"/> do not make
    /// their clip grow.
    /// </summary>
    private static Track Route(Track track, ClipId preferred, List<TrackEvent> added, MeterMap meter, Dictionary<EventId, ClipId> pinned, HashSet<EventId> steady)
    {
        if (added.Count == 0)
        {
            return track;
        }

        // Events go in note clips; a clip of another kind (audio) cannot take them, and nothing may overlap it.
        if (added.Any(e => !pinned.ContainsKey(e.Id) && track.ClipAt(e.Position) is { } c && c is not NoteClip))
        {
            throw new CommandRefusedException("Notes and other events cannot go inside an audio clip. Move the audio clip, or put the events on another track.");
        }

        // Existing clips take their events and grow first.
        var target = track.FindClip(preferred) as NoteClip;
        var into = new Dictionary<ClipId, List<TrackEvent>>();
        var loose = new List<TrackEvent>();
        foreach (var e in added)
        {
            if (pinned.TryGetValue(e.Id, out var home))
            {
                Append(into, home, e);
            }
            else if (track.ClipAt(e.Position) is NoteClip at)
            {
                Append(into, at.Id, e);
            }
            else if (target is not null && track.GapAt(e.Position) is var (from, until) && (from == target.End || until == target.Start))
            {
                Append(into, target.Id, e);
            }
            else
            {
                loose.Add(e);
            }
        }

        var result = Grow(track, into, meter, steady);

        // The rest started in free space: some may now be inside a clip that grew; others get a clip per gap.
        var grown = new Dictionary<ClipId, List<TrackEvent>>();
        var gaps = new SortedDictionary<Tick, List<TrackEvent>>();
        foreach (var e in loose)
        {
            if (result.ClipAt(e.Position) is NoteClip at)
            {
                Append(grown, at.Id, e);
            }
            else
            {
                var gap = result.GapAt(e.Position).From;
                if (!gaps.TryGetValue(gap, out var list))
                {
                    gaps.Add(gap, list = []);
                }

                list.Add(e);
            }
        }

        result = Grow(result, grown, meter, steady);
        foreach (var events in gaps.Values)
        {
            var id = result.FindClip(preferred) is null ? preferred : ClipId.New();
            var clip = NoteClip.Enclosing(events, meter, id)!;
            var (gapStart, gapEnd) = result.GapAt(events.Min(e => e.Position));
            result = result.WithClip(clip.WithBounds(Tick.Max(clip.Start, gapStart), gapEnd is { } end ? Tick.Min(clip.End, end) : clip.End));
        }

        return result;

        static void Append(Dictionary<ClipId, List<TrackEvent>> groups, ClipId id, TrackEvent e)
        {
            if (!groups.TryGetValue(id, out var list))
            {
                groups.Add(id, list = []);
            }

            list.Add(e);
        }
    }

    private static Track Grow(Track track, Dictionary<ClipId, List<TrackEvent>> groups, MeterMap meter, HashSet<EventId> steady)
    {
        foreach (var (id, events) in groups)
        {
            var clip = (NoteClip)track.FindClip(id)!;
            track = track.WithClip(GrowToShow(clip.EditTimeline([], events), [.. events.Where(e => !steady.Contains(e.Id))], track, meter));
        }

        return track;
    }

    /// <summary>Grows <paramref name="clip"/> to bar lines around <paramref name="added"/>, within its gap on <paramref name="track"/>.</summary>
    private static NoteClip GrowToShow(NoteClip clip, List<TrackEvent> added, Track track, MeterMap meter)
    {
        if (added.Count == 0)
        {
            return clip;
        }

        var first = added.Min(e => e.Position.Value);
        var last = added.Max(e => e.Position.Value);
        var reach = Math.Max(added.Max(e => e.EndPosition.Value), last + 1);
        if (first >= clip.Start.Value && reach <= clip.End.Value)
        {
            return clip;
        }

        var (barStart, barEnd) = NoteClip.EnclosingBounds(first, last, reach, meter);
        var (before, after) = track.Neighbours(clip.Id);
        var until = Tick.Max(clip.End, barEnd);
        return clip.WithBounds(Tick.Max(before, Tick.Min(clip.Start, barStart)), after is { } next ? Tick.Min(until, next) : until);
    }
}
