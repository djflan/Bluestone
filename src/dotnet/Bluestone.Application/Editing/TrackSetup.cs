using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Application.Editing;

/// <summary>
/// The everyday defaults that make a track ready to make music without routing it by hand. A new track is a hybrid track
/// (notes and audio, ADR 0020 and 0021) whose audio goes to the master, or to its group, through an ordinary audio
/// connection (ADR 0023): explicit project state, shown in the routing inspector, undone with the edit that made it, and
/// never assumed by the audio graph.
/// </summary>
public static class TrackSetup
{
    /// <summary>
    /// Adds <paramref name="track"/> with an audio path, as one undo step. External MIDI is not set up: a track reaches
    /// hardware only when the musician chooses an output.
    /// </summary>
    public static IProjectCommand NewTrack(Track track, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(track);
        return RoutingGuard.Command("New Track", definitions, p => WithAudioPath(p with { Sequence = p.Sequence.WithTrack(track) }, track.Id));
    }

    /// <summary>Whether any of the track's audio is sent anywhere: from its output, or from a device in its chain.</summary>
    public static bool HasAudioPath(Project project, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(project);
        var output = SignalNode.Track(track);
        var chain = project.ChainOf(track);
        return project.Connections.Any(c => c.Kind == SignalKind.Audio
            && (c.Source == output || (c.Source.Kind == SignalNodeKind.Device && chain?.Find(c.Source.AsDevice()) is not null)));
    }

    /// <summary>Where a track's audio goes by default: its group track when it is in one, otherwise the master.</summary>
    public static SignalNode DefaultAudioDestination(Project project, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Sequence.FindTrack(track)?.Group is { } group && project.Sequence.FindTrack(group) is not null
            ? SignalNode.Track(group)
            : SignalNode.Master;
    }

    /// <summary>
    /// Gives <paramref name="track"/> an audio connection from its output to <see cref="DefaultAudioDestination"/> when none
    /// of its audio is sent anywhere; otherwise returns the project unchanged. Deliberate routing is never replaced. An
    /// instrument track, whose output carries no audio of its own, becomes a hybrid track (ADR 0021 only ever widens), so
    /// the connection stays valid whatever its chain holds later.
    /// </summary>
    public static Project WithAudioPath(Project project, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Sequence.FindTrack(track) is not { } found || HasAudioPath(project, track))
        {
            return project;
        }

        var connection = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track), DefaultAudioDestination(project, track));
        var sequence = found.Role == TrackRole.Instrument ? project.Sequence.WithTrack(found.WithRole(TrackRole.Hybrid)) : project.Sequence;
        return project with { Sequence = sequence, Connections = project.Connections.Add(connection) };
    }

    /// <summary>
    /// Sends each track's audio to <paramref name="destination"/> (the master, a mixer channel, or another track), or nowhere
    /// when it is null: its first audio connection from its output is pointed there, or one is added. Further audio
    /// connections and taps are left alone. One undo step; refused if it would make a routing error.
    /// </summary>
    public static IProjectCommand SetAudioDestination(IReadOnlyCollection<TrackId> tracks, SignalNode? destination, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return RoutingGuard.Command(tracks.Count == 1 ? "Change Audio Output" : $"Change Audio Output ({tracks.Count} Tracks)", definitions, p =>
        {
            var result = p;
            foreach (var track in tracks)
            {
                var output = SignalNode.Track(track);
                var current = result.Connections.FirstOrDefault(c => c.Kind == SignalKind.Audio && c.Source == output);
                if (destination is not { } target)
                {
                    result = current is null ? result : result with { Connections = result.Connections.Remove(current) };
                }
                else if (current is null)
                {
                    result = result with { Connections = result.Connections.Add(SignalConnection.Create(SignalKind.Audio, output, target)) };
                }
                else if (current.Destination != target)
                {
                    result = result with { Connections = result.Connections.Replace(current, current with { Destination = target }) };
                }
            }

            return result;
        });
    }
}
