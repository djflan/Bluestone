using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Routing;

public enum RoutingSeverity
{
    /// <summary>Worth showing, but the project plays and nothing is lost. Never blocks loading or saving.</summary>
    Warning,

    /// <summary>The connection cannot work. An edit that causes one is refused.</summary>
    Error,
}

public enum RoutingIssueCode
{
    UnknownSource,
    UnknownDestination,
    InvalidSource,
    InvalidDestination,
    UnknownPort,

    /// <summary>The destination cannot take this kind of signal (events into a mixer channel, audio into a MIDI port).</summary>
    KindMismatch,

    /// <summary>The source cannot carry this kind of signal at that point, for example events after an instrument has consumed them all.</summary>
    NothingToCarry,

    /// <summary>A path of connections leads back to where it started. Feedback is not allowed (ADR 0023).</summary>
    Cycle,

    DuplicateConnection,
    InvalidMapping,
    VoiceNotApplicable,

    /// <summary>A lane's device is not in the project. The lane is kept.</summary>
    AutomationDeviceMissing,

    /// <summary>A lane's parameter is not one its device defines. The lane is kept.</summary>
    AutomationParameterUnknown,

    /// <summary>A lane's external instrument, or its port, is not in the project. The lane is kept.</summary>
    AutomationInstrumentMissing,
}

/// <summary>One finding about a project's routing, in words a musician can read.</summary>
public sealed record RoutingIssue(RoutingSeverity Severity, RoutingIssueCode Code, string Message, ImmutableArray<ConnectionId> Connections = default)
{
    public ImmutableArray<ConnectionId> Connections { get; init; } = Connections.IsDefault ? [] : Connections;
}

