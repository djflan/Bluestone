using System.Collections.Concurrent;
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
using NotificationKind = Bluestone.Plugins.Protocol.NotificationKind;

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
/// A chain's audio starts with the owner's own audio (a track's audio clips, when the track plays by the mute and
/// solo rule, plus audio connected into the track or rack). Each device in order: a plugin audio effect processes
/// it; a plugin instrument adds its output; anything else (bypassed devices, MIDI effects, built-in event devices,
/// plugins that are not running) passes it on unchanged. A tap takes the audio after its device. Connections that
/// routing validation rejects are left out, and so are mixer outputs and sends that would close a loop. Control
/// thread only (it opens files). It owns the clip nodes it opens: a node it replaces is closed at the next build (by
/// then the graph that replaced it is the one playing), and <see cref="Dispose"/> closes the rest.
/// </remarks>
public sealed class ProjectAudioGraph : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _blockFrames;
    private readonly Func<DeviceId, PluginInstance?> _instances;
    private readonly ConcurrentDictionary<MixerChannelId, MixerStripState> _strips = new();
    private readonly Dictionary<DeviceId, PluginNode> _plugins = [];
    private readonly Dictionary<TrackId, ClipEntry> _clips = [];
    private readonly List<ClipPlayerNode> _retired = [];
    private readonly LatencyCompensation _compensation = new();
    private readonly Lock _monitorGate = new();
    private ImmutableHashSet<DeviceId> _monitored = [];

    /// <param name="sampleRate">The engine's rate.</param>
    /// <param name="blockFrames">The engine's block size.</param>
    /// <param name="instances">The running plugin instance of a device, if any (the plugin bridge).</param>
    public ProjectAudioGraph(int sampleRate, int blockFrames, Func<DeviceId, PluginInstance?> instances)
    {
        _sampleRate = sampleRate;
        _blockFrames = blockFrames;
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
    }

    public MixerStripState Master { get; } = new();

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

    /// <summary>The meter and gain state of a mixer channel, once a graph has included it. Any thread (meters are read from the UI).</summary>
    public MixerStripState? StripOf(MixerChannelId channel) => _strips.GetValueOrDefault(channel);

    /// <summary>The node playing a plugin device, once a graph has included it. Control thread.</summary>
    public PluginNode? PluginNodeOf(DeviceId device) => _plugins.GetValueOrDefault(device);

    /// <summary>Every plugin node of the last graph built, by device. Any thread.</summary>
    public ImmutableDictionary<DeviceId, PluginNode> PluginNodes { get; private set; } = ImmutableDictionary<DeviceId, PluginNode>.Empty;

    /// <param name="project">The project.</param>
    /// <param name="signal">Its evaluated signal graph (software-instrument and parameter feeds).</param>
    /// <param name="devices">The device catalog (for what each device consumes and produces).</param>
    /// <param name="projectDirectory">Where relative audio file locations start; null resolves them against the working directory.</param>
    public ProjectAudioGraphResult Build(Project project, SignalGraphResult signal, DeviceCatalog devices, string? projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(devices);
        foreach (var node in _retired)
        {
            node.Dispose();
        }

        _retired.Clear();
        return new Builder(this, project, signal, devices, projectDirectory).Run();
    }

    /// <summary>Closes the files of every clip node this object opened. Call once no engine renders its graphs.</summary>
    public void Dispose()
    {
        foreach (var node in _retired.Concat(_clips.Values.Select(c => c.Node)))
        {
            node.Dispose();
        }

        _retired.Clear();
        _clips.Clear();
        foreach (var plugin in _plugins.Values)
        {
            plugin.Instance.Changed -= OnPluginChanged;
        }

        _plugins.Clear();
    }

    private void OnPluginChanged(object? sender, NotificationKind kind)
    {
        if (kind == NotificationKind.LatencyChanged)
        {
            LatencyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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

    private sealed record ClipEntry(ImmutableArray<AudioClipRegion> Regions, ClipPlayerNode Node, IReadOnlyList<string> Problems);

    private sealed class Builder(ProjectAudioGraph owner, Project project, SignalGraphResult signal, DeviceCatalog devices, string? directory)
    {
        private readonly AudioGraphBuilder _builder = new();
        private readonly ImmutableArray<string>.Builder _problems = ImmutableArray.CreateBuilder<string>();
        private readonly ImmutableDictionary<DeviceId, int>.Builder _latency = ImmutableDictionary.CreateBuilder<DeviceId, int>();
        private readonly Dictionary<DeviceChainId, List<int>[]> _chains = [];
        private readonly Dictionary<TrackId, List<int>> _starts = [];
        private readonly HashSet<SignalNode> _visiting = [];
        private readonly ImmutableHashSet<DeviceId> _monitored = owner.Monitored;
        private readonly Dictionary<MixerChannelId, int> _stripIndex = [];
        private ImmutableArray<SignalConnection> _audio = [];

        public ProjectAudioGraphResult Run()
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

            ForgetUnused();
            owner.PluginNodes = owner._plugins.ToImmutableDictionary();
            var graph = _builder.Build(owner._blockFrames, owner.Master, project.Mixer.MasterGainDecibels, owner._compensation);
            return new ProjectAudioGraphResult(graph, _problems.Distinct().ToImmutableArray(), _latency.ToImmutable());
        }

        private void AddStrips()
        {
            foreach (var channel in project.Mixer.Channels)
            {
                if (!owner._strips.TryGetValue(channel.Id, out var state))
                {
                    state = new MixerStripState();
                    owner._strips[channel.Id] = state;
                }

                _stripIndex[channel.Id] = _builder.AddStrip(state, new MixerStripSettings(channel.GainDecibels, channel.Pan, channel.IsMuted, channel.IsSoloed));
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

            foreach (var gone in owner._strips.Keys.Where(id => !_stripIndex.ContainsKey(id)).ToList())
            {
                owner._strips.TryRemove(gone, out _);
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
                if (definition is not null && definition.Produces.HasFlag(SignalKinds.Audio))
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

            _chains[chain.Id] = points;
            return points;
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

        private ClipPlayerNode? ClipsOf(Track track)
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

            // Only the gain or fades changed: the node keeps its streams. Anything else (or a node closed elsewhere)
            // opens the clips again. The engine never closes a node that leaves its graph, so a node kept for a muted
            // track is still open when the track plays again.
            var entry = owner._clips.GetValueOrDefault(track.Id);
            if (entry is not null && !entry.Node.IsClosed && entry.Regions.Length == regions.Length && entry.Regions.Zip(regions).All(p => p.First.SameStream(p.Second)))
            {
                if (!entry.Regions.SequenceEqual(regions))
                {
                    entry.Node.UpdateEnvelopes(regions);
                    entry = entry with { Regions = regions };
                    owner._clips[track.Id] = entry;
                }
            }
            else
            {
                if (entry is not null)
                {
                    owner._retired.Add(entry.Node);
                }

                var node = ClipPlayerNode.Open(track.Name, regions, rate, owner._blockFrames, out var problems);
                entry = new ClipEntry(regions, node, problems);
                owner._clips[track.Id] = entry;
            }

            foreach (var problem in entry.Problems)
            {
                _problems.Add($"{track.Name}: {problem}");
            }

            return entry.Node;
        }

        private string Resolve(string location) =>
            Path.IsPathRooted(location) || directory is null ? location : Path.GetFullPath(Path.Combine(directory, location));

        private PluginNode? NodeOf(DeviceInstance device)
        {
            if (owner._instances(device.Id) is not { } instance)
            {
                return null;
            }

            if (!owner._plugins.TryGetValue(device.Id, out var node) || !ReferenceEquals(node.Instance, instance))
            {
                if ((int)instance.Request.SampleRate != owner._sampleRate)
                {
                    _problems.Add($"{(device.Definition.DisplayName ?? device.Definition.Id.Value)} runs at {instance.Request.SampleRate} Hz and the audio device at {owner._sampleRate} Hz, so it is silent.");
                    return null;
                }

                if (instance.Request.MaxBlockFrames < owner._blockFrames)
                {
                    _problems.Add($"{(device.Definition.DisplayName ?? device.Definition.Id.Value)} takes at most {instance.Request.MaxBlockFrames} frames per block and the engine renders {owner._blockFrames}, so it is silent.");
                    return null;
                }

                if (owner._plugins.TryGetValue(device.Id, out var replaced))
                {
                    replaced.Instance.Changed -= owner.OnPluginChanged;
                }

                node = new PluginNode(instance, (device.Definition.DisplayName ?? device.Definition.Id.Value), owner._blockFrames);
                instance.Changed += owner.OnPluginChanged;
                owner._plugins[device.Id] = node;
            }

            var feed = signal.SoftwareInstruments.FirstOrDefault(f => f.Device == device.Id);
            var parameters = signal.Parameters.FirstOrDefault(f => f.Device == device.Id);
            // An unchanged schedule keeps its instance: replacing it would make the node look for notes to release.
            var schedule = Schedule(feed?.Events ?? [], parameters?.Changes ?? [], project.Sequence.TempoMap, owner._sampleRate);
            if (!schedule.HasSameContent(node.Schedule))
            {
                node.Schedule = schedule;
            }

            _latency[device.Id] = node.LatencyFrames;
            return node;
        }

        private void ForgetUnused()
        {
            var present = project.Chains.SelectMany(c => c.Devices).Select(d => d.Id).ToHashSet();
            foreach (var gone in owner._plugins.Keys.Where(id => !present.Contains(id)).ToList())
            {
                owner._plugins[gone].Instance.Changed -= owner.OnPluginChanged;
                owner._plugins.Remove(gone);
            }

            foreach (var gone in owner._clips.Keys.Where(id => project.Sequence.FindTrack(id) is null).ToList())
            {
                owner._retired.Add(owner._clips[gone].Node);
                owner._clips.Remove(gone);
            }
        }
    }
}
