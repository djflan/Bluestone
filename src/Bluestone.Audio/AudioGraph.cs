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

    internal sealed record StripEntry(MixerStripState State, float Left, float Right, int Destination, float[] Storage);
}

/// <summary>
/// Assembles an <see cref="AudioGraph"/>. Nodes may feed other nodes (an instrument into an effect), mixer strips,
/// or the master; strips feed another strip (a bus) or the master. Feedback is refused.
/// </summary>
public sealed class AudioGraphBuilder
{
    private readonly List<AudioNode> _nodes = [];
    private readonly List<(int From, int To)> _nodeEdges = [];
    private readonly List<(int Node, int Strip)> _toStrips = [];
    private readonly HashSet<int> _toMaster = [];
    private readonly List<(MixerStripState State, MixerStripSettings Settings)> _strips = [];
    private readonly Dictionary<int, int> _stripDestinations = [];
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
        var strips = stripOrder.Select(index =>
        {
            var (state, settings) = _strips[index];
            var gain = audible[index] ? PanLaw.DecibelsToGain(settings.GainDecibels) : 0f;
            var (left, right) = PanLaw.Gains(settings.Pan);
            var destination = _stripDestinations.TryGetValue(index, out var d) ? stripPosition[d] : -1;
            return new AudioGraph.StripEntry(state, gain * left, gain * right, destination, new float[2 * blockFrames]);
        }).ToArray();

        return new AudioGraph(blockFrames, nodes, strips, master, PanLaw.DecibelsToGain(masterGainDecibels), [.. _streams]);
    }

    // Mute silences a strip. While any strip is soloed, a strip sounds only if it is soloed, feeds a soloed strip
    // (directly or through buses), or is a bus a soloed strip reaches.
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
                    foreach (var downstream in Path(i))
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

    private List<int> OrderStrips()
    {
        var depth = new int[_strips.Count];
        for (var i = 0; i < _strips.Count; i++)
        {
            var hops = Path(i).Count();
            var last = Path(i).Last();
            if (_stripDestinations.ContainsKey(last))
            {
                throw new InvalidOperationException("The mixer strips are routed in a loop.");
            }

            depth[i] = hops;
        }

        // Deeper strips feed shallower ones, so they render first; ties keep the order they were added.
        return [.. Enumerable.Range(0, _strips.Count).OrderByDescending(i => depth[i])];
    }

    private void CheckNode(int node) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)node, (uint)_nodes.Count, nameof(node));

    private void CheckStrip(int strip) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)strip, (uint)_strips.Count, nameof(strip));
}
