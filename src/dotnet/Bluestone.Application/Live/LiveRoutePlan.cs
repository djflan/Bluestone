using System.Collections.Immutable;
using Bluestone.Application.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Signal;

namespace Bluestone.Application.Live;

/// <summary>Where a live route ends.</summary>
public enum LiveTargetKind
{
    /// <summary>Nothing playable is reached; live notes are not heard.</summary>
    None,

    /// <summary>An external instrument's port: MIDI out.</summary>
    External,

    /// <summary>A plugin instrument in a chain: heard through the audio engine.</summary>
    Software,
}

/// <summary>One step of a live route: a device to run (built-in or plugin MIDI effect), or a connection's forced channel.</summary>
public sealed record LiveRouteStage(DeviceInstance? Device, MidiChannel? Channel)
{
    public static LiveRouteStage Run(DeviceInstance device) => new(device, null);

    public static LiveRouteStage Force(MidiChannel channel) => new(null, channel);
}

/// <summary>
/// What live input on a track passes through and where it ends (ADR 0035): breadth-first from the track along event
/// connections, the nearest playable instrument wins (decision 21 of the epic). On the way, every device that is not
/// bypassed and consumes events is a stage, in chain order (built-in devices and plugin MIDI effects alike), and
/// every connection's forced channel is a stage where the connection is. A plugin instrument in a chain ends the route
/// there (it takes the notes, as at plan time); otherwise the first usable external instrument port does.
/// </summary>
public sealed record LiveRoutePlan(TrackId Track, ImmutableArray<LiveRouteStage> Stages, LiveTargetKind Target, EndpointDescriptor? Endpoint, DeviceId? Instrument)
{
    /// <summary>The same stages (devices with the same settings) to the same target.</summary>
    public bool IsSameAs(LiveRoutePlan? other) =>
        other is not null
        && Track == other.Track
        && Target == other.Target
        && Endpoint?.Id == other.Endpoint?.Id
        && Instrument == other.Instrument
        && Stages.SequenceEqual(other.Stages);

    /// <summary>The route for <paramref name="track"/> in <paramref name="project"/>; <see cref="LiveTargetKind.None"/> when nothing playable is reached.</summary>
    /// <param name="project">The project.</param>
    /// <param name="track">The track played live.</param>
    /// <param name="devices">The device catalog (what each device consumes and produces).</param>
    /// <param name="endpoints">The endpoint an external instrument's port reaches now, or null when it is not usable.</param>
    public static LiveRoutePlan Find(Project project, TrackId track, DeviceCatalog devices, Func<ExternalInstrumentId, string?, EndpointDescriptor?> endpoints)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(endpoints);
        var queue = new Queue<(SignalNode Node, ImmutableArray<LiveRouteStage> Stages)>();
        var start = SignalNode.Track(track);
        var seen = new HashSet<SignalNode> { start };
        queue.Enqueue((start, []));
        while (queue.Count > 0)
        {
            var (node, stages) = queue.Dequeue();
            var chain = node.Kind == SignalNodeKind.Track ? project.ChainOf(node.AsTrack()) : project.FindChain(node.AsRack());
            foreach (var device in chain?.Devices ?? [])
            {
                if (device.IsBypassed || devices.Find(device.Definition.Id) is not { } definition
                    || !definition.Consumes.HasFlag(SignalKinds.Events) || definition.Handles == EventClass.None)
                {
                    continue;
                }

                if (definition.Produces.HasFlag(SignalKinds.Events))
                {
                    stages = stages.Add(LiveRouteStage.Run(device));
                    continue;
                }

                // An instrument takes the notes. Only a plugin instrument makes sound; anything else ends the route silent.
                var software = definition.Produces.HasFlag(SignalKinds.Audio) && device.Definition.Id.Value.StartsWith(PluginDeviceHost.DefinitionPrefix, StringComparison.Ordinal);
                return new LiveRoutePlan(track, stages, software ? LiveTargetKind.Software : LiveTargetKind.None, null, software ? device.Id : null);
            }

            foreach (var connection in project.ConnectionsFrom(node).Where(c => c.Kind == SignalKind.Events))
            {
                var next = connection.Mapping.Force is { } forced ? stages.Add(LiveRouteStage.Force(forced)) : stages;
                var destination = connection.Destination;
                if (destination.Kind == SignalNodeKind.ExternalInstrument)
                {
                    if (endpoints(destination.AsInstrument(), destination.Port) is { } endpoint)
                    {
                        return new LiveRoutePlan(track, next, LiveTargetKind.External, endpoint, null);
                    }
                }
                else if (destination.Kind is SignalNodeKind.Track or SignalNodeKind.Rack && seen.Add(destination))
                {
                    queue.Enqueue((destination, next));
                }
            }
        }

        return new LiveRoutePlan(track, [], LiveTargetKind.None, null, null);
    }
}