/// <summary>
/// Checks a project's connections: that each end exists and fits its role, that the signal can be present
/// where it is taken from, that the destination accepts it, and that nothing feeds back on itself
/// (ADR 0023).
/// </summary>
/// <remarks>
/// It reports; it never changes the project. Loading a project does not fail on warnings, and a device
/// whose implementation is missing never turns a connection into an error: the project must keep what
/// the musician built until the plugin comes back.
/// </remarks>
public static class SignalRoutingValidator
{
    public static ImmutableArray<RoutingIssue> Validate(Project project, DeviceDefinitionLookup definitions)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definitions);
        var issues = ImmutableArray.CreateBuilder<RoutingIssue>();
        var seen = new HashSet<ConnectionId>();
        var shapes = new HashSet<(SignalKind, SignalNode, SignalNode)>();
        foreach (var connection in project.Connections)
        {
            var sound = true;
            if (!seen.Add(connection.Id))
            {
                issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.DuplicateConnection, $"Connection {connection.Id} is listed twice.", connection));
                continue;
            }

            if (!shapes.Add((connection.Kind, connection.Source, connection.Destination)))
            {
                issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.DuplicateConnection, $"{Describe(project, connection.Source)} is already connected to {Describe(project, connection.Destination)}.", connection));
                continue;
            }

            sound &= CheckSource(project, connection, issues);
            sound &= CheckDestination(project, connection, issues);
            if (sound)
            {
                CheckSignal(project, connection, definitions, issues);
            }
        }

        FindCycles(project, issues);
        CheckAutomation(project, definitions, issues);
        return issues.ToImmutable();
    }

    /// <summary>The errors among <paramref name="issues"/>.</summary>
    public static IEnumerable<RoutingIssue> Errors(IEnumerable<RoutingIssue> issues) => issues.Where(i => i.Severity == RoutingSeverity.Error);

    private static bool CheckSource(Project project, SignalConnection connection, ImmutableArray<RoutingIssue>.Builder issues)
    {
        var source = connection.Source;
        switch (source.Kind)
        {
            case SignalNodeKind.Track when project.Sequence.FindTrack(source.AsTrack()) is not null:
            case SignalNodeKind.Rack when project.FindChain(source.AsRack()) is { Owner.Kind: ChainOwnerKind.Rack }:
            case SignalNodeKind.Device when project.FindDevice(source.AsDevice()) is not null:
                return true;
            case SignalNodeKind.Track or SignalNodeKind.Rack or SignalNodeKind.Device:
                issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.UnknownSource, $"The source {Describe(project, source)} is not in the project.", connection));
                return false;
            default:
                issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.InvalidSource, $"{source.Kind} cannot be the source of a connection.", connection));
                return false;
        }
    }

    private static bool CheckDestination(Project project, SignalConnection connection, ImmutableArray<RoutingIssue>.Builder issues)
    {
        var destination = connection.Destination;
        var ok = true;
        switch (destination.Kind)
        {
            case SignalNodeKind.Track when project.Sequence.FindTrack(destination.AsTrack()) is null:
            case SignalNodeKind.Rack when project.FindChain(destination.AsRack()) is not { Owner.Kind: ChainOwnerKind.Rack }:
            case SignalNodeKind.MixerChannel when project.Mixer.Find(destination.AsMixerChannel()) is null:
                issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.UnknownDestination, $"The destination {Describe(project, destination)} is not in the project.", connection));
                return false;
            case SignalNodeKind.ExternalInstrument:
                if (project.FindInstrument(destination.AsInstrument()) is not { } instrument)
                {
                    issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.UnknownDestination, "The destination instrument is not in the project.", connection));
                    return false;
                }

                if (instrument.FindPort(destination.Port) is null)
                {
                    issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.UnknownPort, $"{instrument.Name} has no port \"{destination.Port}\".", connection));
                    return false;
                }

                break;
            case SignalNodeKind.Device:
                issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.InvalidDestination, "A device output cannot be the destination of a connection; connect to a track or rack.", connection));
                return false;
        }

        var accepts = connection.Kind == SignalKind.Events
            ? destination.Kind is SignalNodeKind.Track or SignalNodeKind.Rack or SignalNodeKind.ExternalInstrument
            : destination.Kind is SignalNodeKind.Track or SignalNodeKind.Rack or SignalNodeKind.MixerChannel or SignalNodeKind.Master;
        if (!accepts)
        {
            issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.KindMismatch, $"{Describe(project, destination)} does not take {connection.Kind.ToString().ToLowerInvariant()}.", connection));
            ok = false;
        }

        if (!connection.Mapping.IsConsistent)
        {
            issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.InvalidMapping, "A connection cannot both force a channel and remap channels.", connection));
            ok = false;
        }

        if (connection.Kind == SignalKind.Audio && (!connection.Mapping.IsPreserving || connection.Voice is not null))
        {
            issues.Add(Issue(RoutingSeverity.Warning, RoutingIssueCode.InvalidMapping, "Channel mapping and voices apply to events, not audio; they are ignored here.", connection));
        }
        else if (connection.Voice is not null && destination.Kind != SignalNodeKind.ExternalInstrument)
        {
            issues.Add(Issue(RoutingSeverity.Warning, RoutingIssueCode.VoiceNotApplicable, "A voice can be selected only on an external instrument; it is ignored here.", connection));
        }

        return ok;
    }

    private static void CheckSignal(Project project, SignalConnection connection, DeviceDefinitionLookup definitions, ImmutableArray<RoutingIssue>.Builder issues)
    {
        var source = connection.Source;
        SignalPresence presence;
        switch (source.Kind)
        {
            case SignalNodeKind.Track:
                presence = project.ChainOf(source.AsTrack()) is { } chain
                    ? SignalFlow.PresenceAt(project, chain, null, definitions)
                    : SignalFlow.StartOf(project, ChainOwner.ForTrack(source.AsTrack()));
                break;
            case SignalNodeKind.Rack:
                presence = SignalFlow.PresenceAt(project, project.FindChain(source.AsRack())!, null, definitions);
                break;
            default:
                var (owner, device) = project.FindDevice(source.AsDevice())!.Value;
                presence = SignalFlow.PresenceAt(project, owner, device.Id, definitions);
                break;
        }

        if (!presence.Carries(connection.Kind))
        {
            issues.Add(Issue(RoutingSeverity.Error, RoutingIssueCode.NothingToCarry, $"{Describe(project, source)} has no {connection.Kind.ToString().ToLowerInvariant()} to send at that point.", connection));
        }
    }

    // A cycle is a path of connections (and mixer outputs) that returns to a node. Nodes are the things
    // that own a chain (tracks and racks) and mixer channels; a tap belongs to its chain's owner, because
    // everything before the tap has already run when it fires.
    private static void FindCycles(Project project, ImmutableArray<RoutingIssue>.Builder issues)
    {
        var edges = new Dictionary<(char, Guid), List<((char, Guid) To, ConnectionId? Via)>>();
        foreach (var connection in project.Connections)
        {
            if (Owner(project, connection.Source) is not { } from || Owner(project, connection.Destination) is not { } to)
            {
                continue;
            }

            Add(edges, from, (to, connection.Id));
        }

        foreach (var channel in project.Mixer.Channels)
        {
            if (channel.Output is { } output)
            {
                Add(edges, ('M', channel.Id.Value), (('M', output.Value), null));
            }
        }

        var state = new Dictionary<(char, Guid), int>();
        var path = new List<((char, Guid) Node, ConnectionId? Via)>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in edges.Keys.ToList())
        {
            if (!state.ContainsKey(start))
            {
                Visit(start, null);
            }
        }

        void Visit((char, Guid) node, ConnectionId? via)
        {
            state[node] = 1;
            path.Add((node, via));
            if (edges.TryGetValue(node, out var next))
            {
                foreach (var (to, edgeVia) in next)
                {
                    if (state.GetValueOrDefault(to) == 1)
                    {
                        Report(to, edgeVia);
                    }
                    else if (!state.ContainsKey(to))
                    {
                        Visit(to, edgeVia);
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }

        void Report((char, Guid) back, ConnectionId? closing)
        {
            var start = path.FindIndex(p => p.Node == back);
            var members = path.Skip(start).ToList();
            var key = string.Join("|", members.Select(m => m.Node).Order().Select(n => $"{n.Item1}{n.Item2}"));
            if (!reported.Add(key))
            {
                return;
            }

            var names = members.Select(m => NameOf(project, m.Node)).Append(NameOf(project, back));
            var connections = members.Skip(1).Select(m => m.Via).Append(closing).OfType<ConnectionId>().ToImmutableArray();
            issues.Add(new RoutingIssue(RoutingSeverity.Error, RoutingIssueCode.Cycle, $"These connections feed back on themselves: {string.Join(" → ", names)}. Feedback is not allowed.", connections));
        }

        static void Add(Dictionary<(char, Guid), List<((char, Guid), ConnectionId?)>> graph, (char, Guid) from, ((char, Guid), ConnectionId?) edge)
        {
            if (!graph.TryGetValue(from, out var list))
            {
                graph[from] = list = [];
            }

            list.Add(edge);
        }
    }

    private static (char, Guid)? Owner(Project project, SignalNode node) => node.Kind switch
    {
        SignalNodeKind.Track => ('T', node.Id),
        SignalNodeKind.Rack => ('R', node.Id),
        SignalNodeKind.MixerChannel => ('M', node.Id),
        SignalNodeKind.Device when project.FindDevice(node.AsDevice()) is { } found =>
            found.Chain.Owner.Kind == ChainOwnerKind.Track ? ('T', found.Chain.Owner.Track.Value) : ('R', found.Chain.Id.Value),
        _ => null,
    };

    private static string NameOf(Project project, (char Kind, Guid Id) node) => node.Kind switch
    {
        'T' => project.Sequence.FindTrack(new TrackId(node.Id))?.Name ?? "a track",
        'R' => project.FindChain(new DeviceChainId(node.Id)) is { Name.Length: > 0 } rack ? rack.Name : "a rack",
        _ => project.Mixer.Find(new MixerChannelId(node.Id))?.Name ?? "a mixer channel",
    };

    private static void CheckAutomation(Project project, DeviceDefinitionLookup definitions, ImmutableArray<RoutingIssue>.Builder issues)
    {
        foreach (var track in project.Sequence.Tracks)
        {
            foreach (var lane in track.Automation.Where(l => l.Target.IsDevice))
            {
                if (project.FindDevice(lane.Target.Device) is not { } found)
                {
                    issues.Add(new RoutingIssue(RoutingSeverity.Warning, RoutingIssueCode.AutomationDeviceMissing, $"An automation lane on {track.Name} targets a device that is not in the project. The lane is kept."));
                }
                else if (definitions(found.Device.Definition.Id) is { } definition && definition.FindParameter(lane.Target.DeviceParameter) is null)
                {
                    issues.Add(new RoutingIssue(RoutingSeverity.Warning, RoutingIssueCode.AutomationParameterUnknown, $"An automation lane on {track.Name} targets parameter {lane.Target.DeviceParameter} of {found.Device.DisplayName}, which it does not define. The lane is kept."));
                }
            }

            foreach (var lane in track.Automation.Where(l => l.Target.IsInstrument))
            {
                if (project.FindInstrument(lane.Target.Instrument) is not { } instrument)
                {
                    issues.Add(new RoutingIssue(RoutingSeverity.Warning, RoutingIssueCode.AutomationInstrumentMissing, $"An automation lane on {track.Name} targets an external instrument that is not in the project. The lane is kept."));
                }
                else if (instrument.FindPort(lane.Target.Port) is null)
                {
                    issues.Add(new RoutingIssue(RoutingSeverity.Warning, RoutingIssueCode.AutomationInstrumentMissing, $"An automation lane on {track.Name} targets port {lane.Target.Port} of {instrument.Name}, which it does not have. The lane is kept."));
                }
            }
        }
    }

    /// <summary>A short name for a node, for messages.</summary>
    public static string Describe(Project project, SignalNode node)
    {
        ArgumentNullException.ThrowIfNull(project);
        return node.Kind switch
        {
            SignalNodeKind.Track => project.Sequence.FindTrack(node.AsTrack()) is { } track ? $"track \"{track.Name}\"" : "a missing track",
            SignalNodeKind.Rack => project.FindChain(node.AsRack()) is { } rack ? (rack.Name.Length > 0 ? $"rack \"{rack.Name}\"" : "a rack") : "a missing rack",
            SignalNodeKind.Device => project.FindDevice(node.AsDevice()) is { } found ? $"{found.Device.DisplayName} in {OwnerName(project, found.Chain)}" : "a missing device",
            SignalNodeKind.ExternalInstrument => project.FindInstrument(node.AsInstrument()) is { } instrument ? $"{instrument.Name}{(node.Port is null ? string.Empty : " port " + node.Port)}" : "a missing instrument",
            SignalNodeKind.MixerChannel => project.Mixer.Find(node.AsMixerChannel()) is { } channel ? $"mixer channel \"{channel.Name}\"" : "a missing mixer channel",
            _ => "the master",
        };
    }

    private static string OwnerName(Project project, DeviceChain chain) =>
        chain.Owner.Kind == ChainOwnerKind.Track && project.Sequence.FindTrack(chain.Owner.Track) is { } track ? $"\"{track.Name}\"" : (chain.Name.Length > 0 ? $"rack \"{chain.Name}\"" : "a rack");

    private static RoutingIssue Issue(RoutingSeverity severity, RoutingIssueCode code, string message, SignalConnection connection) =>
        new(severity, code, message, [connection.Id]);
}
