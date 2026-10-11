namespace Bluestone.Audio;

/// <summary>
/// An immutable render graph: nodes (sources and processors), mixer strips, and the master. Built on a control
/// thread with <see cref="AudioGraphBuilder"/> and published to the engine, which swaps it in atomically between
/// blocks; the audio thread never waits for it. Rendering allocates nothing.
/// </summary>
public sealed class AudioGraph
{
    private readonly NodeEntry[] _nodes;
    private readonly StripEntry[] _strips;
    private readonly float[] _silence;

    internal AudioGraph(int blockFrames, NodeEntry[] nodes, StripEntry[] strips, MixerStripState master, float masterGain, IReadOnlyList<IStreamedSource> streams)
    {
        BlockFrames = blockFrames;
        _nodes = nodes;
        _strips = strips;
        Master = master;
        MasterGain = masterGain;
        Streams = streams;
        _silence = new float[2 * blockFrames];
    }

    /// <summary>The largest block this graph renders; its buffers are this long.</summary>
    public int BlockFrames { get; }

    public MixerStripState Master { get; }

    public float MasterGain { get; }

    /// <summary>Nodes in render order.</summary>
    public IEnumerable<AudioNode> Nodes => _nodes.Select(n => n.Node);

    /// <summary>Sources that read from disk; the engine's disk streamer services them while this graph is current.</summary>
    internal IReadOnlyList<IStreamedSource> Streams { get; }

    /// <summary>A graph that renders silence.</summary>
    public static AudioGraph Silent(int blockFrames) => new AudioGraphBuilder().Build(blockFrames, new MixerStripState());

    /// <summary>Renders one block of <see cref="BlockFrames"/> frames into <paramref name="output"/>. Audio thread.</summary>
    internal void Render(in AudioRenderContext context, StereoBuffer output)
    {
        var frames = context.Frames;
        var stride = BlockFrames;
        output.Clear();
        var silence = StereoBuffer.Of(_silence, stride, frames);
        silence.Clear();
        foreach (var strip in _strips)
        {
            StereoBuffer.Of(strip.Storage, stride, frames).Clear();
        }

        foreach (var entry in _nodes)
        {
            var input = silence;
            if (entry.Inputs.Length > 0)
            {
                input = StereoBuffer.Of(entry.InputStorage!, stride, frames);
                input.Clear();
                foreach (var source in entry.Inputs)
                {
                    input.Add(StereoBuffer.Of(_nodes[source].OutputStorage, stride, frames));
                }
            }

            var rendered = StereoBuffer.Of(entry.OutputStorage, stride, frames);
            rendered.Clear();
            entry.Node.Render(context, input, rendered);
            foreach (var strip in entry.Strips)
            {
                StereoBuffer.Of(_strips[strip].Storage, stride, frames).Add(rendered);
            }

            if (entry.ToMaster)
            {
                output.Add(rendered);
            }
        }

        foreach (var strip in _strips)
        {
            var buffer = StereoBuffer.Of(strip.Storage, stride, frames);

            // Sends read the strip's input; their gains include the fader and pan when they are post-fader.
            foreach (var send in strip.Sends)
            {
                send.Ramp.AddTo(buffer, StereoBuffer.Of(_strips[send.Destination].Storage, stride, frames), send.Left, send.Right);
            }

            strip.State.Apply(buffer, strip.Left, strip.Right);
            strip.State.Meter.Update(buffer, context.SampleRate);
            if (strip.Destination < 0)
            {
                output.Add(buffer);
            }
            else
            {
                StereoBuffer.Of(_strips[strip.Destination].Storage, stride, frames).Add(buffer);
            }
        }

        Master.Apply(output, MasterGain, MasterGain);
        Master.Meter.Update(output, context.SampleRate);
    }

    internal sealed record NodeEntry(AudioNode Node, int[] Inputs, float[]? InputStorage, float[] OutputStorage, int[] Strips, bool ToMaster);

    internal sealed record StripEntry(MixerStripState State, float Left, float Right, int Destination, float[] Storage, SendEntry[] Sends);

    internal sealed record SendEntry(GainRamp Ramp, int Destination, float Left, float Right);
}

