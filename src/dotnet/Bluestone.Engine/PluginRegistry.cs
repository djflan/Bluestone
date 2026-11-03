using Bluestone.Audio;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Engine;

/// <summary>
/// The engine's handle on every plugin instance it drives (ADR 0044): one <see cref="AttachedPluginInstance"/> per
/// device, shared by the audio graph (instruments and effects) and live routes (MIDI effects), so one exchange is never
/// driven through two handles. An instance is kept while its shape stays the same, so a node keeps its note tracking
/// across graphs, and released when neither the graph nor a route uses it. Thread-safe.
/// </summary>
internal sealed class PluginRegistry : IDisposable
{
    private readonly bool _restarted;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _plugins = [];
    private HashSet<Guid> _graph = [];
    private HashSet<Guid> _routes = [];

    /// <param name="restarted">Started after a crash: each instance sweeps its plugin's notes before its first block.</param>
    public PluginRegistry(bool restarted) => _restarted = restarted;

    /// <summary>
    /// The instance for <paramref name="key"/>, attached to the exchange <paramref name="link"/> names and set to its
    /// depth and latency. A plugin it cannot reach is silent (its failure policy), and <paramref name="problems"/> says so.
    /// </summary>
    public AttachedPluginInstance Attach(Guid key, string name, DescribedPluginLink link, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(problems);
        lock (_gate)
        {
            if (!_plugins.TryGetValue(key, out var entry) || entry.Instance.Shape != link.Shape || entry.Instance.CollectWait != link.CollectWait
                || entry.Instance.BypassOnFailure != link.BypassOnFailure)
            {
                entry?.Instance.Dispose();
                var created = new AttachedPluginInstance(link.Shape, Math.Clamp(link.PipelineDepth, 1, link.Shape.SlotCount), link.CollectWait, link.BypassOnFailure);
                if (_restarted)
                {
                    created.SweepBeforeNextBlock();
                }

                entry = new Entry(created, name);
                _plugins[key] = entry;
            }

            entry.Name = name;
            var instance = entry.Instance;
            instance.PluginLatencyFrames = Math.Max(0, link.LatencyFrames - ((link.PipelineDepth - 1) * link.Shape.MaxFrames));
            instance.RequestPipelineDepth(link.PipelineDepth);
            try
            {
                instance.Attach(link.DataPlane);
            }
            catch (Exception ex) when (ex is ProtocolException or IOException or UnauthorizedAccessException)
            {
                instance.Attach(null);
                problems.Add($"{name}: the engine could not reach its plugin ({ex.Message}), so it is silent.");
            }

            return instance;
        }
    }

    /// <summary>The devices the graph (or the live routes) use now; instances neither uses are released.</summary>
    public void Use(bool graph, IEnumerable<Guid> keys)
    {
        lock (_gate)
        {
            if (graph)
            {
                _graph = [.. keys];
            }
            else
            {
                _routes = [.. keys];
            }

            foreach (var gone in _plugins.Keys.Where(k => !_graph.Contains(k) && !_routes.Contains(k)).ToList())
            {
                _plugins.Remove(gone, out var entry);
                entry!.Instance.Dispose();
            }
        }
    }

    /// <summary>Instances that fell back from depth 1 since the last call, by name.</summary>
    public List<string> TakeFallbacks()
    {
        var names = new List<string>();
        lock (_gate)
        {
            foreach (var entry in _plugins.Values)
            {
                var fallbacks = entry.Instance.Fallbacks;
                if (fallbacks != entry.FallbacksSeen)
                {
                    entry.FallbacksSeen = fallbacks;
                    names.Add(entry.Name);
                }
            }
        }

        return names;
    }

    /// <summary>Blocks of every instance that missed their depth-1 deadline. Any thread.</summary>
    public long DeadlineMisses
    {
        get
        {
            lock (_gate)
            {
                var misses = 0L;
                foreach (var entry in _plugins.Values)
                {
                    misses += entry.Instance.LiveDeadlineMisses;
                }

                return misses;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var entry in _plugins.Values)
            {
                entry.Instance.Dispose();
            }

            _plugins.Clear();
        }
    }

    private sealed class Entry(AttachedPluginInstance instance, string name)
    {
        public AttachedPluginInstance Instance { get; } = instance;

        public string Name { get; set; } = name;

        public int FallbacksSeen { get; set; }
    }
}
