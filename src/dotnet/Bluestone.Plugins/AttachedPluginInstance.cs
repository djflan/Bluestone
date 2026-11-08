using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Plugins;

/// <summary>
/// The data plane of a plugin instance whose control plane is in another process (ADR 0044): the engine attaches to the
/// block exchange of an instance that Bluestone's <see cref="PluginHostManager"/> supervises, and drives it from its
/// audio thread. Bluestone says which exchange is current (<see cref="Attach"/>): after the worker restarts, a new
/// generation and file; while it is not running, none, and the output follows the failure policy.
/// </summary>
/// <remarks>
/// <para>
/// When the worker dies Bluestone marks the exchange faulted; the exchange sees the mark and this instance outputs the
/// failure policy until it is attached to the next one. Its latency is the plugin's own, as Bluestone reports it, plus
/// the pipeline's.
/// </para>
/// <para>
/// The depth can change while it plays (<see cref="RequestPipelineDepth"/>): an instrument played live runs at depth 1,
/// submitting and collecting each block in the same call (ADR 0043 decision 6). Every collect waits at most what is left
/// of the rendering thread's <see cref="CollectBudget"/> (a share of the block the engine renders, shared by every
/// plugin in it), or its collect wait where there is none. A block that misses that deadline is silent (the failure
/// policy). After <see cref="MissesToFallBack"/> misses within <see cref="MissWindowBlocks"/> blocks
/// the instance falls back to depth 2 by itself (<see cref="FellBack"/>) and stays there until depth 1 is asked for
/// again after something else was (monitoring stops and starts), so a worker that cannot keep up costs one block of
/// latency rather than repeated dropouts.
/// </para>
/// </remarks>
public sealed class AttachedPluginInstance : IPluginProcessor, IDisposable
{
    /// <summary>Missed depth-1 blocks within <see cref="MissWindowBlocks"/> that make the instance fall back to depth 2.</summary>
    public const int MissesToFallBack = 3;

    /// <summary>How many blocks back a missed depth-1 block counts towards falling back.</summary>
    public const int MissWindowBlocks = 1000;

    private readonly PluginBlockPipeline _pipeline;
    private readonly long[] _misses = new long[MissesToFallBack];
    private readonly PluginEvent[] _withSweep;
    private readonly Lock _gate = new();
    private HostBlockExchange? _exchange;
    private PluginDataPlane? _plane;
    private int _latency;
    private int _sweep;
    private int _fellBack;
    private int _fallbacks;
    private long _blocks;
    private int _nextMiss;
    private long _liveMisses;

