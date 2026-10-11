using Bluestone.Domain.Sequencing;

namespace Bluestone.Application.Editing;

/// <summary>Edits to a track's automation lanes, as undoable commands.</summary>
public static class AutomationCommands
{
    /// <summary>Adds <paramref name="lane"/>, unless the track already has a lane for its target.</summary>
    public static IProjectCommand AddLane(TrackId track, AutomationLane lane) =>
        ProjectCommands.EditTrack("Add Automation Lane", track, t => t.Automation.Any(l => l.Target == lane.Target) ? t : t.WithLane(lane));

    public static IProjectCommand RemoveLane(TrackId track, AutomationLaneId lane) =>
        ProjectCommands.EditTrack("Delete Automation Lane", track, t => t.RemoveLane(lane));

    /// <summary>Replaces a lane's points, e.g. after a point is added, moved, or deleted.</summary>
    public static IProjectCommand SetPoints(TrackId track, AutomationLaneId lane, string label, IReadOnlyCollection<AutomationPoint> points) =>
        ProjectCommands.EditTrack(label, track, t => t.FindLane(lane) is { } found && !found.Points.SequenceEqual(points) ? t.WithLane(found.WithPoints(points)) : t);
}
