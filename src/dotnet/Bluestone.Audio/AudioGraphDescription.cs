using System.Collections.Immutable;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio;

/// <summary>What kind of node a <see cref="DescribedNode"/> is.</summary>
public enum DescribedNodeKind : byte
{
    /// <summary>A track's audio clips (<see cref="ClipPlayerNode"/>).</summary>
    Clips = 1,

    /// <summary>A running plugin instrument or effect (<see cref="PluginNode"/>).</summary>
    Plugin = 2,

    /// <summary>Its input at <see cref="DescribedNode.Gain"/> (<see cref="GainNode"/>): what an Audio Receiver brings in.</summary>
    Gain = 3,
}

/// <summary>
/// A node by what it plays, not by the object that plays it: <see cref="Key"/> (a track's or a device's id) lets the
/// realizer keep the same node, with its streams, notes, and meters, from one graph to the next.
/// </summary>
/// <param name="Kind">Clips or a plugin.</param>
/// <param name="Key">The track (clips) or device (plugin) the node belongs to.</param>
/// <param name="Name">For messages: the track's or device's name.</param>
public sealed record DescribedNode(DescribedNodeKind Kind, Guid Key, string Name)
{
    /// <summary>The clips to play (<see cref="DescribedNodeKind.Clips"/>).</summary>
    public ImmutableArray<AudioClipRegion> Regions { get; init; } = [];

    /// <summary>What the plugin receives over the song (<see cref="DescribedNodeKind.Plugin"/>).</summary>
    public PluginSchedule Schedule { get; init; } = PluginSchedule.Empty;

    /// <summary>How another process reaches the plugin's instance (<see cref="DescribedNodeKind.Plugin"/>); null when not described.</summary>
    public DescribedPluginLink? Link { get; init; }

    /// <summary>The linear gain (<see cref="DescribedNodeKind.Gain"/>).</summary>
    public float Gain { get; init; } = 1;
}

/// <summary>
/// A plugin node's instance as the engine process reaches it (ADR 0044): the shape of its block exchange, the host's
/// pipeline settings, the latency Bluestone knows for it, and the exchange of the worker running it now (null while it is
/// not running: the output then follows the failure policy). A restarted worker has a new exchange; the engine attaches
/// to it when it realizes the next graph.
/// </summary>
public sealed record DescribedPluginLink(ExchangeOptions Shape, int PipelineDepth, TimeSpan CollectWait, bool BypassOnFailure, int LatencyFrames, PluginDataPlane? DataPlane);

/// <summary>A mixer strip by its channel's id, with its fader, pan, mute, and solo.</summary>
public sealed record DescribedStrip(Guid Key, MixerStripSettings Settings);

/// <summary>
/// A chain junction's meter (ADR 0037): the sum of the described nodes <see cref="Nodes"/> (indices into
/// <see cref="AudioGraphDescription.Nodes"/>), measured while the junction is watched. <see cref="Key"/> names the junction:
/// the device it follows, or the chain for its input.
/// </summary>
public sealed record DescribedMeterPoint(Guid Key, ImmutableArray<int> Nodes);

/// <summary>One call made while describing a graph, replayed in order by the realizer (see <see cref="AudioGraphDescription"/>).</summary>
public readonly record struct DescribedStep(DescribedStepKind Kind, int A, int B = 0, double Level = 0, bool PreFader = false);

public enum DescribedStepKind : byte
{
    /// <summary>Node <c>A</c> (an index into <see cref="AudioGraphDescription.Nodes"/>) is added.</summary>
    AddNode = 1,

    /// <summary>Node <c>A</c> feeds node <c>B</c>.</summary>
    Connect = 2,

    /// <summary>Node <c>A</c> is monitored live (<see cref="AudioGraphBuilder.Monitor"/>).</summary>
    Monitor = 3,

    /// <summary>Node <c>A</c> feeds strip <c>B</c>.</summary>
    ConnectToStrip = 4,

    /// <summary>Node <c>A</c> feeds the master.</summary>
    ConnectToMaster = 5,

    /// <summary>Strip <c>A</c> goes to strip <c>B</c> instead of the master.</summary>
    RouteStrip = 6,

    /// <summary>Strip <c>A</c> sends to strip <c>B</c> at <c>Level</c> dB, before or after its fader.</summary>
    SendStrip = 7,
}