    public AttachedPluginInstance(ExchangeOptions shape, int pipelineDepth, TimeSpan collectWait, bool bypassOnFailure)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Shape = shape;
        // The engine's callback bounds every collect: depth 1 waits within the block, and depth 2 must not overrun it either.
        _pipeline = new PluginBlockPipeline(shape.MaxFrames, shape.InputChannels, shape.ParameterChangeCapacity, pipelineDepth, collectWait, bypassOnFailure, shape.SlotCount)
        {
            BoundedByCallback = true,
        };
        Array.Fill(_misses, long.MinValue);
        _withSweep = new PluginEvent[shape.EventCapacity];
        BypassOnFailure = bypassOnFailure;
    }

    /// <summary>Never raised here: Bluestone hears the plugin's notifications and describes the graph again.</summary>
    event EventHandler<NotificationKind>? IPluginProcessor.Changed
    {
        add
        {
        }

        remove
        {
        }
    }

    /// <summary>The exchange's shape, fixed for the instance.</summary>
    public ExchangeOptions Shape { get; }

    /// <summary>The depth it plays at (from the next block).</summary>
    public int PipelineDepth => _pipeline.PipelineDepth;

    /// <summary>Depth 1 was asked for, but the instance fell back to depth 2 after missing its deadline. Any thread.</summary>
    public bool FellBack => Volatile.Read(ref _fellBack) != 0;

    /// <summary>How many times it has fallen back. Any thread.</summary>
    public int Fallbacks => Volatile.Read(ref _fallbacks);

    /// <summary>Blocks that missed the depth-1 deadline (each was silent). Any thread.</summary>
    public long LiveDeadlineMisses => Interlocked.Read(ref _liveMisses);

    public TimeSpan CollectWait => _pipeline.CollectWait;

    public bool BypassOnFailure { get; }

    public int MaxBlockFrames => Shape.MaxFrames;

    public int InputChannels => Shape.InputChannels;

    public int OutputChannels => Shape.OutputChannels;

    public int SidechainChannels => Shape.SidechainChannels;

    public int EventCapacity => Shape.EventCapacity;

    /// <summary>The plugin's own latency, as Bluestone last reported it. Any thread.</summary>
    public int PluginLatencyFrames
    {
        get => Volatile.Read(ref _latency);
        set => Volatile.Write(ref _latency, Math.Max(0, value));
    }

    /// <summary>The plugin's own latency plus the pipeline's at the depth asked for. Any thread.</summary>
    public int ReportedLatencyFrames => PipelineLatency.ReportedLatencyFrames(PluginLatencyFrames, PipelineDepth, Shape.MaxFrames);

    /// <summary>
    /// Asks for <paramref name="depth"/> from the next block. Depth 1 is not taken while the instance has fallen back,
    /// unless another depth was asked for since. Control thread.
    /// </summary>
    public void RequestPipelineDepth(int depth)
    {
        depth = Math.Clamp(depth, 1, Shape.SlotCount);
        if (depth > 1)
        {
            Volatile.Write(ref _fellBack, 0);
        }
        else if (FellBack)
        {
            return;
        }

        _pipeline.SetPipelineDepth(depth);
    }

    /// <summary>The exchange attached now, or null.</summary>
    public PluginDataPlane? DataPlane
    {
        get
        {
            lock (_gate)
            {
                return _plane;
            }
        }
    }

    /// <summary>Whether the instance has an exchange that is not known to be faulted.</summary>
    public bool IsAttached => Volatile.Read(ref _exchange) is { IsFaulted: false };

    /// <summary>
    /// Attaches to <paramref name="plane"/> (none for null), unless it is the one attached already. The previous exchange
    /// is released once the audio thread is out of it. Control thread.
    /// </summary>
    /// <exception cref="ProtocolException">The file's header does not match.</exception>
    /// <exception cref="IOException">The file cannot be mapped.</exception>
    public void Attach(PluginDataPlane? plane)
    {
        lock (_gate)
        {
            if (Equals(plane, _plane) && (plane is null || _exchange is not null))
            {
                return;
            }

            if (plane is not null && plane.Options != Shape)
            {
                throw new ProtocolException("The exchange's shape is not the instance's.");
            }

            var next = plane is null ? null : HostBlockExchange.Open(plane.Path, plane.Generation, plane.Options);
            var previous = Interlocked.Exchange(ref _exchange, next);
            _plane = plane;

            // Waits for a call the audio thread may be making on it; later calls see it disposed (silence, never a fault).
            previous?.Dispose();
        }
    }

    /// <summary>
    /// Sends sustain off, All Notes Off, and All Sound Off on every channel before the next block's events: an engine that
    /// replaced one that stopped cannot know which notes the plugin still holds (ADR 0044).
    /// </summary>
    public void SweepBeforeNextBlock() => Volatile.Write(ref _sweep, 1);

    public BlockOutcome ProcessBlock(int frames, ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<PluginEvent> inputEvents, Span<PluginEvent> outputEvents, in TransportState transport, ReadOnlySpan<float> sidechain = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frames, Shape.MaxFrames);
        if (input.Length != Shape.InputChannels * frames)
        {
            throw new ArgumentException("The input must hold InputChannels * frames samples.", nameof(input));
        }

        if (output.Length < Shape.OutputChannels * frames)
        {
            throw new ArgumentException("The output must hold OutputChannels * frames samples.", nameof(output));
        }

        if (!sidechain.IsEmpty && sidechain.Length != Shape.SidechainChannels * frames)
        {
            throw new ArgumentException("The sidechain must be empty or hold SidechainChannels * frames samples.", nameof(sidechain));
        }

        if (Volatile.Read(ref _sweep) != 0 && Interlocked.Exchange(ref _sweep, 0) != 0)
        {
            inputEvents = WithSweep(inputEvents);
        }

        var live = _pipeline.PipelineDepth == 1;

        var outcome = _pipeline.Process(Volatile.Read(ref _exchange), frames, input, output[..(Shape.OutputChannels * frames)], inputEvents, outputEvents, transport, sidechain);
        var block = _blocks++;
        if (live && outcome.Outcome == ProcessOutcome.Late)
        {
            Missed(block);
        }

        return outcome;
    }

    public bool QueueParameterChange(uint parameterId, double normalizedValue, int sampleOffset) =>
        NormalizedValue.IsValid(normalizedValue) && sampleOffset >= 0 && sampleOffset < Shape.MaxFrames && _pipeline.Queue(parameterId, normalizedValue, sampleOffset);

    /// <summary>Releases the exchange. The worker and its file belong to Bluestone and are left alone.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _exchange, null)?.Dispose();
            _plane = null;
        }
    }

    // A missed depth-1 block; the third within the window makes the instance fall back to depth 2. Audio thread.
    private void Missed(long block)
    {
        Interlocked.Increment(ref _liveMisses);
        _misses[_nextMiss] = block;
        _nextMiss = (_nextMiss + 1) % _misses.Length;
        var oldest = _misses[_nextMiss];
        if (oldest != long.MinValue && block - oldest < MissWindowBlocks)
        {
            _pipeline.SetPipelineDepth(2);
            Volatile.Write(ref _fellBack, 1);
            Interlocked.Increment(ref _fallbacks);
            Array.Fill(_misses, long.MinValue);
        }
    }

    private ReadOnlySpan<PluginEvent> WithSweep(ReadOnlySpan<PluginEvent> events)
    {
        var count = 0;
        for (var channel = 0; channel < 16 && count + 3 <= _withSweep.Length; channel++)
        {
            _withSweep[count++] = PluginEvent.ControlChange(0, channel, 64, 0);
            _withSweep[count++] = PluginEvent.ControlChange(0, channel, 123, 0);
            _withSweep[count++] = PluginEvent.ControlChange(0, channel, 120, 0);
        }

        var kept = Math.Min(events.Length, _withSweep.Length - count);
        events[..kept].CopyTo(_withSweep.AsSpan(count));
        return _withSweep.AsSpan(0, count + kept);
    }
}
