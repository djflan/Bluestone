using Bluestone.Domain.Devices;
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

    /// <summary>Adds a project Master fader, pan, or Master-chain device lane, unless that parameter already has a lane.</summary>
    public static IProjectCommand AddMasterLane(AutomationLane lane) =>
        new ProjectCommand("Add Master Automation Lane", project =>
        {
            var targetsMasterDevice = lane.Target.IsDevice
                && project.FindDevice(lane.Target.Device) is { } found
                && found.Chain.Owner.Kind == ChainOwnerKind.Master;
            return (!lane.Target.IsMaster && !targetsMasterDevice)
                || project.MasterAutomation.Any(existing => existing.Target == lane.Target)
                    ? project
                    : project with { MasterAutomation = project.MasterAutomation.Add(lane) };
        });

    public static IProjectCommand RemoveMasterLane(AutomationLaneId lane) =>
        new ProjectCommand("Delete Master Automation Lane", project =>
            project with { MasterAutomation = project.MasterAutomation.RemoveAll(existing => existing.Id == lane) });

    /// <summary>Replaces a Master lane's points as one undoable edit.</summary>
    public static IProjectCommand SetMasterPoints(AutomationLaneId lane, string label, IReadOnlyCollection<AutomationPoint> points) =>
        new ProjectCommand(label, project =>
        {
            var found = project.MasterAutomation.FirstOrDefault(existing => existing.Id == lane);
            return found is null || found.Points.SequenceEqual(points)
                ? project
                : project with { MasterAutomation = project.MasterAutomation.Replace(found, found.WithPoints(points)) };
        });
}