/// <summary>
/// A render graph as plain data (ADR 0044): which nodes, strips, and connections, but none of the objects that render
/// them. The UI process describes the project's graph; the engine realizes it with <see cref="AudioGraphRealizer"/>,
/// which creates or reuses the nodes and strip states and builds the <see cref="AudioGraph"/>. The steps are the
/// builder calls in the order they were made, so the realized graph is exactly the one an
/// <see cref="AudioGraphBuilder"/> given the same calls would build.
/// </summary>
/// <param name="Nodes">Every node the steps add, by index.</param>
/// <param name="Strips">The mixer strips, in the order they are added.</param>
/// <param name="Steps">The calls, in order.</param>
/// <param name="MasterGainDecibels">The master fader.</param>
/// <param name="Retained">
/// Tracks and devices whose nodes are kept even when this graph does not play them (a muted track's clips stay open,
/// so unmuting does not reopen its files). Nodes of anything else are let go.
/// </param>
public sealed record AudioGraphDescription(
    ImmutableArray<DescribedNode> Nodes,
    ImmutableArray<DescribedStrip> Strips,
    ImmutableArray<DescribedStep> Steps,
    double MasterGainDecibels,
    ImmutableHashSet<Guid> Retained)
{
    public static AudioGraphDescription Empty { get; } = new([], [], [], 0, []);

    /// <summary>The chain junctions that can be metered, at most one per key.</summary>
    public ImmutableArray<DescribedMeterPoint> MeterPoints { get; init; } = [];
}

/// <summary>
/// Records an <see cref="AudioGraphDescription"/> with the same calls as <see cref="AudioGraphBuilder"/>, checking the
/// same things (indices in range, no strip sending twice or to itself) at the same time, so a description that was
/// recorded realizes without errors.
/// </summary>
public sealed class AudioGraphDescriptionBuilder
{
    private readonly List<DescribedNode> _nodes = [];
    private readonly List<DescribedStrip> _strips = [];
    private readonly List<DescribedStep> _steps = [];
    private readonly HashSet<(int Strip, int Destination)> _sends = [];
    private readonly List<DescribedMeterPoint> _meterPoints = [];
    private readonly HashSet<Guid> _meterKeys = [];

    /// <returns>The node's index.</returns>
    public int AddNode(DescribedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Add(node);
        _steps.Add(new(DescribedStepKind.AddNode, _nodes.Count - 1));
        return _nodes.Count - 1;
    }

    /// <summary>The key of the node at <paramref name="index"/>, which stays the same between descriptions of the same thing.</summary>
    public Guid KeyOf(int index) => _nodes[index].Key;

    public void Connect(int from, int to)
    {
        CheckNode(from);
        CheckNode(to);
        _steps.Add(new(DescribedStepKind.Connect, from, to));
    }

    public void Monitor(int node)
    {
        CheckNode(node);
        _steps.Add(new(DescribedStepKind.Monitor, node));
    }

    public void ConnectToMaster(int node)
    {
        CheckNode(node);
        _steps.Add(new(DescribedStepKind.ConnectToMaster, node));
    }

    /// <returns>The strip's index.</returns>
    public int AddStrip(DescribedStrip strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        _strips.Add(strip);
        return _strips.Count - 1;
    }

    public void ConnectToStrip(int node, int strip)
    {
        CheckNode(node);
        CheckStrip(strip);
        _steps.Add(new(DescribedStepKind.ConnectToStrip, node, strip));
    }

    public void RouteStrip(int strip, int destination)
    {
        CheckStrip(strip);
        CheckStrip(destination);
        _steps.Add(new(DescribedStepKind.RouteStrip, strip, destination));
    }

    public void SendStrip(int strip, int destination, double levelDecibels, bool preFader)
    {
        CheckStrip(strip);
        CheckStrip(destination);
        if (strip == destination || !_sends.Add((strip, destination)))
        {
            throw new ArgumentException("A strip sends at most once to another strip, and never to itself.", nameof(destination));
        }

        _steps.Add(new(DescribedStepKind.SendStrip, strip, destination, levelDecibels, preFader));
    }

    /// <summary>A junction's meter over the sum of <paramref name="nodes"/> (see <see cref="DescribedMeterPoint"/>).</summary>
    public void AddMeterPoint(Guid key, IEnumerable<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ImmutableArray<int> members = [.. nodes.Distinct()];
        foreach (var node in members)
        {
            CheckNode(node);
        }

        if (!_meterKeys.Add(key))
        {
            throw new ArgumentException($"Meter point {key} is described twice.", nameof(key));
        }

        _meterPoints.Add(new DescribedMeterPoint(key, members));
    }

    public AudioGraphDescription Build(double masterGainDecibels, IEnumerable<Guid> retained) =>
        new([.. _nodes], [.. _strips], [.. _steps], masterGainDecibels, [.. retained]) { MeterPoints = [.. _meterPoints] };

    private void CheckNode(int node) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)node, (uint)_nodes.Count, nameof(node));

    private void CheckStrip(int strip) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)strip, (uint)_strips.Count, nameof(strip));
}
