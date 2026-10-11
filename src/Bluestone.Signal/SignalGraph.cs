using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal;

/// <summary>The events one connection delivers to a part of an external instrument, channel mapping applied.</summary>
/// <param name="Connection">The connection that delivers them.</param>
/// <param name="Instrument">The instrument.</param>
/// <param name="Port">The instrument's port the events go to, resolved (never null).</param>
/// <param name="Voice">A voice to select on the part before the first event; resolving it needs the instrument's profile.</param>
/// <param name="Events">The events in canonical order.</param>
public sealed record ExternalPartFeed(ConnectionId Connection, ExternalInstrumentId Instrument, string Port, VoiceAssignment? Voice, ImmutableArray<SignalEvent> Events)
{
    /// <summary>The channel every channel event is sent on, when the connection forces one.</summary>
    public MidiChannel? ForcedChannel { get; init; }
}

/// <summary>
/// The events a software instrument takes in: everything that reaches it from every track routed into its
/// chain, merged in canonical order. Bluestone has no audio engine yet, so nothing plays them (ADR 0022).
/// </summary>
public sealed record SoftwareInstrumentFeed(DeviceId Device, DeviceChainId Chain, ImmutableArray<SignalEvent> Events);

/// <summary>Device automation for a device that is not processed in Bluestone's process (a plugin or a software instrument).</summary>
public sealed record ParameterFeed(DeviceId Device, ImmutableArray<ParameterChange> Changes);

/// <summary>Everything the signal graph sends, for the playback plan and, later, the audio engine and plugin workers.</summary>
public sealed record SignalGraphResult
{
    public ImmutableArray<ExternalPartFeed> ExternalParts { get; init; } = [];

    public ImmutableArray<SoftwareInstrumentFeed> SoftwareInstruments { get; init; } = [];

    public ImmutableArray<ParameterFeed> Parameters { get; init; } = [];

    public ImmutableArray<SignalDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Evaluates a project's event routing: renders each track, runs its chain, follows every event connection
/// to the next track, rack, or external instrument, and collects what arrives (ADR 0023). Built-in devices
/// run here, at plan-compile time; plugins and audio do not.
/// </summary>
/// <remarks>
/// <para>
/// Order: tracks and racks are evaluated so that everything feeding a node runs before it. A node's input
/// is its own content (a track that plays, by the mute and solo rule of <see cref="GroupTracks"/>) merged with everything
/// connected into it, in canonical order (position, phase, origin track, index). A connection takes the
/// end of its source's chain, or the point after one device for a tap, and applies its channel mapping.
/// </para>
/// <para>
/// Connections that routing validation rejects are left out and reported, and so is any connection that
/// would still close a loop; evaluation never fails because of the project's routing. Device automation
/// is not signal: it is rendered for every track whether it plays or not, applied to built-in devices
/// at its positions, and returned as <see cref="ParameterFeed"/>s for every other device.
/// </para>
/// </remarks>
public static class SignalGraph
{
    public static SignalGraphResult Evaluate(Project project, DeviceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);
        return new Evaluation(project, catalog).Run();
    }

    /// <summary>
    /// The channel a track's content is rendered on for automation conflicts: the single channel its
    /// outgoing event connections force, or none when they force none or several (handoff decision 10).
    /// </summary>
    public static MidiChannel? ChannelOverride(IEnumerable<SignalConnection> connections, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(connections);
        MidiChannel? found = null;
        var source = SignalNode.Track(track);
        foreach (var connection in connections)
        {
            if (connection.Source != source || connection.Kind != SignalKind.Events || connection.Mapping.Force is not { } forced)
            {
                continue;
            }

            if (found is { } other && other != forced)
            {
                return null;
            }

            found = forced;
        }

        return found;
    }

    /// <summary>Applies <paramref name="mapping"/>: drops filtered channels and readdresses the rest. Events without a channel pass.</summary>
    public static ImmutableArray<SignalEvent> Map(ReadOnlySpan<SignalEvent> events, ChannelMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.IsPreserving)
        {
            return [.. events];
        }