/// <summary>
/// Assembles an <see cref="AudioGraph"/>. Nodes may feed other nodes (an instrument into an effect), mixer strips,
/// or the master; strips feed another strip (a bus) or the master, and send copies to other strips (returns).
/// Feedback is refused.
/// </summary>
public sealed class AudioGraphBuilder
{
    private readonly List<AudioNode> _nodes = [];
    private readonly List<(int From, int To)> _nodeEdges = [];
    private readonly List<(int Node, int Strip)> _toStrips = [];
    private readonly HashSet<int> _toMaster = [];
    private readonly List<(MixerStripState State, MixerStripSettings Settings)> _strips = [];
    private readonly Dictionary<int, int> _stripDestinations = [];
    private readonly List<(int Strip, int Destination, double LevelDecibels, bool PreFader)> _sends = [];
    private readonly List<IStreamedSource> _streams = [];

    /// <returns>The node's index in this builder.</returns>
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

    /// <summary>Feeds node <paramref name="from"/>'s output into node <paramref name="to"/>'s input.</summary>
    public void Connect(int from, int to)
    {
        CheckNode(from);
        CheckNode(to);
        _nodeEdges.Add((from, to));
    }

    public void ConnectToMaster(int node)
    {
        CheckNode(node);
        _toMaster.Add(node);
    }

    /// <returns>The strip's index in this builder.</returns>
    public int AddStrip(MixerStripState state, MixerStripSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        _strips.Add((state, settings));
        return _strips.Count - 1;
    }

    public void ConnectToStrip(int node, int strip)
    {
        CheckNode(node);
        CheckStrip(strip);
        _toStrips.Add((node, strip));
    }

    /// <summary>Sends strip <paramref name="strip"/> to strip <paramref name="destination"/> (a bus) instead of the master.</summary>
    public void RouteStrip(int strip, int destination)
    {
        CheckStrip(strip);
        CheckStrip(destination);
        _stripDestinations[strip] = destination;
    }

    /// <summary>
    /// Sends a copy of strip <paramref name="strip"/> to strip <paramref name="destination"/> at
    /// <paramref name="levelDecibels"/>: before the strip's fader and pan (<paramref name="preFader"/>), or after both.
    /// Mute silences a strip's sends too; see <see cref="Build"/> for solo.
    /// </summary>
    public void SendStrip(int strip, int destination, double levelDecibels, bool preFader)
    {
        CheckStrip(strip);
        CheckStrip(destination);
        if (strip == destination || _sends.Any(s => s.Strip == strip && s.Destination == destination))
        {
            throw new ArgumentException("A strip sends at most once to another strip, and never to itself.", nameof(destination));
        }

        _sends.Add((strip, destination, levelDecibels, preFader));
    }

    /// <summary>
    /// Builds the graph. Mute silences a strip and its sends. While any strip is soloed, a strip sounds if it is soloed,
    /// feeds a soloed strip through its output (a bus's members), or is reached from a soloed strip through outputs and
    /// sends (its buses and returns); a send carries audio when its strip sounds or its target is soloed (so soloing a
    /// return plays what is sent to it, without the dry channels).
    /// </summary>
    public AudioGraph Build(int blockFrames, MixerStripState master, double masterGainDecibels = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockFrames);
        ArgumentNullException.ThrowIfNull(master);
        var nodeOrder = OrderNodes();
        var stripOrder = OrderStrips();
        var nodePosition = new int[_nodes.Count];
        for (var i = 0; i < nodeOrder.Count; i++)
        {
            nodePosition[nodeOrder[i]] = i;
        }

        var stripPosition = new int[_strips.Count];
        for (var i = 0; i < stripOrder.Count; i++)
        {
            stripPosition[stripOrder[i]] = i;
        }

        var nodes = nodeOrder.Select(index =>
        {
            int[] inputs = [.. _nodeEdges.Where(e => e.To == index).Select(e => nodePosition[e.From])];
            return new AudioGraph.NodeEntry(
                _nodes[index],
                inputs,
                inputs.Length > 0 ? new float[2 * blockFrames] : null,
                new float[2 * blockFrames],
                [.. _toStrips.Where(e => e.Node == index).Select(e => stripPosition[e.Strip]).Distinct()],
                _toMaster.Contains(index));
        }).ToArray();

