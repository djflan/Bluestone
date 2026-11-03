using System.Collections.Concurrent;
using System.Collections.Immutable;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using NotificationKind = Bluestone.Plugins.Protocol.NotificationKind;

namespace Bluestone.Audio;

/// <summary>A realized graph: the graph to publish, what could not be played, and each plugin node's latency by key.</summary>
/// <param name="Graph">The graph; its <see cref="AudioGraph.LatencyFrames"/> is the longest path's latency.</param>
/// <param name="Problems">Unreadable audio files and plugins that could not play, in words.</param>
/// <param name="LatencyFrames">Each plugin node in the graph, by device key: frames its output lags.</param>
public sealed record AudioGraphRealization(AudioGraph Graph, ImmutableArray<string> Problems, ImmutableDictionary<Guid, int> LatencyFrames);

/// <summary>
/// Turns <see cref="AudioGraphDescription"/>s into render graphs (ADR 0044), keeping state between them: a clip node
/// whose files did not change keeps its streams (only gain and fades are updated), a plugin node keeps its note
/// tracking, a strip keeps its meter and gain, and the compensation delay lines keep their audio. This is the half of
/// building a graph that needs the objects that render: in the engine process it runs there; in a single process,
/// beside the describing half.
/// </summary>
/// <remarks>
/// Control thread only (it opens files). It owns the clip nodes it opens: a node it replaces or lets go is closed at the
/// next realization (by then the graph that replaced it is the one playing), and <see cref="Dispose"/> closes the rest.
/// </remarks>
public sealed class AudioGraphRealizer : IDisposable
{
    private readonly int _sampleRate;
    private readonly int _blockFrames;
    private readonly Func<Guid, PluginInstance?> _instances;
    private readonly ConcurrentDictionary<Guid, MixerStripState> _strips = new();
    private readonly Dictionary<Guid, PluginNode> _plugins = [];
    private readonly Dictionary<Guid, (ImmutableArray<AudioClipRegion> Regions, ClipPlayerNode Node, IReadOnlyList<string> Problems)> _clips = [];
    private readonly List<ClipPlayerNode> _retired = [];
    private readonly LatencyCompensation _compensation = new();

    /// <param name="sampleRate">The engine's rate.</param>
    /// <param name="blockFrames">The engine's block size.</param>
    /// <param name="instances">The running plugin instance for a device key, if any.</param>
    public AudioGraphRealizer(int sampleRate, int blockFrames, Func<Guid, PluginInstance?> instances)
    {
        _sampleRate = sampleRate;
        _blockFrames = blockFrames;
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
    }

    public MixerStripState Master { get; } = new();

    /// <summary>A plugin in the graph reported a new latency (any thread); realize the graph again to compensate for it.</summary>
    public event EventHandler? LatencyChanged;

    /// <summary>The meter and gain state of a strip, once a graph has included it. Any thread.</summary>
    public MixerStripState? StripOf(Guid key) => _strips.GetValueOrDefault(key);

    /// <summary>Every plugin node kept, by device key. Any thread.</summary>
    public ImmutableDictionary<Guid, PluginNode> PluginNodes { get; private set; } = ImmutableDictionary<Guid, PluginNode>.Empty;