        var mapped = ImmutableArray.CreateBuilder<SignalEvent>(events.Length);
        foreach (var e in events)
        {
            if (e.Event is not ChannelEvent channelEvent)
            {
                mapped.Add(e);
            }
            else if (mapping.TryMap(channelEvent.Channel, out var channel))
            {
                mapped.Add(channel == channelEvent.Channel ? e : e.With(channelEvent with { Channel = channel }));
            }
        }

        return mapped.ToImmutable();
    }

    private sealed class Evaluation(Project project, DeviceCatalog catalog) : IChainObserver
    {
        private readonly List<SignalDiagnostic> _diagnostics = [];
        private readonly Dictionary<ConnectionId, ImmutableArray<SignalEvent>> _delivered = [];
        private readonly Dictionary<DeviceId, ImmutableArray<SignalEvent>> _taps = [];
        private readonly HashSet<DeviceId> _tapped = [];
        private readonly List<SoftwareInstrumentFeed> _instruments = [];
        private readonly List<ExternalPartFeed> _parts = [];
        private readonly List<SignalConnection> _connections = [];
        private readonly HashSet<TrackId> _playing = [];
        private DeviceChainId _currentChain;

        public SignalGraphResult Run()
        {
            SelectConnections();
            var order = Order();
            var rendered = RenderTracks(out var changes);
            var runners = new Dictionary<DeviceChainId, ChainRunner>();
            foreach (var node in order)
            {
                Evaluate(node, rendered, changes, runners);
            }

            return new SignalGraphResult
            {
                ExternalParts = [.. _parts],
                SoftwareInstruments = [.. _instruments],
                Parameters = ParameterFeeds(changes, runners),
                Diagnostics = [.. _diagnostics],
            };
        }

        public void AfterDevice(DeviceId device, ReadOnlySpan<SignalEvent> events)
        {
            if (_tapped.Contains(device))
            {
                _taps[device] = [.. events];
            }
        }

        public void InstrumentInput(DeviceId device, ReadOnlySpan<SignalEvent> events)
        {
            _instruments.Add(new SoftwareInstrumentFeed(device, _currentChain, [.. events]));
            if (!events.IsEmpty)
            {
                var name = project.FindDevice(device)?.Device.DisplayName ?? "A software instrument";
                _diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.NotAudible, $"{name} receives events, but Bluestone has no audio engine yet, so it does not sound.") { Device = device });
            }
        }

        // Event connections that routing validation accepts; every rejected connection is reported. Audio has no
        // engine yet, so audio connections are checked but not followed.
        private void SelectConnections()
        {
            var rejected = new Dictionary<ConnectionId, string>();
            foreach (var issue in SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(project, catalog.Lookup)))
            {
                foreach (var id in issue.Connections)
                {
                    rejected.TryAdd(id, issue.Message);
                }
            }

            var seen = new HashSet<ConnectionId>();
            foreach (var connection in project.Connections)
            {
                if (!seen.Add(connection.Id))
                {
                    continue;
                }

                if (rejected.TryGetValue(connection.Id, out var reason))
                {
                    _diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.InvalidConnection, $"A connection was left out: {reason}") { Connection = connection.Id });
                    continue;
                }

                if (connection.Kind != SignalKind.Events)
                {
                    continue;
                }

                _connections.Add(connection);
                if (connection.Source.Kind == SignalNodeKind.Device)
                {
                    _tapped.Add(connection.Source.AsDevice());
                }
            }
        }

        // Every track's events and device automation, rendered once. Device automation is gathered from every
        // track, playing or not, in track order and then position order.
        private ImmutableArray<SignalEvent>[] RenderTracks(out ImmutableArray<ParameterChange> changes)
        {
            var sequence = project.Sequence;
            var playing = GroupTracks.Playing(sequence);
            var rendered = new ImmutableArray<SignalEvent>[sequence.Tracks.Length];
            var allChanges = new List<ParameterChange>();
            for (var index = 0; index < sequence.Tracks.Length; index++)
            {
                var track = sequence.Tracks[index];
                var result = TrackRendering.Render(track, sequence.Ppqn, ChannelOverride(_connections, track.Id));
                allChanges.AddRange(result.ParameterChanges);
                if (!playing[index])
                {
                    rendered[index] = [];
                    continue;
                }

                _playing.Add(track.Id);

                if (result.SuppressedEvents > 0)
                {
                    Report(SignalDiagnosticCode.ReplacedByAutomation, track, $"{result.SuppressedEvents} events in clips were replaced by the track's automation.");
                }

                if (result.DroppedLanes > 0)
                {
                    Report(SignalDiagnosticCode.LaneDropped, track, $"{result.DroppedLanes} automation lanes control the same thing as another lane once sent on the route's channel, and were left out.");
                }

                var events = ImmutableArray.CreateBuilder<SignalEvent>(result.Events.Length);
                for (var i = 0; i < result.Events.Length; i++)
                {
                    if (EventClassification.Of(result.Events[i]) != EventClass.Meta)
                    {
                        events.Add(new SignalEvent(result.Events[i], index, i));
                    }
                }

                rendered[index] = events.ToImmutable();
            }

            changes = [.. allChanges.OrderBy(c => c.Position)];
            foreach (var orphan in changes.Select(c => c.Device).Distinct().Where(d => project.FindDevice(d) is null))
            {
                _diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.AutomationTargetMissing, "Automation targets a device that is not in the project; the lane is kept but has no effect.") { Device = orphan });
            }

            return rendered;
        }

        // Tracks (in track order) and racks (in chain order), sorted so every node comes after those feeding it.
        // A connection that still closes a loop after validation is left out and reported.
        private List<SignalNode> Order()
        {
            var nodes = project.Sequence.Tracks.Select(t => SignalNode.Track(t.Id))
                .Concat(project.Chains.Where(c => c.Owner.Kind == ChainOwnerKind.Rack).Select(c => SignalNode.Rack(c.Id)))
                .ToList();
            var known = nodes.ToHashSet();

            // A tap on a chain whose owning track is gone has no place in the order, and nothing would run it.
            foreach (var orphan in _connections.Where(c => OwnerOf(c.Source) is not { } owner || !known.Contains(owner)).ToList())
            {
                _connections.Remove(orphan);
                _diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.InvalidConnection, $"A connection was left out: {SignalRoutingValidator.Describe(project, orphan.Source)} belongs to a chain without a track.") { Connection = orphan.Id });
            }

            var edges = _connections
                .Where(c => known.Contains(c.Destination))
                .Select(c => (Connection: c, From: OwnerOf(c.Source)!.Value, To: c.Destination))
                .ToList();

            var order = new List<SignalNode>();
            var placed = new HashSet<SignalNode>();
            while (placed.Count < nodes.Count)
            {
                var ready = nodes.FindIndex(n => !placed.Contains(n) && edges.All(e => e.To != n || placed.Contains(e.From)));
                if (ready < 0)
                {
                    // Only loops are left: drop every connection between the nodes still waiting, then go on.
                    var waiting = nodes.Where(n => !placed.Contains(n)).ToHashSet();
                    foreach (var loop in edges.Where(e => waiting.Contains(e.From) && waiting.Contains(e.To)).ToList())
                    {
                        edges.Remove(loop);
                        _connections.Remove(loop.Connection);
                        _diagnostics.Add(new SignalDiagnostic(SignalDiagnosticCode.Feedback, $"A connection into {SignalRoutingValidator.Describe(project, loop.To)} would feed back on itself and was left out.") { Connection = loop.Connection.Id });
                    }

                    continue;
                }

                order.Add(nodes[ready]);
                placed.Add(nodes[ready]);
            }

            return order;
        }

        private void Evaluate(SignalNode node, ImmutableArray<SignalEvent>[] rendered, ImmutableArray<ParameterChange> changes, Dictionary<DeviceChainId, ChainRunner> runners)
        {
            var input = new List<SignalEvent>();
            Track? track = null;
            if (node.Kind == SignalNodeKind.Track)
            {
                var index = project.Sequence.Tracks.IndexOf(project.Sequence.FindTrack(node.AsTrack())!);
                track = project.Sequence.Tracks[index];
                input.AddRange(rendered[index]);
            }

            foreach (var connection in _connections.Where(c => c.Destination == node))
            {
                input.AddRange(_delivered.GetValueOrDefault(connection.Id, []));
            }

            // A stable sort keeps a connection's events in order and the listed order of connections on ties.
            var ordered = input.Order(Comparer<SignalEvent>.Create((a, b) => SignalOrder.Compare(a, b))).ToArray();
            var chain = track is null ? project.FindChain(node.AsRack()) : project.ChainOf(track.Id);
            ReadOnlySpan<SignalEvent> output = ordered;
            if (chain is not null)
            {
                var runner = new ChainRunner(chain, catalog, project.Sequence.Ppqn, project.Sequence.TempoMap);
                runners[chain.Id] = runner;
                _diagnostics.AddRange(runner.Diagnostics);
                var chainChanges = changes.Where(c => chain.Find(c.Device) is not null).ToArray();
                var result = new SignalBuffer(ordered.Length);
                _currentChain = chain.Id;
                runner.Run(SignalBlock.Everything, ordered, result, chainChanges, this);
                _diagnostics.AddRange(runner.TakeReports().Select(r => r with { Track = track?.Id }));
                output = result.Events;
            }

            var sent = false;
            foreach (var connection in _connections.Where(c => c.Source == node || (c.Source.Kind == SignalNodeKind.Device && chain?.Find(c.Source.AsDevice()) is not null)))
            {
                var from = connection.Source == node ? output : _taps.GetValueOrDefault(connection.Source.AsDevice(), []).AsSpan();
                Deliver(connection, Map(from, connection.Mapping));
                sent = true;
            }

            var hasInstrument = chain is not null && chain.Devices.Any(d => !d.IsBypassed && runners[chain.Id].KindOf(d.Id) == StageKind.Instrument);
            if (track is not null && _playing.Contains(track.Id) && !sent)
            {
                if (!hasInstrument)
                {
                    Report(SignalDiagnosticCode.Unrouted, track, "The track is not routed to an output and will not play.");
                }
                else if (!output.IsEmpty)
                {
                    Report(SignalDiagnosticCode.Unrouted, track, string.Create(CultureInfo.InvariantCulture, $"{output.Length} events that pass the track's instrument (such as SysEx) are not routed anywhere."));
                }
            }
        }

        private void Deliver(SignalConnection connection, ImmutableArray<SignalEvent> events)
        {
            if (connection.Destination.Kind != SignalNodeKind.ExternalInstrument)
            {
                _delivered[connection.Id] = events;
                return;
            }

            var instrument = project.FindInstrument(connection.Destination.AsInstrument())!;
            var port = instrument.FindPort(connection.Destination.Port)!;
            _parts.Add(new ExternalPartFeed(connection.Id, instrument.Id, port.Id, connection.Voice, events) { ForcedChannel = connection.Mapping.Force });
        }

        private ImmutableArray<ParameterFeed> ParameterFeeds(ImmutableArray<ParameterChange> changes, Dictionary<DeviceChainId, ChainRunner> runners) =>
        [
            .. changes
                .Where(c => project.FindDevice(c.Device) is { } found && !(runners.TryGetValue(found.Chain.Id, out var runner) && runner.Processes(c.Device)))
                .GroupBy(c => c.Device)
                .Select(g => new ParameterFeed(g.Key, [.. g])),
        ];

        // The track or rack whose chain a connection's source belongs to.
        private SignalNode? OwnerOf(SignalNode source) => source.Kind switch
        {
            SignalNodeKind.Track or SignalNodeKind.Rack => source,
            SignalNodeKind.Device when project.FindDevice(source.AsDevice()) is { } found =>
                found.Chain.Owner.Kind == ChainOwnerKind.Track ? SignalNode.Track(found.Chain.Owner.Track) : SignalNode.Rack(found.Chain.Id),
            _ => null,
        };

        private void Report(SignalDiagnosticCode code, Track track, string message) =>
            _diagnostics.Add(new SignalDiagnostic(code, message) { Track = track.Id });
    }
}
