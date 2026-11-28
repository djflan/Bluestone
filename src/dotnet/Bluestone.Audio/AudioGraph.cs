using System.Collections.Immutable;

namespace Bluestone.Audio;

/// <summary>
/// An immutable render graph built on the control thread and rendered without allocation or locks on the audio thread.
/// Nodes and strips share one topological schedule so strip outputs can feed track chains and the Master chain.
/// </summary>
public sealed class AudioGraph
{
    private readonly NodeEntry[] _nodes;
    private readonly StripEntry[] _strips;
    private readonly StageEntry[] _renderOrder;
    private readonly MeterEntry[] _meterPoints;
    private readonly float[] _silence;
    private readonly float[] _delayed;
    private readonly float[] _metered;

    internal AudioGraph(
        int blockFrames,
        NodeEntry[] nodes,
        StripEntry[] strips,
        StageEntry[] renderOrder,
        MixerStripState master,
        float masterGain,
        double masterGainDecibels,
        double masterPan,
        bool masterAudible,
        DescribedAutomationPoint[] masterGainAutomation,
        DescribedAutomationPoint[] masterPanAutomation,
        float masterLeft,
        float masterRight,
        IReadOnlyList<IStreamedSource> streams,
        int latencyFrames,
        MeterEntry[]? meterPoints = null)
    {
        BlockFrames = blockFrames;
        _nodes = nodes;
        _strips = strips;
        _renderOrder = renderOrder;
        _meterPoints = meterPoints ?? [];
        _metered = _meterPoints.Length > 0 ? new float[2 * blockFrames] : [];
        Master = master;
        MasterGain = masterGain;
        MasterGainDecibels = masterGainDecibels;
        MasterPan = masterPan;
        MasterAudible = masterAudible;
        MasterGainAutomation = masterGainAutomation;
        MasterPanAutomation = masterPanAutomation;
        MasterLeft = masterLeft;
        MasterRight = masterRight;
        Streams = streams;
        LatencyFrames = latencyFrames;
        _silence = new float[2 * blockFrames];
        _delayed = new float[2 * blockFrames];
    }

    /// <summary>Frames between the song position a block renders and the song position the master puts out.</summary>
    public int LatencyFrames { get; }

