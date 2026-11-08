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
/// One way live input on a track is heard (ADR 0035, ADR 0037): the devices it passes through and where it ends. Live input
/// fans out like playback does (<see cref="FindAll"/>): through the whole chain, so every plugin instrument in it plays and
/// note effects after an instrument work on the notes it passes on, and along every event connection, from the chain's
/// output or from any point inside it, to other tracks and racks and to every usable external instrument port. On the
/// way, every device that is not bypassed and consumes events is a stage, in chain order (built-in devices and plugin MIDI
/// effects alike), and every connection's forced channel is a stage where the connection is. A built-in stage runs as a
/// copy per route; a plugin MIDI effect is one instance, so only the first route through it is played live.
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

    /// <summary>The first of <see cref="FindAll"/>: the nearest target, or <see cref="LiveTargetKind.None"/>.</summary>
    public static LiveRoutePlan Find(Project project, TrackId track, DeviceCatalog devices, Func<ExternalInstrumentId, string?, EndpointDescriptor?> endpoints) =>
        FindAll(project, track, devices, endpoints)[0];

    /// <summary>
    /// Every way live input on <paramref name="track"/> is heard, nearest first (breadth-first along connections, chain
    /// order within a chain); one <see cref="LiveTargetKind.None"/> plan when nothing playable is reached. A plugin
    /// instrument is played once, by the first way that reaches it.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="track">The track played live.</param>
    /// <param name="devices">The device catalog (what each device consumes and produces).</param>
    /// <param name="endpoints">The endpoint an external instrument's port reaches now, or null when it is not usable.</param>
    public static ImmutableArray<LiveRoutePlan> FindAll(Project project, TrackId track, DeviceCatalog devices, Func<ExternalInstrumentId, string?, EndpointDescriptor?> endpoints)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(endpoints);
        var plans = ImmutableArray.CreateBuilder<LiveRoutePlan>();
        var played = new HashSet<DeviceId>();
        var queue = new Queue<(SignalNode Node, ImmutableArray<LiveRouteStage> Stages)>();
        var start = SignalNode.Track(track);
        var seen = new HashSet<SignalNode> { start };
        queue.Enqueue((start, []));
        while (queue.Count > 0)
        {
            var (node, stages) = queue.Dequeue();
            var chain = node.Kind == SignalNodeKind.Track ? project.ChainOf(node.AsTrack()) : project.FindChain(node.AsRack());

            // The stages in front of each junction of the chain: [0] its input, [i + 1] after device i. Null once notes have ended.
            var devicesInChain = chain?.Devices ?? [];
            var at = new ImmutableArray<LiveRouteStage>?[devicesInChain.Length + 1];
            at[0] = stages;
            ImmutableArray<LiveRouteStage>? current = stages;
            for (var i = 0; i < devicesInChain.Length; i++)
            {
                var device = devicesInChain[i];
                if (current is { } before && !device.IsBypassed && devices.Find(device.Definition.Id) is { } definition
                    && definition.Consumes.HasFlag(SignalKinds.Events) && definition.Handles != EventClass.None)
                {
                    if (definition.Produces.HasFlag(SignalKinds.Events))
                    {
                        current = before.Add(LiveRouteStage.Run(device));
                    }
                    else
                    {
                        // An instrument plays the notes; only a plugin instrument makes sound. It passes them on unless its
                        // definition ends the note path (ADR 0037).
                        if (definition.Produces.HasFlag(SignalKinds.Audio) && device.Definition.Id.Value.StartsWith(PluginDeviceHost.DefinitionPrefix, StringComparison.Ordinal) && played.Add(device.Id))
                        {
                            plans.Add(new LiveRoutePlan(track, before, LiveTargetKind.Software, null, device.Id));
                        }

                        if ((definition.PassesThrough & EventClass.Notes) == EventClass.None)
                        {
                            current = null;
                        }
                    }
                }

                at[i + 1] = current;
            }

            foreach (var connection in project.Connections.Where(c => c.Kind == SignalKind.Events))
            {
                var from = connection.Source == node ? at[^1]
                    : chain is not null && connection.Source.Kind == SignalNodeKind.ChainInput && connection.Source.AsChain() == chain.Id ? at[0]
                    : chain is not null && connection.Source.Kind == SignalNodeKind.Device && chain.IndexOf(connection.Source.AsDevice()) is var index and >= 0 ? at[index + 1]
                    : null;
                if (from is not { } prefix)
                {
                    continue;
                }

                var next = connection.Mapping.Force is { } forced ? prefix.Add(LiveRouteStage.Force(forced)) : prefix;
                var destination = connection.Destination;
                if (destination.Kind == SignalNodeKind.ExternalInstrument)
                {
                    if (endpoints(destination.AsInstrument(), destination.Port) is { } endpoint)
                    {
                        var plan = new LiveRoutePlan(track, next, LiveTargetKind.External, endpoint, null);
                        if (!plans.Any(p => p.IsSameAs(plan)))
                        {
                            plans.Add(plan);
                        }
                    }
                }
                else if (destination.Kind is SignalNodeKind.Track or SignalNodeKind.Rack && seen.Add(destination))
                {
                    queue.Enqueue((destination, next));
                }
            }
        }

        return plans.Count > 0 ? [.. OnePerPluginStage(plans)] : [new LiveRoutePlan(track, [], LiveTargetKind.None, null, null)];
    }

    // A plugin MIDI effect is one running instance: only the first route through it runs it live. Later routes that would
    // share it are left out (a built-in effect is copied per route instead, so it never limits fan-out).
    private static IEnumerable<LiveRoutePlan> OnePerPluginStage(IEnumerable<LiveRoutePlan> plans)
    {
        var claimed = new HashSet<DeviceId>();
        foreach (var plan in plans)
        {
            var plugins = plan.Stages
                .Where(s => s.Device is { } d && d.Definition.Id.Value.StartsWith(PluginDeviceHost.DefinitionPrefix, StringComparison.Ordinal))
                .Select(s => s.Device!.Id)
                .ToList();
            if (plugins.Any(claimed.Contains))
            {
                continue;
            }

            claimed.UnionWith(plugins);
            yield return plan;
        }
    }
}
