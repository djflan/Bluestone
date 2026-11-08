using System.Collections.Immutable;
using Bluestone.Application.Plugins;
using Bluestone.Audio;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Signal;

namespace Bluestone.Application.Audio;

/// <summary>A render graph for a project, with what could not be included and each plugin node's latency.</summary>
/// <param name="Graph">The graph to publish to the engine; its <see cref="AudioGraph.LatencyFrames"/> is the longest path's latency, which the transport renders ahead by.</param>
/// <param name="Problems">Missing or unreadable audio files, and devices that make no sound here, in words.</param>
/// <param name="LatencyFrames">Each plugin device in the graph: frames its output lags (pipelining plus the plugin's own).</param>
public sealed record ProjectAudioGraphResult(AudioGraph Graph, ImmutableArray<string> Problems, ImmutableDictionary<DeviceId, int> LatencyFrames);

/// <summary>
/// Turns a project into a render graph (ADR 0032): audio clips, plugin instruments and effects in chain order, the
/// audio connections (ADR 0023) into mixer channels, buses, the master, and other chains, and the mixer's gain,
/// pan, mute, and solo. It keeps node and strip state between builds, so an edit while playing does not restart a
/// clip's stream, lose a plugin's note tracking, or reset a meter. Every path is delay-compensated (ADR 0035): the
/// compensation delay lines are kept between builds too, and a plugin that reports a new latency raises
/// <see cref="LatencyChanged"/> so the graph can be built again.
/// </summary>
/// <remarks>
/// Building has two halves (ADR 0044): describing the graph from the project (<see cref="Describe"/>, an
/// <see cref="AudioGraphDescription"/> of plain data, which the UI process can do) and realizing it
/// (<see cref="AudioGraphRealizer"/>, which owns the nodes, strip states, and delay lines, and will run in the engine
/// process). <see cref="Build"/> does both in this process.
/// </remarks>
/// <remarks>
/// A chain's audio starts with the owner's own audio (a track's audio clips, when the track plays by the mute and
/// solo rule, plus audio connected into the track or rack). Each device in order: a plugin audio effect processes
/// it; a plugin instrument adds its output; anything else (bypassed devices, MIDI effects, built-in event devices,
/// plugins that are not running) passes it on unchanged. A tap takes the audio after its device. Connections that
/// routing validation rejects are left out, and so are mixer outputs and sends that would close a loop. Every junction of a
/// chain the graph reaches gets a meter point (the chain's input by the chain's key, the point after a device by the
/// device's); a chain whose audio goes nowhere is not in the graph, so its junctions have no meter. Control
/// thread only (it opens files). It owns the clip nodes it opens: a node it replaces is closed at the next build (by
/// then the graph that replaced it is the one playing), and <see cref="Dispose"/> closes the rest.
/// </remarks>
public sealed class ProjectAudioGraph : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _blockFrames;
    private readonly Func<DeviceId, PluginInstance?> _instances;
    private readonly AudioGraphRealizer _realizer;
    private readonly Lock _monitorGate = new();
    private ImmutableHashSet<DeviceId> _monitored = [];
    private ImmutableHashSet<DeviceId> _livePlayed = [];

    /// <param name="sampleRate">The engine's rate.</param>
    /// <param name="blockFrames">The engine's block size.</param>
    /// <param name="instances">The running plugin instance of a device, if any (the plugin bridge).</param>
    public ProjectAudioGraph(int sampleRate, int blockFrames, Func<DeviceId, PluginInstance?> instances)
    {
        _sampleRate = sampleRate;
        _blockFrames = blockFrames;
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _realizer = new AudioGraphRealizer(sampleRate, blockFrames, described => instances(new DeviceId(described.Key)));
        _realizer.LatencyChanged += (_, _) => LatencyChanged?.Invoke(this, EventArgs.Empty);
    }

    public MixerStripState Master => _realizer.Master;

    /// <summary>A plugin in the graph reported a new latency (any thread); build the graph again to compensate for it.</summary>
    public event EventHandler? LatencyChanged;

    /// <summary>
    /// Plugin instruments played live with low-latency monitoring (ADR 0035): their output is not delayed for
    /// compensation. Takes effect at the next build. Any thread.
    /// </summary>
    public ImmutableHashSet<DeviceId> Monitored
    {
        get
        {
            lock (_monitorGate)
            {
                return _monitored;
            }
        }

        set
        {
            lock (_monitorGate)
            {
                _monitored = value ?? [];
            }
        }
    }

    /// <summary>
    /// Plugin instruments being played live, described for a depth-1 exchange (ADR 0043 decision 6): the engine process
    /// submits and collects their blocks in the same callback, so they add no pipeline block, and their latency in the
    /// description, and so the compensation, follows. Only the engine reads it; in this process instances keep their
    /// depth. Takes effect at the next description. Any thread.
    /// </summary>
    public ImmutableHashSet<DeviceId> LivePlayed
    {
        get
        {
            lock (_monitorGate)
            {
                return _livePlayed;
            }
        }

        set
        {
            lock (_monitorGate)
            {
                _livePlayed = value ?? [];
            }
        }
    }

    /// <summary>The meter and gain state of a mixer channel, once a graph has included it. Any thread (meters are read from the UI).</summary>
    public MixerStripState? StripOf(MixerChannelId channel) => _realizer.StripOf(channel.Value);

    /// <summary>A chain junction's meter (a chain's ID for its input, a device's for the point after it), once a graph has it. Any thread.</summary>
    public AudioMeter? JunctionMeterOf(Guid junction) => _realizer.JunctionMeterOf(junction);

    /// <summary>The junctions to measure, replacing the previous set (see <see cref="AudioGraphRealizer.Watch"/>). Any thread.</summary>
    public void WatchJunctions(IEnumerable<Guid> junctions) => _realizer.Watch(junctions);

    /// <summary>The node playing a plugin device, once a graph has included it. Control thread.</summary>
    public PluginNode? PluginNodeOf(DeviceId device) => _realizer.PluginNodes.GetValueOrDefault(device.Value);

    /// <summary>Every plugin node of the last graph built, by device. Any thread.</summary>
    public ImmutableDictionary<DeviceId, PluginNode> PluginNodes { get; private set; } = ImmutableDictionary<DeviceId, PluginNode>.Empty;

    /// <param name="project">The project.</param>
    /// <param name="signal">Its evaluated signal graph (software-instrument and parameter feeds).</param>
    /// <param name="devices">The device catalog (for what each device consumes and produces).</param>
    /// <param name="projectDirectory">Where relative audio file locations start; null resolves them against the working directory.</param>
    public ProjectAudioGraphResult Build(Project project, SignalGraphResult signal, DeviceCatalog devices, string? projectDirectory)
    {
        var (description, problems) = Describe(project, signal, devices, projectDirectory);
        var realized = _realizer.Realize(description);
        PluginNodes = _realizer.PluginNodes.ToImmutableDictionary(p => new DeviceId(p.Key), p => p.Value);
        return new ProjectAudioGraphResult(
            realized.Graph,
            [.. problems.Concat(realized.Problems).Distinct()],
            realized.LatencyFrames.ToImmutableDictionary(p => new DeviceId(p.Key), p => p.Value));
    }

    /// <summary>
    /// The graph for <paramref name="project"/> as plain data, and what it leaves out in words: the half of
    /// <see cref="Build"/> that needs only the project and the running plugin instances (ADR 0044).
    /// </summary>
    public (AudioGraphDescription Description, ImmutableArray<string> Problems) Describe(Project project, SignalGraphResult signal, DeviceCatalog devices, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(devices);
        return new Builder(this, project, signal, devices, projectDirectory).Run();
    }

    /// <summary>Closes the files of every clip node this object opened. Call once no engine renders its graphs.</summary>
    public void Dispose() => _realizer.Dispose();

    /// <summary>The song frame of a tick, at <paramref name="sampleRate"/>.</summary>
    public static long FrameAt(TempoMap tempo, Tick position, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(tempo);
        return (long)Math.Round(SecondsAt(tempo, position) * sampleRate);
    }

    /// <summary>A plugin device's events and automation as a schedule in song frames: notes become note-on and note-off pairs.</summary>
    public static PluginSchedule Schedule(ImmutableArray<SignalEvent> events, ImmutableArray<Domain.Sequencing.ParameterChange> parameters, TempoMap tempo, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(tempo);
        var scheduled = new List<(long Frame, int Rank, int Order, PluginEvent Event)>(events.Length * 2);
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i].Event;
            var frame = FrameAt(tempo, e.Position, sampleRate);
            switch (e)
            {
                case NoteEvent n:
                    scheduled.Add((frame, 1, i, PluginEvent.NoteOn(0, n.Channel.Index, n.Note.Value, n.Velocity.Value)));
                    scheduled.Add((Math.Max(frame + 1, FrameAt(tempo, n.EndPosition, sampleRate)), 0, i, PluginEvent.NoteOff(0, n.Channel.Index, n.Note.Value, n.ReleaseVelocity.Value)));
                    break;
                case NoteOffEvent n:
                    scheduled.Add((frame, 0, i, PluginEvent.NoteOff(0, n.Channel.Index, n.Note.Value, n.ReleaseVelocity.Value)));
                    break;
                case PolyPressureEvent p:
                    scheduled.Add((frame, 1, i, PluginEvent.PolyPressure(0, p.Channel.Index, p.Note.Value, p.Pressure.ToSevenBit())));
                    break;
                case ControllerEvent c:
                    scheduled.Add((frame, 1, i, PluginEvent.ControlChange(0, c.Channel.Index, c.Controller.Value, c.Value.ToSevenBit())));
                    break;
                case ChannelPressureEvent c:
                    scheduled.Add((frame, 1, i, PluginEvent.ChannelPressure(0, c.Channel.Index, c.Pressure.ToSevenBit())));
                    break;
                case PitchBendEvent b:
                    scheduled.Add((frame, 1, i, PluginEvent.PitchBend(0, b.Channel.Index, b.Value.ToFourteenBit())));
                    break;
                case ProgramEvent { Selection: { BankMsb: null, BankLsb: null } } program:
                    scheduled.Add((frame, 1, i, PluginEvent.ProgramChange(0, program.Channel.Index, program.Selection.Program.Value)));
                    break;
                case SysExEvent s when PluginEvent.TryCreateSystemExclusive(0, s.Message.Bytes.Span, out var sysEx):
                    scheduled.Add((frame, 1, i, sysEx));
                    break;
            }
        }

        // Note-offs before note-ons at one frame; otherwise canonical order.
        var ordered = scheduled.OrderBy(s => s.Frame).ThenBy(s => s.Rank).ThenBy(s => s.Order).Select(s => new ScheduledEvent(s.Frame, s.Event));
        var changes = parameters.Select(p => new ScheduledParameterChange(FrameAt(tempo, p.Position, sampleRate), p.Parameter.Value, p.Value.ToFraction()));
        return new PluginSchedule(ordered, changes);
    }

    // Song time in seconds; before tick 0 at the first tempo.
    private static double SecondsAt(TempoMap tempo, Tick position) => SecondsAt(tempo, position.Value);

    // A clip's origin can be before tick 0 (a trimmed clip moved to the start).
    private static double SecondsAt(TempoMap tempo, long position) => tempo.SignedTimeAt(position).TotalSeconds;

    private sealed class Builder(ProjectAudioGraph owner, Project project, SignalGraphResult signal, DeviceCatalog devices, string? directory)
    {
        private readonly AudioGraphDescriptionBuilder _builder = new();
        private readonly ImmutableArray<string>.Builder _problems = ImmutableArray.CreateBuilder<string>();
        private readonly Dictionary<DeviceChainId, List<int>[]> _chains = [];
        private readonly Dictionary<TrackId, List<int>> _starts = [];
        private readonly HashSet<SignalNode> _visiting = [];
        private readonly ImmutableHashSet<DeviceId> _monitored = owner.Monitored;
        private readonly ImmutableHashSet<DeviceId> _livePlayed = owner.LivePlayed;
        private readonly Dictionary<MixerChannelId, int> _stripIndex = [];
        private ImmutableArray<SignalConnection> _audio = [];

        public (AudioGraphDescription Description, ImmutableArray<string> Problems) Run()
        {
            var rejected = SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(project, devices.Lookup)).SelectMany(i => i.Connections).ToHashSet();
            _audio = [.. project.Connections.Where(c => c.Kind == SignalKind.Audio && !rejected.Contains(c.Id))];
            AddStrips();
            foreach (var connection in _audio)
            {
                if (connection.Destination.Kind == SignalNodeKind.MixerChannel && _stripIndex.TryGetValue(connection.Destination.AsMixerChannel(), out var strip))
                {
                    foreach (var node in Output(connection.Source))
                    {
                        _builder.ConnectToStrip(node, strip);
                    }
                }
                else if (connection.Destination.Kind == SignalNodeKind.Master)
                {
                    foreach (var node in Output(connection.Source))
                    {
                        _builder.ConnectToMaster(node);
                    }
                }
            }

            // Nodes of every device and track in the project are kept, even those this graph does not play.
            var retained = project.Chains.SelectMany(c => c.Devices).Select(d => d.Id.Value).Concat(project.Sequence.Tracks.Select(t => t.Id.Value));
            return (_builder.Build(project.Mixer.MasterGainDecibels, retained), _problems.Distinct().ToImmutableArray());
        }

        private void AddStrips()
        {
            foreach (var channel in project.Mixer.Channels)
            {
                _stripIndex[channel.Id] = _builder.AddStrip(new DescribedStrip(channel.Id.Value, new MixerStripSettings(channel.GainDecibels, channel.Pan, channel.IsMuted, channel.IsSoloed)));
            }

            // Outputs first, then sends; an edge that would close a loop (only in a file edited by hand) is left out.
            var edges = new Dictionary<int, List<int>>();
            foreach (var channel in project.Mixer.Channels)
            {
                if (channel.Output is { } bus && _stripIndex.TryGetValue(bus, out var destination))
                {
                    var from = _stripIndex[channel.Id];
                    if (AddEdge(edges, from, destination))
                    {
                        _builder.RouteStrip(from, destination);
                    }
                    else
                    {
                        _problems.Add($"{channel.Name}: its output would feed back on itself, so it goes to the master.");
                    }
                }
            }

            foreach (var channel in project.Mixer.Channels)
            {
                foreach (var send in channel.Sends)
                {
                    if (_stripIndex.TryGetValue(send.Target, out var destination) && destination != _stripIndex[channel.Id])
                    {
                        var from = _stripIndex[channel.Id];
                        if (AddEdge(edges, from, destination))
                        {
                            _builder.SendStrip(from, destination, send.LevelDecibels, send.PreFader);
                        }
                        else
                        {
                            _problems.Add($"{channel.Name}: a send would feed back on itself, so it is silent.");
                        }
                    }
                }
            }
        }

        // Adds from → to unless to already reaches from.
        private static bool AddEdge(Dictionary<int, List<int>> edges, int from, int to)
        {
            var seen = new HashSet<int>();
            var pending = new Stack<int>([to]);
            while (pending.TryPop(out var at))
            {
                if (at == from)
                {
                    return false;
                }

                if (seen.Add(at) && edges.TryGetValue(at, out var next))
                {
                    next.ForEach(pending.Push);
                }
            }

            if (!edges.TryGetValue(from, out var list))
            {
                edges[from] = list = [];
            }

            list.Add(to);
            return true;
        }

        // The audio nodes a source's output is the sum of.
        private List<int> Output(SignalNode source)
        {
            if (!_visiting.Add(source))
            {
                return [];
            }

            try
            {
                switch (source.Kind)
                {
                    case SignalNodeKind.Track:
                        var track = source.AsTrack();
                        return project.ChainOf(track) is { } chain ? Fold(chain)[^1] : Start(track);
                    case SignalNodeKind.Rack:
                        return project.FindChain(source.AsRack()) is { } rack ? Fold(rack)[^1] : [];
                    case SignalNodeKind.Device when project.FindDevice(source.AsDevice()) is var (owningChain, device):
                        return Fold(owningChain)[owningChain.Devices.IndexOf(device) + 1];
                    case SignalNodeKind.ChainInput when project.FindChain(source.AsChain()) is { } inputOf:
                        return Fold(inputOf)[0];
                    default:
                        return [];
                }
            }
            finally
            {
                _visiting.Remove(source);
            }
        }

        // The audio before the chain's first device ([0]) and after each device ([i + 1]).
        private List<int>[] Fold(DeviceChain chain)
        {
            if (_chains.TryGetValue(chain.Id, out var done))
            {
                return done;
            }

            var points = new List<int>[chain.Devices.Length + 1];
            var audio = chain.Owner.Kind == ChainOwnerKind.Track ? Start(chain.Owner.Track) : Inputs(SignalNode.Rack(chain.Id));
            points[0] = audio;
            for (var i = 0; i < chain.Devices.Length; i++)
            {
                var device = chain.Devices[i];
                var definition = device.IsBypassed ? null : devices.Find(device.Definition.Id);
                if (definition?.Id == BuiltInDevices.AudioReceiver.Id)
                {
                    audio = Receive(chain, points, i, device, audio);
                }
                else if (definition is not null && definition.Produces.HasFlag(SignalKinds.Audio))
                {
                    if (NodeOf(device) is { } plugin)
                    {
                        var node = _builder.AddNode(plugin);
                        if (_monitored.Contains(device.Id))
                        {
                            _builder.Monitor(node);
                        }

                        if (definition.Consumes.HasFlag(SignalKinds.Audio))
                        {
                            foreach (var source in audio)
                            {
                                _builder.Connect(source, node);
                            }

                            audio = [node];
                        }
                        else
                        {
                            audio = [.. audio, node];
                        }
                    }
                    else if (!device.Definition.Id.Value.StartsWith(PluginDeviceHost.DefinitionPrefix, StringComparison.Ordinal))
                    {
                        _problems.Add($"{definition.Name} has no sound of its own; only plugin instruments and effects process audio.");
                    }
                }

                points[i + 1] = audio;
            }

            // Every junction can be metered (ADR 0037): the chain's input by the chain, the rest by the device before it.
            _builder.AddMeterPoint(chain.Id.Value, points[0]);
            for (var i = 0; i < chain.Devices.Length; i++)
            {
                _builder.AddMeterPoint(chain.Devices[i].Id.Value, points[i + 1]);
            }

            _chains[chain.Id] = points;
            return points;
        }

        // An Audio Receiver at chain index `at` (ADR 0037): what its input receives, at its gain, mixed with the chain's audio
        // or replacing it. A source earlier in the same chain is that junction's audio; validation keeps it earlier.
        private List<int> Receive(DeviceChain chain, List<int>[] points, int at, DeviceInstance device, List<int> audio)
        {
            // One gain node per connection and source node, never one summing them, at every gain (0 dB too, so taking a
            // node the chain already has, or one two connections both reach, is still a path of its own): a monitored
            // source (a live-played instrument) passes undelayed through its own node, and every other source keeps its
            // delay compensation through its own. A node's key follows its connection and source, so removing one source
            // leaves the others' nodes and delay lines as they were.
            var input = SignalNode.DeviceInput(device.Id, DeviceInputPort.Receive);
            var level = (float)Math.Pow(10, BuiltInDevices.ReceiverGainOf(device) / 20);
            var received = new List<int>();
            foreach (var connection in _audio.Where(c => c.Destination == input))
            {
                foreach (var source in SourcesOf(chain, points, at, connection).Distinct())
                {
                    var node = _builder.AddNode(new DescribedNode(DescribedNodeKind.Gain, GainKey(GainKey(device.Id.Value, connection.Id.Value), _builder.KeyOf(source)), device.DisplayName) { Gain = level });
                    _builder.Connect(source, node);
                    received.Add(node);
                }
            }

            return BuiltInDevices.ReplacesOf(device) ? received : [.. audio, .. received];
        }

        // The audio nodes one connection into a device input at chain index `at` brings: a source earlier in the same chain
        // is that junction's audio (validation keeps it earlier); any other is its owner's output, or the point after its device.
        private List<int> SourcesOf(DeviceChain chain, List<int>[] points, int at, SignalConnection connection) =>
            SignalFlow.JunctionOf(project, connection.Source) is var (source, junction) && source.Id == chain.Id
                ? junction <= at ? points[junction] : []
                : Output(connection.Source);

        // A stable key for a receiver's gain node on one source: the receiver's and the source node's keys combined, so the
        // node keeps its state between builds whatever other sources come and go.
        private static Guid GainKey(Guid device, Guid source)
        {
            Span<byte> a = stackalloc byte[16];
            Span<byte> b = stackalloc byte[16];
            device.TryWriteBytes(a);
            source.TryWriteBytes(b);
            for (var i = 0; i < 16; i++)
            {
                a[i] ^= b[(i + 7) % 16];
            }

            return new Guid(a);
        }

        // A track's own audio: its clips, when it plays, and audio connected into it.
        private List<int> Start(TrackId id)
        {
            if (_starts.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var nodes = Inputs(SignalNode.Track(id));
            if (project.Sequence.FindTrack(id) is { } track && Plays(track) && ClipsOf(track) is { } clips)
            {
                nodes = [.. nodes, _builder.AddNode(clips)];
            }

            _starts[id] = nodes;
            return nodes;
        }

        private List<int> Inputs(SignalNode destination) =>
            [.. _audio.Where(c => c.Destination == destination).SelectMany(c => Output(c.Source)).Distinct()];

        // The plan's rule, groups included (ADR 0030): mute and solo apply to a track's own content.
        private bool Plays(Track track) => GroupTracks.Plays(project.Sequence, track);

        private DescribedNode? ClipsOf(Track track)
        {
            var tempo = project.Sequence.TempoMap;
            var rate = owner._sampleRate;
            ImmutableArray<AudioClipRegion> regions = [.. track.Clips.OfType<AudioClip>().Select(clip =>
            {
                var start = SecondsAt(tempo, clip.Start);
                var startFrame = (long)Math.Round(start * rate);
                var endFrame = (long)Math.Round(SecondsAt(tempo, clip.End) * rate);
                var (fadeIn, fadeOut) = clip.EffectiveFades;
                var origin = SecondsAt(tempo, clip.Origin);
                // A loop is a fixed stretch of the file, measured where the clip's content starts (no time-stretching).
                var loop = clip.Loop is { IsEnabled: true } l ? (Start: SecondsAt(tempo, clip.Origin + l.Start.Value) - origin, Length: SecondsAt(tempo, clip.Origin + l.End.Value) - SecondsAt(tempo, clip.Origin + l.Start.Value)) : default((double Start, double Length)?);
                return new AudioClipRegion(Resolve(clip.Source.Location), startFrame, endFrame, start - origin)
                {
                    LoopStartSeconds = loop?.Start,
                    LoopLengthSeconds = loop?.Length,
                    Envelope = new AudioClipEnvelope(
                        PanLaw.DecibelsToGain(clip.GainDecibels),
                        fadeIn.Value == 0 ? 0 : (long)Math.Round(SecondsAt(tempo, clip.Start + fadeIn) * rate) - startFrame,
                        fadeOut.Value == 0 ? 0 : endFrame - (long)Math.Round(SecondsAt(tempo, new Tick(clip.End.Value - fadeOut.Value)) * rate),
                        clip.FadeShape == FadeShape.EqualPower),
                };
            })];
            if (regions.IsEmpty)
            {
                return null;
            }

            return new DescribedNode(DescribedNodeKind.Clips, track.Id.Value, track.Name) { Regions = regions };
        }

        private string Resolve(string location) =>
            Path.IsPathRooted(location) || directory is null ? location : Path.GetFullPath(Path.Combine(directory, location));

        private DescribedNode? NodeOf(DeviceInstance device)
        {
            if (owner._instances(device.Id) is not { } instance)
            {
                return null;
            }

            var name = device.Definition.DisplayName ?? device.Definition.Id.Value;
            if ((int)instance.Request.SampleRate != owner._sampleRate)
            {
                _problems.Add($"{name} runs at {instance.Request.SampleRate} Hz and the audio device at {owner._sampleRate} Hz, so it is silent.");
                return null;
            }

            if (instance.Request.MaxBlockFrames < owner._blockFrames)
            {
                _problems.Add($"{name} takes at most {instance.Request.MaxBlockFrames} frames per block and the engine renders {owner._blockFrames}, so it is silent.");
                return null;
            }

            var feed = signal.SoftwareInstruments.FirstOrDefault(f => f.Device == device.Id);
            var parameters = signal.Parameters.FirstOrDefault(f => f.Device == device.Id);
            var schedule = Schedule(feed?.Events ?? [], parameters?.Changes ?? [], project.Sequence.TempoMap, owner._sampleRate);
            var request = instance.Request;
            var depth = _livePlayed.Contains(device.Id) ? 1 : instance.PipelineDepth;
            var link = new DescribedPluginLink(
                instance.ExchangeShape,
                depth,
                instance.CollectWait,
                request.FailurePolicy == FailurePolicy.BypassInput,
                PipelineLatency.ReportedLatencyFrames(instance.PluginLatencyFrames, depth, request.MaxBlockFrames),
                instance.DataPlane);
            return new DescribedNode(DescribedNodeKind.Plugin, device.Id.Value, name) { Schedule = schedule, Link = link };
        }
    }
}
