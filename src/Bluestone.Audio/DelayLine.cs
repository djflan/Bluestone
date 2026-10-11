namespace Bluestone.Audio;

/// <summary>
/// A stereo delay of a whole number of frames, for delay compensation (ADR 0035). It keeps its history across graph
/// snapshots, so rebuilding the graph does not drop the audio inside it; only a change of the delay moves the read
/// position (a click, as in other hosts, when a latency changes). Audio thread, after it is built.
/// </summary>
public sealed class DelayLine
{
    private readonly float[] _left;
    private readonly float[] _right;
    private readonly int _mask;
    private int _write;

    /// <param name="maxDelay">The longest delay it must hold, in frames.</param>
    /// <param name="maxBlock">The longest block it processes.</param>
    public DelayLine(int maxDelay, int maxBlock)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDelay);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBlock);
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(maxDelay + maxBlock));
        _left = new float[size];
        _right = new float[size];
        _mask = size - 1;
        MaxDelay = size - maxBlock;
    }

    /// <summary>The longest delay this line can apply with blocks up to the size it was built for.</summary>
    public int MaxDelay { get; }

    /// <summary>
    /// Writes <paramref name="source"/> into the line and puts what was written <paramref name="delay"/> frames earlier
    /// into <paramref name="output"/> (overwritten). <paramref name="source"/> and <paramref name="output"/> may not overlap.
    /// </summary>
    public void Process(StereoBuffer source, StereoBuffer output, int delay)
    {
        var frames = source.Frames;
        var write = _write;
        for (var i = 0; i < frames; i++)
        {
            var at = (write + i) & _mask;
            _left[at] = source.Left[i];
            _right[at] = source.Right[i];
            var from = (write + i - delay) & _mask;
            output.Left[i] = _left[from];
            output.Right[i] = _right[from];
        }

        _write = (write + frames) & _mask;
    }
}

/// <summary>
/// The delay lines of a project's render graphs, kept by edge (source, destination) across rebuilds so compensation
/// is continuous while the graph is edited. Control thread: <see cref="AudioGraphBuilder.Build"/> takes and prunes lines
/// here; the graphs it builds use them on the audio thread.
/// </summary>
public sealed class LatencyCompensation
{
    private readonly Dictionary<(object Source, object Destination, int Kind), DelayLine> _lines = [];
    private readonly HashSet<(object Source, object Destination, int Kind)> _used = [];

    /// <summary>Lines kept for the edges of the last graph built.</summary>
    public int Count => _lines.Count;

    internal void BeginBuild() => _used.Clear();

    /// <summary>A line for the edge that can hold <paramref name="delay"/>, or null for no delay.</summary>
    internal DelayLine? LineFor(object source, object destination, int kind, int delay, int blockFrames)
    {
        if (delay <= 0)
        {
            return null;
        }

        var key = (source, destination, kind);
        _used.Add(key);
        if (!_lines.TryGetValue(key, out var line) || line.MaxDelay < delay)
        {
            line = new DelayLine(delay, blockFrames);
            _lines[key] = line;
        }

        return line;
    }

    // An edge that leaves the graph starts silent if it comes back.
    internal void EndBuild()
    {
        foreach (var gone in _lines.Keys.Where(k => !_used.Contains(k)).ToList())
        {
            _lines.Remove(gone);
        }
    }
}
