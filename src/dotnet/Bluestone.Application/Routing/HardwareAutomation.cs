using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Playback;
using Bluestone.Profiles;

namespace Bluestone.Application.Routing;

/// <summary>
/// Translates automation of external instrument parameters into the MIDI their profiles map them to (ADR 0031):
/// CC (7 or 14 bits), RPN, NRPN, or SysEx, on the part's port and channel. Playback and MIDI file export both use
/// it, so they agree on what is sent.
/// </summary>
/// <remarks>
/// <para>
/// Thinning: lanes are sampled as other lanes are (every 1/32 of a quarter note on linear segments), each sample
/// is scaled to the parameter's range, and a value is sent only when it differs from the last one sent. A
/// parameter is also sent at most once per <see cref="MinimumInterval"/> of song time: a change that comes
/// sooner waits until the interval has passed, and a newer change replaces it while it waits, so the last value
/// of every ramp still arrives.
/// </para>
/// <para>
/// Like device automation (ADR 0024), it is sent whether the lane's track is muted or not. When two lanes control
/// the same parameter of the same part, the first in track order (then lane order) is sent and the other is reported.
/// </para>
/// </remarks>
public static class HardwareAutomation
{
    /// <summary>The shortest time between two changes of one parameter: at most 50 per second.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Ordering indexes of translated events start here, so at the same tick and phase they follow the track's own
    /// events (as other automation does). Each lane on a track gets <see cref="SequencesPerLane"/> indexes.
    /// </summary>
    public const int SequenceBase = 1 << 30;

    /// <summary>More than the most events one change becomes (an RPN or NRPN with 14-bit data and a null: six).</summary>
    public const int SequencesPerLane = 8;

