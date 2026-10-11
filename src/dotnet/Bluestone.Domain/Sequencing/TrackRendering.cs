using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>A value to set on a device parameter at a position, rendered from a device automation lane.</summary>
public readonly record struct ParameterChange(Tick Position, DeviceId Device, ParameterId Parameter, ControlValue Value);

/// <summary>What a track sends, and how much of its clip content and automation was left out on the way.</summary>
/// <param name="Events">Timeline-positioned events in canonical order.</param>
/// <param name="SuppressedEvents">Clip events left out because a lane controls the same target.</param>
/// <param name="DroppedLanes">Lanes left out because an earlier lane controls the same target once channels are overridden.</param>
public sealed record RenderedTrack(ImmutableArray<TrackEvent> Events, int SuppressedEvents, int DroppedLanes)
{
    /// <summary>
    /// Device parameter changes in position order. They are separate from <see cref="Events"/> on purpose:
    /// automation of a device parameter is delivered to the device, not routed through the devices before it
    /// and not encoded as MIDI (ADR 0024).
    /// </summary>
    public ImmutableArray<ParameterChange> ParameterChanges { get; init; } = [];
}

/// <summary>
/// Combines a track's clips and automation into the events it sends (ADR 0020), and its device automation
/// into parameter changes (ADR 0024). Playback and MIDI file export both use this, so they agree on what a
/// track plays.
/// </summary>
public static class TrackRendering
{
    /// <summary>
    /// The track's arranged events, with its non-empty lanes rendered (see <see cref="AutomationRenderer"/>).
    /// Automation wins: clip events setting a target a lane controls (including the LSB partner of a
    /// controller lane) are left out. Targets are compared on <paramref name="channelOverride"/> when
    /// given, as the route will send them. At the same tick and phase, automation follows clip events.
    /// </summary>
    public static RenderedTrack Render(Track track, Ppqn ppqn, MidiChannel? channelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        // In track order, so the first of two lanes that land on the same target wins.
        var targets = new HashSet<AutomationTarget>();
        var lanes = new List<AutomationLane>();
        var dropped = 0;
        var changes = RenderParameters(track, ppqn);
        foreach (var lane in track.Automation.Where(l => l.Target.IsMidi && !l.Points.IsEmpty))
        {
            if (targets.Add(Effective(lane.Target, channelOverride)))
            {
                lanes.Add(lane);
            }
            else
            {
                dropped++;
            }
        }

        if (lanes.Count == 0)
        {
            return new RenderedTrack(track.ArrangedEvents, 0, dropped) { ParameterChanges = changes };
        }

        var kept = new List<TrackEvent>(track.ArrangedEvents.Length);
        var suppressed = 0;
        foreach (var e in track.ArrangedEvents)
        {
            if (e is ChannelEvent channel && AutomationTarget.Of(channel) is { } target && targets.Contains(Effective(target, channelOverride)))
            {
                suppressed++;
            }
            else
            {
                kept.Add(e);
            }
        }

        // Each lane's samples are in order; OrderBy is stable, so earlier lanes go first at a shared tick.
        var interval = AutomationRenderer.DefaultInterval(ppqn);
        var rendered = lanes
            .SelectMany(lane => AutomationRenderer.Sample(lane, interval).Select(s => (TrackEvent)lane.Target.CreateEvent(s.Position, s.Value)))
            .OrderBy(e => e.Position)
            .ToList();

        // Both lists are in canonical order; merge them, clip events first at the same tick and phase.
        var events = ImmutableArray.CreateBuilder<TrackEvent>(kept.Count + rendered.Count);
        int i = 0, j = 0;
        while (i < kept.Count || j < rendered.Count)
        {
            events.Add(j == rendered.Count || (i < kept.Count && EventOrder.Compare(kept[i], rendered[j]) <= 0) ? kept[i++] : rendered[j++]);
        }

        return new RenderedTrack(events.MoveToImmutable(), suppressed, dropped) { ParameterChanges = changes };
    }

    /// <summary>The device automation lanes of <paramref name="track"/>, sampled the way MIDI lanes are, at full parameter resolution.</summary>
    private static ImmutableArray<ParameterChange> RenderParameters(Track track, Ppqn ppqn)
    {
        var interval = AutomationRenderer.DefaultInterval(ppqn);
        var changes = track.Automation
            .Where(l => l.Target.IsDevice && !l.Points.IsEmpty)
            .SelectMany(lane => AutomationRenderer.Sample(lane, interval).Select(s => new ParameterChange(s.Position, lane.Target.Device, lane.Target.DeviceParameter, s.Value)))
            .OrderBy(c => c.Position)
            .ToImmutableArray();
        return changes;
    }

    /// <summary>
    /// Whether <paramref name="e"/>, as a clip event on <paramref name="track"/>, would be left out because a
    /// non-empty lane controls its target, compared on <paramref name="channelOverride"/> when given.
    /// </summary>
    public static bool Replaces(Track track, ChannelEvent e, MidiChannel? channelOverride = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(e);
        return AutomationTarget.Of(e) is { } target
            && track.Automation.Any(l => !l.Points.IsEmpty && Effective(l.Target, channelOverride) == Effective(target, channelOverride));
    }

    private static AutomationTarget Effective(AutomationTarget target, MidiChannel? channelOverride) =>
        channelOverride is { } channel ? target.WithChannel(channel) : target;
}
