using System.Collections.Immutable;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Plugins;

namespace Bluestone.Audio;

/// <summary>What kind of node a <see cref="DescribedNode"/> is.</summary>
public enum DescribedNodeKind : byte
{
    /// <summary>A track's audio clips (<see cref="ClipPlayerNode"/>).</summary>
    Clips = 1,

    /// <summary>A running plugin instrument or effect (<see cref="PluginNode"/>).</summary>
    Plugin = 2,
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
}

/// <summary>A mixer strip by its channel's id, with its fader, pan, mute, and solo.</summary>
public sealed record DescribedStrip(Guid Key, MixerStripSettings Settings);

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

    /// <returns>The node's index.</returns>
    public int AddNode(DescribedNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes.Add(node);
        _steps.Add(new(DescribedStepKind.AddNode, _nodes.Count - 1));
        return _nodes.Count - 1;
    }

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

    public AudioGraphDescription Build(double masterGainDecibels, IEnumerable<Guid> retained) =>
        new([.. _nodes], [.. _strips], [.. _steps], masterGainDecibels, [.. retained]);

    private void CheckNode(int node) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)node, (uint)_nodes.Count, nameof(node));

    private void CheckStrip(int strip) => ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)strip, (uint)_strips.Count, nameof(strip));
}