    /// <summary>Whether a node now reports another latency than the one this graph was built for.</summary>
    public bool IsLatencyStale
    {
        get
        {
            foreach (var entry in _nodes)
            {
                if (entry.Node.LatencyFrames != entry.BuiltLatency)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Frames from <paramref name="node"/>'s output until the master puts it out, or null if absent.</summary>
    public int? DelayToMasterOf(AudioNode node)
    {
        foreach (var entry in _nodes)
        {
            if (ReferenceEquals(entry.Node, node))
            {
                return entry.DelayToMaster;
            }
        }

        return null;
    }

    /// <summary>The largest block this graph renders.</summary>
    public int BlockFrames { get; }

    public MixerStripState Master { get; }

    public float MasterGain { get; }

    private double MasterGainDecibels { get; }

    private double MasterPan { get; }

    private bool MasterAudible { get; }

    private DescribedAutomationPoint[] MasterGainAutomation { get; }

    private DescribedAutomationPoint[] MasterPanAutomation { get; }

    internal float MasterLeft { get; }

    internal float MasterRight { get; }

    /// <summary>Nodes in graph order.</summary>
    public IEnumerable<AudioNode> Nodes => _renderOrder.Where(stage => !stage.IsStrip).Select(stage => _nodes[stage.Index].Node);

    internal IReadOnlyList<IStreamedSource> Streams { get; }

    public static AudioGraph Silent(int blockFrames) => new AudioGraphBuilder().Build(blockFrames, new MixerStripState());

    /// <summary>Prepares each mixer's immutable sample-rate DSP state before the graph is published or rendered.</summary>
    internal void PrepareMeters(int sampleRate)
    {
        Master.Meter.Prepare(sampleRate);
        foreach (var strip in _strips)
        {
            strip.State.Meter.Prepare(sampleRate);
        }
    }

    /// <summary>Renders one block. Audio thread; all storage is allocated while building the graph.</summary>
    internal void Render(in AudioRenderContext context, StereoBuffer output, AudioClickRenderer? audioClick = null)
    {
        var frames = context.Frames;
        var stride = BlockFrames;
        output.Clear();
        var silence = StereoBuffer.Of(_silence, stride, frames);
        silence.Clear();

        foreach (var node in _nodes)
        {
            StereoBuffer.Of(node.OutputStorage, stride, frames).Clear();
            if (node.InputStorage is { } input)
            {
                StereoBuffer.Of(input, stride, frames).Clear();
            }

            if (node.SidechainStorage is { } sidechain)
            {
                StereoBuffer.Of(sidechain, stride, frames).Clear();
            }
        }

        foreach (var strip in _strips)
        {
            StereoBuffer.Of(strip.Storage, stride, frames).Clear();
            StereoBuffer.Of(strip.PreFaderStorage, stride, frames).Clear();
        }

        foreach (var stage in _renderOrder)
        {
            if (!stage.IsStrip)
            {
                var entry = _nodes[stage.Index];
                var input = entry.InputStorage is { } inputStorage ? StereoBuffer.Of(inputStorage, stride, frames) : silence;
                foreach (var source in entry.Inputs)
                {
                    input.Add(Delayed(StereoBuffer.Of(_nodes[source.Index].OutputStorage, stride, frames), source.Delay, frames));
                }

                foreach (var source in entry.StripInputs)
                {
                    var sourceBuffer = _strips[source.Index].RouteAudible
                        ? StereoBuffer.Of(_strips[source.Index].Storage, stride, frames)
                        : silence;
                    input.Add(Delayed(sourceBuffer, source.Delay, frames));
                }

                foreach (var send in entry.SendInputs)
                {
                    var source = _strips[send.SourceStrip];
                    var buffer = send.PreFader ? StereoBuffer.Of(source.PreFaderStorage, stride, frames) : StereoBuffer.Of(source.Storage, stride, frames);
                    send.Ramp.AddTo(Delayed(buffer, send.Delay, frames), input, send.Left, send.Right);
                }

                var sidechain = entry.SidechainStorage is { } sidechainStorage ? StereoBuffer.Of(sidechainStorage, stride, frames) : default;
                foreach (var source in entry.SidechainInputs)
                {
                    sidechain.Add(Delayed(StereoBuffer.Of(_nodes[source.Index].OutputStorage, stride, frames), source.Delay, frames));
                }

                var rendered = StereoBuffer.Of(entry.OutputStorage, stride, frames);
                entry.Node.Render(context, input, sidechain, rendered);
                if (entry.ToMaster)
                {
                    output.Add(Delayed(rendered, entry.MasterDelay, frames));
                }
            }
            else
            {
                var strip = _strips[stage.Index];
                var buffer = StereoBuffer.Of(strip.Storage, stride, frames);
                foreach (var source in strip.NodeInputs)
                {
                    buffer.Add(Delayed(StereoBuffer.Of(_nodes[source.Index].OutputStorage, stride, frames), source.Delay, frames));
                }

                foreach (var source in strip.StripInputs)
                {
                    var sourceBuffer = _strips[source.Index].RouteAudible
                        ? StereoBuffer.Of(_strips[source.Index].Storage, stride, frames)
                        : silence;
                    buffer.Add(Delayed(sourceBuffer, source.Delay, frames));
                }

                foreach (var send in strip.SendInputs)
                {
                    var source = _strips[send.SourceStrip];
                    var sourceBuffer = send.PreFader ? StereoBuffer.Of(source.PreFaderStorage, stride, frames) : StereoBuffer.Of(source.Storage, stride, frames);
                    send.Ramp.AddTo(Delayed(sourceBuffer, send.Delay, frames), buffer, send.Left, send.Right);
                }

                var preFader = StereoBuffer.Of(strip.PreFaderStorage, stride, frames);
                for (var i = 0; i < frames; i++)
                {
                    preFader.Left[i] = buffer.Left[i];
                    preFader.Right[i] = buffer.Right[i];
                }

                strip.State.ApplyAutomated(buffer, context, strip.GainDecibels, strip.Pan, strip.Unmuted, strip.GainAutomation, strip.PanAutomation);
                strip.State.Meter.Update(buffer, context);
                if (strip.ToMaster)
                {
                    output.Add(Delayed(strip.RouteAudible ? buffer : silence, strip.MasterDelay, frames));
                }
            }
        }

        foreach (var point in _meterPoints)
        {
            if (point.Meter.IsWatched)
            {
                var sum = StereoBuffer.Of(_metered, stride, frames);
                sum.Clear();
                foreach (var node in point.Nodes)
                {
                    sum.Add(StereoBuffer.Of(_nodes[node].OutputStorage, stride, frames));
                }

                point.Meter.Update(sum, context.SampleRate);
            }
        }

        if (audioClick is not null)
        {
            audioClick.Render(context, output);
        }

        Master.ApplyAutomated(output, context, MasterGainDecibels, MasterPan, MasterAudible, MasterGainAutomation, MasterPanAutomation);
        Master.Meter.Update(output, context);
    }

    private StereoBuffer Delayed(StereoBuffer signal, in EdgeDelay delay, int frames)
    {
        if (delay.Line is not { } line)
        {
            return signal;
        }

        var delayed = StereoBuffer.Of(_delayed, BlockFrames, frames);
        line.Process(signal, delayed, delay.Frames);
        return delayed;
    }

    internal readonly record struct EdgeDelay(DelayLine? Line, int Frames);

    internal readonly record struct InputEdge(bool IsStrip, int Index, EdgeDelay Delay);

    internal sealed record SendEntry(int SourceStrip, GainRamp Ramp, float Left, float Right, bool PreFader, EdgeDelay Delay);

    internal readonly record struct StageEntry(bool IsStrip, int Index);

    internal sealed record MeterEntry(AudioMeter Meter, int[] Nodes);

    internal sealed record NodeEntry(
        AudioNode Node,
        InputEdge[] Inputs,
        InputEdge[] StripInputs,
        SendEntry[] SendInputs,
        InputEdge[] SidechainInputs,
        float[]? InputStorage,
        float[]? SidechainStorage,
        float[] OutputStorage,
        bool ToMaster,
        EdgeDelay MasterDelay,
        int BuiltLatency,
        int DelayToMaster);

    internal sealed record StripEntry(
        MixerStripState State,
        float[] Storage,
        float[] PreFaderStorage,
        double GainDecibels,
        double Pan,
        bool RouteAudible,
        bool Unmuted,
        DescribedAutomationPoint[] GainAutomation,
        DescribedAutomationPoint[] PanAutomation,
        InputEdge[] NodeInputs,
        InputEdge[] StripInputs,
        SendEntry[] SendInputs,
        bool ToMaster,
        EdgeDelay MasterDelay);
}

/// <summary>Assembles a node/strip graph on the control thread. Feedback is refused.</summary>
public sealed class AudioGraphBuilder
{
    private readonly List<AudioNode> _nodes = [];
    private readonly List<(int From, int To, bool Sidechain)> _nodeEdges = [];
    private readonly List<(int Node, int Strip)> _toStrips = [];
    private readonly HashSet<int> _toMaster = [];
    private readonly List<(MixerStripState State, MixerStripSettings Settings, DescribedAutomationPoint[] GainAutomation, DescribedAutomationPoint[] PanAutomation)> _strips = [];
    private readonly Dictionary<int, RouteTarget> _stripOutputs = [];
    private readonly HashSet<int> _unrouted = [];
    private readonly List<SendRoute> _sends = [];
    private readonly List<IStreamedSource> _streams = [];
    private readonly HashSet<int> _monitored = [];
    private readonly List<(AudioMeter Meter, int[] Nodes)> _meterPoints = [];

    public int AddNode(AudioNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Add(node);
        if (node is IStreamedSource streamed)
        {
            _streams.Add(streamed);
        }

        return _nodes.Count - 1;
    }

    public void Connect(int from, int to)
    {
        CheckNode(from);
        CheckNode(to);
        _nodeEdges.Add((from, to, false));
    }

    public void ConnectSidechain(int from, int to)
    {
        CheckNode(from);
        CheckNode(to);
        _nodeEdges.Add((from, to, true));
    }

    public void Monitor(int node)
    {
        CheckNode(node);
        _monitored.Add(node);
    }

    public void ConnectToMaster(int node)
    {
        CheckNode(node);
        _toMaster.Add(node);
    }

    public void AddMeterPoint(AudioMeter meter, IEnumerable<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(nodes);
        int[] members = [.. nodes.Distinct()];
        foreach (var node in members)
        {
            CheckNode(node);
        }

        _meterPoints.Add((meter, members));
    }

    public int AddStrip(
        MixerStripState state,
        MixerStripSettings settings,
        ImmutableArray<DescribedAutomationPoint> gainAutomation = default,
        ImmutableArray<DescribedAutomationPoint> panAutomation = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        _strips.Add((state, settings, gainAutomation.IsDefault ? [] : gainAutomation.ToArray(), panAutomation.IsDefault ? [] : panAutomation.ToArray()));
        return _strips.Count - 1;
    }

    public void ConnectToStrip(int node, int strip)
    {
        CheckNode(node);
        CheckStrip(strip);
        _toStrips.Add((node, strip));
    }

    public void RouteStrip(int strip, int destination)
    {
        CheckStrip(strip);
        CheckStrip(destination);
        SetOutput(strip, new RouteTarget(false, destination));
    }

    public void RouteStripToNode(int strip, int node)
    {
        CheckStrip(strip);
        CheckNode(node);
        SetOutput(strip, new RouteTarget(true, node));
    }

    public void RouteStripToMaster(int strip)
    {
        CheckStrip(strip);
        SetOutput(strip, new RouteTarget(true, -1));
    }

    public void UnrouteStrip(int strip)
    {
        CheckStrip(strip);
        if (_stripOutputs.ContainsKey(strip) || !_unrouted.Add(strip))
        {
            throw new ArgumentException("A strip has at most one ordinary output.", nameof(strip));
        }
    }

    public void SendStrip(int strip, int destination, double levelDecibels, bool preFader)
    {
        CheckStrip(strip);
        CheckStrip(destination);
        AddSend(new SendRoute(strip, false, destination, levelDecibels, preFader));
    }

    public void SendStripToNode(int strip, int node, double levelDecibels, bool preFader)
    {
        CheckStrip(strip);
        CheckNode(node);
        AddSend(new SendRoute(strip, true, node, levelDecibels, preFader));
    }

    /// <summary>
    /// Builds the immutable graph. Every route and send is an edge in one mixed node/strip schedule. Delay lines,
    /// buffers, send ramps, and stage ordering are prepared here, never in Render.
    /// </summary>
    public AudioGraph Build(
        int blockFrames,
        MixerStripState master,
        double masterGainDecibels = 0,
        LatencyCompensation? compensation = null,
        double masterPan = 0,
        bool masterMuted = false,
        ImmutableArray<DescribedAutomationPoint> masterGainAutomation = default,
        ImmutableArray<DescribedAutomationPoint> masterPanAutomation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockFrames);
        ArgumentNullException.ThrowIfNull(master);
        compensation ??= new LatencyCompensation();
        compensation.BeginBuild();

        var nodeCount = _nodes.Count;
        var stripCount = _strips.Count;
        var vertexCount = nodeCount + stripCount;
        var edges = AllEdges();
        var order = TopologicalOrder(vertexCount, edges);
        int StripVertex(int strip) => nodeCount + strip;
        bool IsStrip(int vertex) => vertex >= nodeCount;
        int LocalIndex(int vertex) => IsStrip(vertex) ? vertex - nodeCount : vertex;

        var built = _nodes.Select(n => n.LatencyFrames).ToArray();
        var monitored = new bool[vertexCount];
        var inputLatency = new int[vertexCount];
        var outputLatency = new int[vertexCount];
        foreach (var vertex in order)
        {
            var incoming = edges.Where(e => e.To == vertex).ToArray();
            monitored[vertex] = (!IsStrip(vertex) && _monitored.Contains(vertex))
                || incoming.Any(e => !e.Sidechain && monitored[e.From]);
            inputLatency[vertex] = incoming.Where(e => !monitored[e.From]).Select(e => outputLatency[e.From]).DefaultIfEmpty(0).Max();
            outputLatency[vertex] = inputLatency[vertex] + (IsStrip(vertex) ? 0 : built[vertex]);
        }

        var masterInputs = new List<int>();
        masterInputs.AddRange(_toMaster);
        for (var s = 0; s < stripCount; s++)
        {
            if (OutputOf(s).IsMaster)
            {
                masterInputs.Add(StripVertex(s));
            }
        }

        var latency = masterInputs.Where(v => !monitored[v]).Select(v => outputLatency[v]).DefaultIfEmpty(0).Max();
        var (audible, soloNodeInputs) = Audible(edges, nodeCount, stripCount);
        var anySolo = _strips.Any(s => s.Settings.Soloed);
        var delayToMaster = DelayToMaster(order, edges, vertexCount, nodeCount, stripCount, built, outputLatency, monitored, latency);

        AudioGraph.EdgeDelay Edge(object source, object destination, int kind, int frames)
        {
            var delay = Math.Max(0, frames);
            var line = compensation.LineFor(source, destination, kind, delay, blockFrames);
            return new AudioGraph.EdgeDelay(line, line is null ? 0 : delay);
        }

        var nodeEntries = new AudioGraph.NodeEntry[nodeCount];
        for (var n = 0; n < nodeCount; n++)
        {
            var node = _nodes[n];
            var normalNodes = _nodeEdges.Where(e => e.To == n && !e.Sidechain)
                .Select(e => new AudioGraph.InputEdge(false, e.From, Edge(_nodes[e.From], node, 0, ArrivalDelay(e.From, n))))
                .ToArray();
            var stripInputs = _stripOutputs.Where(p => p.Value.IsNode && p.Value.Index == n)
                .Select(p => new AudioGraph.InputEdge(true, p.Key, Edge(_strips[p.Key].State, node, 0, ArrivalDelay(StripVertex(p.Key), n))))
                .ToArray();
            var sends = _sends.Where(s => s.IsNode && s.Destination == n)
                .Select(s => CreateSend(s, node, n))
                .ToArray();
            var sidechain = _nodeEdges.Where(e => e.To == n && e.Sidechain)
                .Select(e => new AudioGraph.InputEdge(false, e.From, Edge(_nodes[e.From], node, 2, monitored[n] ? 0 : inputLatency[n] - outputLatency[e.From])))
                .ToArray();
            var toMaster = _toMaster.Contains(n);
            var masterDelay = toMaster ? Edge(node, master, 0, monitored[n] ? 0 : latency - outputLatency[n]) : default;
            var hasInput = normalNodes.Length + stripInputs.Length + sends.Length > 0;
            nodeEntries[n] = new AudioGraph.NodeEntry(
                node,
                normalNodes,
                stripInputs,
                sends,
                sidechain,
                hasInput ? new float[2 * blockFrames] : null,
                sidechain.Length > 0 ? new float[2 * blockFrames] : null,
                new float[2 * blockFrames],
                toMaster,
                masterDelay,
                built[n],
                delayToMaster[n]);
        }

        var stripEntries = new AudioGraph.StripEntry[stripCount];
        for (var s = 0; s < stripCount; s++)
        {
            var (state, settings, gainAutomation, panAutomation) = _strips[s];
            var nodeInputs = _toStrips.Where(e => e.Strip == s)
                .Select(e => new AudioGraph.InputEdge(false, e.Node, Edge(_nodes[e.Node], state, 0, ArrivalDelay(e.Node, StripVertex(s)))))
                .ToArray();
            var stripInputs = _stripOutputs.Where(p => !p.Value.IsNode && p.Value.Index == s)
                .Select(p => new AudioGraph.InputEdge(true, p.Key, Edge(_strips[p.Key].State, state, 0, ArrivalDelay(StripVertex(p.Key), StripVertex(s)))))
                .ToArray();
            var sends = _sends.Where(send => !send.IsNode && send.Destination == s)
                .Select(send => CreateSend(send, state, StripVertex(s)))
                .ToArray();
            var target = OutputOf(s);
            var toMaster = target.IsMaster;
            var masterDelay = toMaster ? Edge(state, master, 0, monitored[StripVertex(s)] ? 0 : latency - outputLatency[StripVertex(s)]) : default;

            state.KeepSendsTo(_sends.Where(send => send.SourceStrip == s)
                .Select(send => send.IsNode ? (object)_nodes[send.Destination] : _strips[send.Destination].State).ToHashSet());
            stripEntries[s] = new AudioGraph.StripEntry(
                state,
                new float[2 * blockFrames],
                new float[2 * blockFrames],
                settings.GainDecibels,
                settings.Pan,
                audible[s],
                !settings.Muted,
                gainAutomation,
                panAutomation,
                nodeInputs,
                stripInputs,
                sends,
                toMaster,
                masterDelay);
        }

        var renderOrder = order.Select(vertex => new AudioGraph.StageEntry(IsStrip(vertex), LocalIndex(vertex))).ToArray();
        AudioGraph.MeterEntry[] meterPoints = [.. _meterPoints.Select(p => new AudioGraph.MeterEntry(p.Meter, p.Nodes))];
        compensation.EndBuild();

        var masterGain = PanLaw.DecibelsToGain(masterGainDecibels);
        var (masterPanLeft, masterPanRight) = PanLaw.Gains(masterPan);
        var masterLevel = masterMuted ? 0 : masterGain;
        return new AudioGraph(
            blockFrames,
            nodeEntries,
            stripEntries,
            renderOrder,
            master,
            masterGain,
            masterGainDecibels,
            masterPan,
            !masterMuted,
            masterGainAutomation.IsDefault ? [] : masterGainAutomation.ToArray(),
            masterPanAutomation.IsDefault ? [] : masterPanAutomation.ToArray(),
            masterLevel * masterPanLeft,
            masterLevel * masterPanRight,
            [.. _streams],
            latency,
            meterPoints);

        int ArrivalDelay(int source, int destination)
        {
            var due = IsStrip(destination) && monitored[destination] ? latency : inputLatency[destination];
            return monitored[source] ? 0 : due - outputLatency[source];
        }

        AudioGraph.SendEntry CreateSend(SendRoute send, object destination, int destinationVertex)
        {
            var settings = _strips[send.SourceStrip].Settings;
            var reachesSoloedEffect = send.IsNode
                ? soloNodeInputs.Contains(send.Destination)
                : _strips[send.Destination].Settings.Soloed;
            var carries = !settings.Muted && (!anySolo || audible[send.SourceStrip] || reachesSoloedEffect);
            var level = carries ? PanLaw.DecibelsToGain(send.LevelDecibels) : 0f;
            var sendLeft = level;
            var sendRight = level;
            var sourceState = _strips[send.SourceStrip].State;
            object targetState = send.IsNode ? _nodes[send.Destination] : _strips[send.Destination].State;
            var frames = ArrivalDelay(StripVertex(send.SourceStrip), destinationVertex);
            return new AudioGraph.SendEntry(send.SourceStrip, sourceState.SendTo(targetState), sendLeft, sendRight, send.PreFader,
                Edge(sourceState, destination, 1, frames));
        }
    }

    private static int[] TopologicalOrder(int vertexCount, List<GraphEdge> edges)
    {
        var pending = new int[vertexCount];
        foreach (var edge in edges)
        {
            pending[edge.To]++;
        }

        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < pending.Length; i++)
        {
            if (pending[i] == 0)
            {
                ready.Enqueue(i, i);
            }
        }

        var order = new int[vertexCount];
        var count = 0;
        while (ready.TryDequeue(out var vertex, out _))
        {
            order[count++] = vertex;
            foreach (var edge in edges)
            {
                if (edge.From == vertex && --pending[edge.To] == 0)
                {
                    ready.Enqueue(edge.To, edge.To);
                }
            }
        }

        return count == vertexCount ? order : throw new InvalidOperationException("The audio graph contains a routing loop.");
    }

    private List<GraphEdge> AllEdges()
    {
        var edges = new List<GraphEdge>(_nodeEdges.Count + _toStrips.Count + _stripOutputs.Count + _sends.Count);
        foreach (var (from, to, sidechain) in _nodeEdges)
        {
            edges.Add(new GraphEdge(from, to, sidechain));
        }

        foreach (var (node, strip) in _toStrips)
        {
            edges.Add(new GraphEdge(node, _nodes.Count + strip, false));
        }

        foreach (var output in _stripOutputs)
        {
            if (output.Value.IsMaster)
            {
                continue;
            }

            var destination = output.Value.IsNode ? output.Value.Index : _nodes.Count + output.Value.Index;
            edges.Add(new GraphEdge(_nodes.Count + output.Key, destination, false));
        }

        foreach (var send in _sends)
        {
            var destination = send.IsNode ? send.Destination : _nodes.Count + send.Destination;
            edges.Add(new GraphEdge(_nodes.Count + send.SourceStrip, destination, false, true));
        }

        return edges;
    }

    private (bool[] Strips, HashSet<int> NodesLeadingToSoloedStrips) Audible(List<GraphEdge> edges, int nodeCount, int stripCount)
    {
        var audible = Enumerable.Repeat(!_strips.Any(s => s.Settings.Soloed), stripCount).ToArray();
        var soloed = Enumerable.Range(0, stripCount).Where(i => _strips[i].Settings.Soloed).Select(i => nodeCount + i).ToArray();
        var soloNodeInputs = new HashSet<int>();
        if (soloed.Length > 0)
        {
            var all = new HashSet<int>();
            foreach (var solo in soloed)
            {
                var forward = Reach(solo, edges.Where(e => !e.Sidechain).ToList(), nodeCount + stripCount, false);
                var backward = Reach(solo, edges.Where(e => !e.Sidechain && !e.Send).ToList(), nodeCount + stripCount, true);
                all.UnionWith(forward);
                all.UnionWith(backward);
                soloNodeInputs.UnionWith(backward.Where(vertex => vertex < nodeCount));
            }

            for (var i = 0; i < stripCount; i++)
            {
                audible[i] = all.Contains(nodeCount + i);
            }
        }

        return (audible, soloNodeInputs);
    }

    private static HashSet<int> Reach(int start, List<GraphEdge> edges, int count, bool reverse)
    {
        var reached = new HashSet<int> { start };
        var pending = new Queue<int>();
        pending.Enqueue(start);
        while (pending.TryDequeue(out var current))
        {
            foreach (var edge in edges)
            {
                var from = reverse ? edge.To : edge.From;
                if (from != current)
                {
                    continue;
                }

                var next = reverse ? edge.From : edge.To;
                if ((uint)next < (uint)count && reached.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return reached;
    }

    private static int[] DelayToMaster(
        int[] order,
        List<GraphEdge> edges,
        int vertexCount,
        int nodeCount,
        int stripCount,
        int[] nodeLatency,
        int[] outputLatency,
        bool[] monitored,
        int latency)
    {
        _ = vertexCount;
        _ = stripCount;
        var distance = new int[nodeCount + stripCount];
        for (var i = order.Length - 1; i >= 0; i--)
        {
            var vertex = order[i];
            var downstream = edges.Where(e => e.From == vertex && !e.Sidechain).Select(e =>
                (e.To < nodeCount ? nodeLatency[e.To] : 0) + distance[e.To]).DefaultIfEmpty(0).Max();
            distance[vertex] = downstream;
        }

        var result = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            result[i] = monitored[i] ? distance[i] : Math.Max(0, latency - outputLatency[i]);
        }

        return result;
    }

    private RouteTarget OutputOf(int strip) => _stripOutputs.TryGetValue(strip, out var output)
        ? output
        : _unrouted.Contains(strip) ? new RouteTarget(true, -2) : new RouteTarget(true, -1);

    private void SetOutput(int strip, RouteTarget output)
    {
        if (_stripOutputs.ContainsKey(strip) || _unrouted.Contains(strip))
        {
            throw new ArgumentException("A strip has at most one ordinary output.", nameof(strip));
        }

        _stripOutputs.Add(strip, output);
    }

    private void AddSend(SendRoute send)
    {
        if (send.IsNode && _sends.Any(s => s.SourceStrip == send.SourceStrip && s.IsNode && s.Destination == send.Destination)
            || !send.IsNode && (send.SourceStrip == send.Destination || _sends.Any(s => s.SourceStrip == send.SourceStrip && !s.IsNode && s.Destination == send.Destination)))
        {
            throw new ArgumentException("A strip sends at most once to a destination, and never to itself.");
        }

        _sends.Add(send);
    }

    private void CheckNode(int node) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)node, (uint)_nodes.Count, nameof(node));

    private void CheckStrip(int strip) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)strip, (uint)_strips.Count, nameof(strip));

    private readonly record struct GraphEdge(int From, int To, bool Sidechain, bool Send = false);

    private readonly record struct RouteTarget(bool IsNode, int Index)
    {
        public bool IsMaster => IsNode && Index == -1;
    }

    private readonly record struct SendRoute(int SourceStrip, bool IsNode, int Destination, double LevelDecibels, bool PreFader);
}