    /// <summary>Builds the graph <paramref name="description"/> describes, reusing what it can. Control thread.</summary>
    public AudioGraphRealization Realize(AudioGraphDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        foreach (var node in _retired)
        {
            node.Dispose();
        }

        _retired.Clear();
        var problems = ImmutableArray.CreateBuilder<string>();
        var latency = ImmutableDictionary.CreateBuilder<Guid, int>();
        var builder = new AudioGraphBuilder();
        foreach (var strip in description.Strips)
        {
            builder.AddStrip(_strips.GetOrAdd(strip.Key, _ => new MixerStripState()), strip.Settings);
        }

        var described = new HashSet<Guid>(description.Strips.Select(s => s.Key));
        foreach (var gone in _strips.Keys.Where(key => !described.Contains(key)).ToList())
        {
            _strips.TryRemove(gone, out _);
        }

        // Builder node indices are the description's AddNode steps in order; nodes[i] is the description's node i.
        var indices = new int[description.Nodes.Length];
        foreach (var step in description.Steps)
        {
            switch (step.Kind)
            {
                case DescribedStepKind.AddNode:
                    indices[step.A] = builder.AddNode(NodeFor(description.Nodes[step.A], problems, latency));
                    break;
                case DescribedStepKind.Connect:
                    builder.Connect(indices[step.A], indices[step.B]);
                    break;
                case DescribedStepKind.Monitor:
                    builder.Monitor(indices[step.A]);
                    break;
                case DescribedStepKind.ConnectToStrip:
                    builder.ConnectToStrip(indices[step.A], step.B);
                    break;
                case DescribedStepKind.ConnectToMaster:
                    builder.ConnectToMaster(indices[step.A]);
                    break;
                case DescribedStepKind.RouteStrip:
                    builder.RouteStrip(step.A, step.B);
                    break;
                case DescribedStepKind.SendStrip:
                    builder.SendStrip(step.A, step.B, step.Level, step.PreFader);
                    break;
                default:
                    throw new ArgumentException($"Unknown step {step.Kind}.", nameof(description));
            }
        }

        LetGo(description.Retained);
        PluginNodes = _plugins.ToImmutableDictionary();
        var graph = builder.Build(_blockFrames, Master, description.MasterGainDecibels, _compensation);
        return new AudioGraphRealization(graph, problems.Distinct().ToImmutableArray(), latency.ToImmutable());
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

    private AudioNode NodeFor(DescribedNode described, ImmutableArray<string>.Builder problems, ImmutableDictionary<Guid, int>.Builder latency) =>
        described.Kind switch
        {
            DescribedNodeKind.Clips => ClipsFor(described, problems),
            DescribedNodeKind.Plugin => PluginFor(described, problems, latency),
            _ => throw new ArgumentException($"Unknown node kind {described.Kind}.", nameof(described)),
        };

    private ClipPlayerNode ClipsFor(DescribedNode described, ImmutableArray<string>.Builder problems)
    {
        var regions = described.Regions;

        // Only the gain or fades changed: the node keeps its streams. Anything else (or a node closed elsewhere) opens
        // the clips again.
        if (_clips.TryGetValue(described.Key, out var entry) && !entry.Node.IsClosed && entry.Regions.Length == regions.Length && entry.Regions.Zip(regions).All(p => p.First.SameStream(p.Second)))
        {
            if (!entry.Regions.SequenceEqual(regions))
            {
                entry.Node.UpdateEnvelopes(regions);
                _clips[described.Key] = entry with { Regions = regions };
            }
        }
        else
        {
            if (_clips.TryGetValue(described.Key, out var replaced))
            {
                _retired.Add(replaced.Node);
            }

            var node = ClipPlayerNode.Open(described.Name, regions, _sampleRate, _blockFrames, out var opened);
            entry = (regions, node, opened);
            _clips[described.Key] = entry;
        }

        foreach (var problem in entry.Problems)
        {
            problems.Add($"{described.Name}: {problem}");
        }

        return entry.Node;
    }

    private AudioNode PluginFor(DescribedNode described, ImmutableArray<string>.Builder problems, ImmutableDictionary<Guid, int>.Builder latency)
    {
        // The describing side only describes a plugin node for a running instance at this rate and block size; one
        // that stopped since plays silence until the graph is described again.
        if (_instances(described.Key) is not { } instance)
        {
            problems.Add($"{described.Name} stopped before its audio could be connected, so it is silent.");
            return new SilentNode();
        }

        if (!_plugins.TryGetValue(described.Key, out var node) || !ReferenceEquals(node.Instance, instance))
        {
            if (_plugins.TryGetValue(described.Key, out var replaced))
            {
                replaced.Instance.Changed -= OnPluginChanged;
            }

            node = new PluginNode(instance, described.Name, _blockFrames);
            instance.Changed += OnPluginChanged;
            _plugins[described.Key] = node;
        }

        // An unchanged schedule keeps its instance: replacing it would make the node look for notes to release.
        if (!described.Schedule.HasSameContent(node.Schedule))
        {
            node.Schedule = described.Schedule;
        }

        latency[described.Key] = node.LatencyFrames;
        return node;
    }

    private void LetGo(ImmutableHashSet<Guid> retained)
    {
        foreach (var gone in _plugins.Keys.Where(key => !retained.Contains(key)).ToList())
        {
            _plugins[gone].Instance.Changed -= OnPluginChanged;
            _plugins.Remove(gone);
        }

        foreach (var gone in _clips.Keys.Where(key => !retained.Contains(key)).ToList())
        {
            _retired.Add(_clips[gone].Node);
            _clips.Remove(gone);
        }
    }

    private void OnPluginChanged(object? sender, NotificationKind kind)
    {
        if (kind == NotificationKind.LatencyChanged)
        {
            LatencyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stands in for a plugin that stopped between describing and realizing the graph.</summary>
    private sealed class SilentNode : AudioNode
    {
        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
        }
    }
}
