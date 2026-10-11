using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Application.Editing;

/// <summary>
/// Group tracks (ADR 0030): putting tracks in a group routes them into it, taking them out puts their own
/// outputs back, and folding hides a group's members in the arrangement. Membership and routing change in
/// one step, and an edit that would add a routing error is refused with <see cref="CommandRefusedException"/>.
/// </summary>
/// <remarks>
/// A track that joins a group has its outgoing connections (from its output, not taps on its devices) parked
/// in <see cref="Project.ParkedConnections"/> and replaced by one connection into the group per kind of signal
/// it sent (events and audio), or per kind its output carries when it sent nothing. An events connection keeps
/// the channel mapping of the track's only events connection, so a forced channel still applies. Leaving
/// removes the connections into the group and restores the parked ones that still work.
/// </remarks>
public static class GroupCommands
{
    /// <summary>Puts a track in a group track and routes it there, or with null takes it out and restores its own outputs.</summary>
    public static IProjectCommand SetGroup(TrackId track, TrackId? group, DeviceDefinitionLookup definitions) =>
        SetGroup([track], group, definitions);

    /// <summary>Puts several tracks in a group track (or takes them out with null) in one step.</summary>
    public static IProjectCommand SetGroup(IReadOnlyCollection<TrackId> tracks, TrackId? group, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(definitions);
        return RoutingGuard.Command(group is null ? "Remove From Group" : "Add To Group", definitions, p => tracks.Aggregate(p, (current, track) => Move(current, track, group, definitions)));
    }

    /// <summary>
    /// Makes a new group track named <paramref name="name"/> with ID <paramref name="group"/>, just above the first
    /// of <paramref name="members"/>, and puts them in it. The group sits in the members' group when they share one.
    /// For each kind of signal, when the members' own outputs all went to one destination, the group is connected
    /// there, so the members still reach it.
    /// </summary>
    public static IProjectCommand CreateGroup(TrackId group, string name, IReadOnlyCollection<TrackId> members, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(definitions);
        return RoutingGuard.Command("Group Tracks", definitions, p =>
        {
            var sequence = p.Sequence;
            var chosen = sequence.Tracks.Where(t => members.Contains(t.Id)).ToList();
            if (chosen.Count == 0)
            {
                return p;
            }

            var parent = chosen.Select(t => t.Group).Distinct().Count() == 1 ? chosen[0].Group : null;
            var created = new Track(group, name, [], role: TrackRole.Group);
            var result = p with { Sequence = sequence.InsertTrack(sequence.Tracks.IndexOf(chosen[0]), created) };
            foreach (var member in chosen)
            {
                result = Move(result, member.Id, group, definitions);
            }

            var sources = chosen.Select(t => SignalNode.Track(t.Id)).ToHashSet();
            foreach (var kind in new[] { SignalKind.Events, SignalKind.Audio })
            {
                var destinations = result.ParkedConnections.Where(c => c.Kind == kind && sources.Contains(c.Source)).Select(c => c.Destination).Distinct().ToList();
                if (destinations.Count == 1)
                {
                    result = AddIfItWorks(result, SignalConnection.Create(kind, SignalNode.Track(group), destinations[0]), definitions);
                }
            }

            // Last, so the group's own outputs are parked like any member's, and come back if it leaves.
            return Move(result, group, parent, definitions);
        });
    }

    /// <summary>Folds a group track (its members are hidden under it in the arrangement) or unfolds it. Other tracks do not fold.</summary>
    public static IProjectCommand SetFolded(TrackId group, bool folded) =>
        ProjectCommands.EditTrack(folded ? "Fold Group" : "Unfold Group", group, t => t.Role != TrackRole.Group || t.IsFolded == folded ? t : t.WithFolded(folded));

    /// <summary>
    /// Moves every track directly in <paramref name="group"/> to the group around it, or out of groups, so the
    /// group can be removed without leaving its members unrouted.
    /// </summary>
    internal static Project Dissolve(Project project, TrackId group, DeviceDefinitionLookup definitions)
    {
        var parent = project.Sequence.FindTrack(group)?.Group;
        foreach (var member in GroupTracks.Members(project.Sequence, group).Select(t => t.Id).ToList())
        {
            project = Move(project, member, parent, definitions);
        }

        return project;
    }

    private static Project Move(Project project, TrackId track, TrackId? group, DeviceDefinitionLookup definitions)
    {
        if (project.Sequence.FindTrack(track) is not { } found || found.Group == group)
        {
            return project;
        }

        if (group is { } id)
        {
            if (project.Sequence.FindTrack(id) is not { Role: TrackRole.Group } target)
            {
                throw new CommandRefusedException("Tracks can only be put in a group track.");
            }

            if (id == track || GroupTracks.IsInside(project.Sequence, target, track))
            {
                throw new CommandRefusedException($"\"{target.Name}\" is inside \"{found.Name}\", so \"{found.Name}\" cannot go into it.");
            }
        }

        project = Leave(project, found, definitions);
        return group is { } joining ? Join(project, track, joining, definitions) : project;
    }

    private static Project Leave(Project project, Track track, DeviceDefinitionLookup definitions)
    {
        if (track.Group is not { } group)
        {
            return project;
        }

        var node = SignalNode.Track(track.Id);
        var parked = project.ParkedConnections.Where(c => c.Source == node).ToList();
        var result = project with
        {
            Sequence = project.Sequence.WithTrack(track.WithGroup(null)),
            Connections = project.Connections.RemoveAll(c => c.Source == node && c.Destination == SignalNode.Track(group)),
            ParkedConnections = parked.Count == 0 ? project.ParkedConnections : project.ParkedConnections.RemoveAll(c => c.Source == node),
        };

        // What no longer works (a removed instrument, a loop through the track's new place) stays out.
        foreach (var connection in parked)
        {
            result = AddIfItWorks(result, connection, definitions);
        }

        return result;
    }

    private static Project Join(Project project, TrackId track, TrackId group, DeviceDefinitionLookup definitions)
    {
        var node = SignalNode.Track(track);
        var outgoing = project.Connections.Where(c => c.Source == node).ToList();
        var presence = project.ChainOf(track) is { } chain
            ? SignalFlow.PresenceAt(project, chain, null, definitions)
            : SignalFlow.StartOf(project, ChainOwner.ForTrack(track));
        var kinds = outgoing.Select(c => c.Kind).Distinct().Where(presence.Carries).ToList();
        if (kinds.Count == 0)
        {
            kinds = [.. new[] { SignalKind.Events, SignalKind.Audio }.Where(presence.Carries)];
        }

        var events = outgoing.Where(c => c.Kind == SignalKind.Events).ToList();
        var routes = kinds.Select(kind => SignalConnection.Create(kind, node, SignalNode.Track(group)) with
        {
            Mapping = kind == SignalKind.Events && events.Count == 1 ? events[0].Mapping : ChannelMapping.Preserve,
        });
        var member = project.Sequence.FindTrack(track)!;
        return project with
        {
            Sequence = project.Sequence.WithTrack(member.WithGroup(group)),
            Connections = project.Connections.RemoveAll(c => c.Source == node).AddRange(routes),
            ParkedConnections = project.ParkedConnections.AddRange(outgoing),
        };
    }

    private static Project AddIfItWorks(Project project, SignalConnection connection, DeviceDefinitionLookup definitions)
    {
        try
        {
            return RoutingGuard.Check(project, project with { Connections = project.Connections.Add(connection) }, definitions);
        }
        catch (CommandRefusedException)
        {
            return project;
        }
    }
}