    /// <summary>
    /// The mapping for <paramref name="target"/>, or why it cannot be translated: the instrument, its port, its
    /// profile, or the parameter is missing.
    /// </summary>
    public static (ParameterMapping? Mapping, string? Problem) Resolve(Project project, ProfileCatalog profiles, AutomationTarget target)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profiles);
        var instrument = target.IsInstrument ? project.FindInstrument(target.Instrument) : null;
        var profile = instrument?.Profile is { } reference ? profiles.Find(reference.ProfileId) : null;
        return Resolve(instrument, profile, target);
    }

    /// <summary>The changes <paramref name="lane"/> sends through <paramref name="mapping"/>: thinned values, each encoded for the lane's channel.</summary>
    public static ImmutableArray<(Tick Position, ImmutableArray<TrackEvent> Events)> Translate(AutomationLane lane, ParameterMapping mapping, TempoMap tempoMap, TimeSpan? minimumInterval = null)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(mapping);
        return [.. Values(lane, mapping, tempoMap, minimumInterval).Select(v => (v.Position, mapping.Encode(v.Value, lane.Target.Channel, v.Position)))];
    }

    /// <summary>The values <paramref name="lane"/> sends, scaled to <paramref name="mapping"/>'s range and thinned (see the remarks).</summary>
    public static IEnumerable<(Tick Position, int Value)> Values(AutomationLane lane, ParameterMapping mapping, TempoMap tempoMap, TimeSpan? minimumInterval = null)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(tempoMap);
        var spacing = minimumInterval ?? MinimumInterval;
        int? last = null;
        int? pending = null;
        var lastTime = TimeSpan.Zero;
        foreach (var (position, raw) in AutomationRenderer.Sample(lane, AutomationRenderer.DefaultInterval(tempoMap.Ppqn)))
        {
            var value = mapping.Scale(raw);
            if (pending is { } waiting && Due(tempoMap, lastTime + spacing) is var due && due < position)
            {
                yield return (due, waiting);
                last = waiting;
                lastTime = tempoMap.TimeAt(due);
                pending = null;
            }

            if (value == (pending ?? last))
            {
                continue;
            }

            var time = tempoMap.TimeAt(position);
            if (last is null || time - lastTime >= spacing)
            {
                yield return (position, value);
                last = value;
                lastTime = time;
                pending = null;
            }
            else
            {
                pending = value == last ? null : value;
            }
        }

        if (pending is { } final)
        {
            yield return (Due(tempoMap, lastTime + spacing), final);
        }
    }

    /// <summary>
    /// A translator for MIDI file export: an instrument parameter lane becomes its MIDI events on the part's
    /// channel, or null when it cannot be translated or another lane controls the same parameter first.
    /// </summary>
    public static Func<AutomationLane, IReadOnlyList<TrackEvent>?> ForExport(Project project, ProfileCatalog profiles)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profiles);
        var winners = new HashSet<AutomationLaneId>();
        var claimed = new HashSet<AutomationTarget>();
        foreach (var lane in project.Sequence.Tracks.SelectMany(t => t.Automation).Where(l => l.Target.IsInstrument && !l.Points.IsEmpty))
        {
            if (claimed.Add(lane.Target))
            {
                winners.Add(lane.Id);
            }
        }

        return lane => winners.Contains(lane.Id) && Resolve(project, profiles, lane.Target).Mapping is { } mapping
            ? [.. Translate(lane, mapping, project.Sequence.TempoMap).SelectMany(c => c.Events)]
            : null;
    }

    /// <summary>
    /// The plan parts for every instrument parameter lane in <paramref name="project"/>, in track then lane order.
    /// A part's endpoint gets the slot it already has, or a new one after <paramref name="slots"/>' existing slots.
    /// Lanes that cannot be sent are reported for their track.
    /// </summary>
    internal static ImmutableArray<ParameterPart> Plan(Project project, ImmutableArray<ResolvedInstrument> instruments, List<EndpointId> slots, ImmutableArray<PlanDiagnostic>.Builder diagnostics)
    {
        var parts = ImmutableArray.CreateBuilder<ParameterPart>();
        var claimed = new HashSet<AutomationTarget>();
        var tracks = project.Sequence.Tracks;
        for (var origin = 0; origin < tracks.Length; origin++)
        {
            var track = tracks[origin];
            for (var laneIndex = 0; laneIndex < track.Automation.Length; laneIndex++)
            {
                var lane = track.Automation[laneIndex];
                if (!lane.Target.IsInstrument || lane.Points.IsEmpty)
                {
                    continue;
                }

                var target = lane.Target;
                var resolved = instruments.FirstOrDefault(i => i.Instrument.Id == target.Instrument);
                var (mapping, problem) = Resolve(resolved?.Instrument, resolved?.Profile.Profile, target);
                var port = resolved?.FindPort(target.Port);
                if (problem is null && port is not { Endpoint.IsUsable: true })
                {
                    problem = $"port {target.Port} of {resolved!.Instrument.Name} has no available endpoint.";
                }

                if (problem is null && !claimed.Add(target))
                {
                    problem = "another lane already controls it on the same part.";
                }

                if (problem is not null || mapping is null)
                {
                    diagnostics.Add(new PlanDiagnostic(track.Id, string.Create(CultureInfo.InvariantCulture, $"Automation of {mapping?.Name ?? target.InstrumentParameter} on channel {target.Channel.Number} is not sent: {problem}")));
                    continue;
                }

                var endpoint = port!.Endpoint.Endpoint!.Id;
                var slot = slots.IndexOf(endpoint);
                if (slot < 0)
                {
                    slots.Add(endpoint);
                    slot = slots.Count - 1;
                }

                var sequence = SequenceBase + (laneIndex * SequencesPerLane);
                var sends = Translate(lane, mapping, project.Sequence.TempoMap).Select(c => new ParameterSend(c.Position, sequence, c.Events));
                parts.Add(new ParameterPart(slot, origin, [.. sends]) { Chased = mapping.Kind != ParameterMappingKind.Controller });
            }
        }

        return parts.ToImmutable();
    }

    private static (ParameterMapping? Mapping, string? Problem) Resolve(ExternalInstrument? instrument, DeviceProfile? profile, AutomationTarget target)
    {
        if (!target.IsInstrument)
        {
            return (null, "it is not an external instrument parameter.");
        }

        if (instrument is null)
        {
            return (null, "its external instrument is not in the project.");
        }

        if (instrument.FindPort(target.Port) is null)
        {
            return (null, $"{instrument.Name} has no port {target.Port}.");
        }

        if (instrument.Profile is not { } reference)
        {
            return (null, $"{instrument.Name} has no device profile to translate it.");
        }

        if (profile is null)
        {
            return (null, $"the profile {reference.DisplayName ?? reference.ProfileId} of {instrument.Name} is not installed.");
        }

        return profile.FindMapping(target.InstrumentParameter!) is { } mapping
            ? (mapping, null)
            : (null, $"the profile {profile.Name} does not define a parameter \"{target.InstrumentParameter}\".");
    }

    // The first tick at or after `time`.
    private static Tick Due(TempoMap tempoMap, TimeSpan time)
    {
        var tick = tempoMap.TickAt(time);
        return tempoMap.TimeAt(tick) < time ? new Tick(tick.Value + 1) : tick;
    }
}