        var audible = Audible();
        var anySolo = _strips.Any(s => s.Settings.Soloed);
        var strips = stripOrder.Select(index =>
        {
            var (state, settings) = _strips[index];
            var fader = PanLaw.DecibelsToGain(settings.GainDecibels);
            var (left, right) = PanLaw.Gains(settings.Pan);
            var gain = audible[index] ? fader : 0f;
            var destination = _stripDestinations.TryGetValue(index, out var d) ? stripPosition[d] : -1;
            var sends = _sends.Where(s => s.Strip == index).Select(send =>
            {
                var carries = !settings.Muted && (!anySolo || audible[index] || _strips[send.Destination].Settings.Soloed);
                var level = carries ? PanLaw.DecibelsToGain(send.LevelDecibels) : 0f;
                var (sendLeft, sendRight) = send.PreFader ? (level, level) : (level * fader * left, level * fader * right);
                return new AudioGraph.SendEntry(state.SendTo(_strips[send.Destination].State), stripPosition[send.Destination], sendLeft, sendRight);
            }).ToArray();
            state.KeepSendsTo(_sends.Where(s => s.Strip == index).Select(s => _strips[s.Destination].State).ToHashSet());
            return new AudioGraph.StripEntry(state, gain * left, gain * right, destination, new float[2 * blockFrames], sends);
        }).ToArray();

        return new AudioGraph(blockFrames, nodes, strips, master, PanLaw.DecibelsToGain(masterGainDecibels), [.. _streams]);
    }

    // See Build for the rule.
    private bool[] Audible()
    {
        var audible = new bool[_strips.Count];
        var anySolo = _strips.Any(s => s.Settings.Soloed);
        for (var i = 0; i < _strips.Count; i++)
        {
            audible[i] = !anySolo || _strips[i].Settings.Soloed || Path(i).Skip(1).Any(s => _strips[s].Settings.Soloed);
        }

        if (anySolo)
        {
            for (var i = 0; i < _strips.Count; i++)
            {
                if (_strips[i].Settings.Soloed)
                {
                    foreach (var downstream in Reached(i))
                    {
                        audible[downstream] = true;
                    }
                }
            }
        }

        for (var i = 0; i < _strips.Count; i++)
        {
            audible[i] &= !_strips[i].Settings.Muted;
        }

        return audible;
    }

    // The strip and every bus after it, towards the master.
    private IEnumerable<int> Path(int strip)
    {
        var seen = new HashSet<int>();
        for (int? current = strip; current is { } at && seen.Add(at); current = _stripDestinations.TryGetValue(at, out var next) ? next : null)
        {
            yield return at;
        }
    }

    // The strip and every strip its audio reaches through outputs and sends.
    private HashSet<int> Reached(int strip)
    {
        var reached = new HashSet<int> { strip };
        var pending = new Queue<int>([strip]);
        while (pending.TryDequeue(out var at))
        {
            foreach (var next in Next(at))
            {
                if (reached.Add(next))
                {
                    pending.Enqueue(next);
                }
            }
        }

        return reached;
    }

    private IEnumerable<int> Next(int strip)
    {
        if (_stripDestinations.TryGetValue(strip, out var output))
        {
            yield return output;
        }

        foreach (var send in _sends)
        {
            if (send.Strip == strip)
            {
                yield return send.Destination;
            }
        }
    }

    private List<int> OrderNodes()
    {
        var pending = new int[_nodes.Count];
        foreach (var (_, to) in _nodeEdges)
        {
            pending[to]++;
        }

        var ready = new Queue<int>(Enumerable.Range(0, _nodes.Count).Where(i => pending[i] == 0));
        var order = new List<int>(_nodes.Count);
        while (ready.TryDequeue(out var node))
        {
            order.Add(node);
            foreach (var (from, to) in _nodeEdges)
            {
                if (from == node && --pending[to] == 0)
                {
                    ready.Enqueue(to);
                }
            }
        }

        return order.Count == _nodes.Count ? order : throw new InvalidOperationException("The audio nodes are connected in a loop.");
    }

    // Every strip renders before the strips it feeds or sends to; otherwise in the order they were added.
    private List<int> OrderStrips()
    {
        var pending = new int[_strips.Count];
        for (var i = 0; i < _strips.Count; i++)
        {
            foreach (var next in Next(i))
            {
                pending[next]++;
            }
        }

        var ready = new PriorityQueue<int, int>();
        for (var i = 0; i < _strips.Count; i++)
        {
            if (pending[i] == 0)
            {
                ready.Enqueue(i, i);
            }
        }

        var order = new List<int>(_strips.Count);
        while (ready.TryDequeue(out var strip, out _))
        {
            order.Add(strip);
            foreach (var next in Next(strip))
            {
                if (--pending[next] == 0)
                {
                    ready.Enqueue(next, next);
                }
            }
        }

        return order.Count == _strips.Count ? order : throw new InvalidOperationException("The mixer strips are routed in a loop.");
    }

    private void CheckNode(int node) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)node, (uint)_nodes.Count, nameof(node));

    private void CheckStrip(int strip) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)strip, (uint)_strips.Count, nameof(strip));
}
