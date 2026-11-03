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
/// When the worker dies Bluestone marks the exchange faulted; the exchange sees the mark and this instance outputs the
/// failure policy until it is attached to the next one. Latency is what Bluestone reports for the instance.
/// </remarks>
public sealed class AttachedPluginInstance : IPluginProcessor, IDisposable
{
    private readonly PluginBlockPipeline _pipeline;
    private readonly PluginEvent[] _withSweep;
    private readonly Lock _gate = new();
    private HostBlockExchange? _exchange;
    private PluginDataPlane? _plane;
    private int _latency;
    private int _sweep;

    public AttachedPluginInstance(ExchangeOptions shape, int pipelineDepth, TimeSpan collectWait, bool bypassOnFailure)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Shape = shape;
        _pipeline = new PluginBlockPipeline(shape.MaxFrames, shape.InputChannels, shape.ParameterChangeCapacity, pipelineDepth, collectWait, bypassOnFailure);
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

    public int PipelineDepth => _pipeline.PipelineDepth;

    public TimeSpan CollectWait => _pipeline.CollectWait;

    public bool BypassOnFailure { get; }

    public int MaxBlockFrames => Shape.MaxFrames;

    public int InputChannels => Shape.InputChannels;

    public int OutputChannels => Shape.OutputChannels;

    public int EventCapacity => Shape.EventCapacity;

    /// <summary>The latency Bluestone last reported for the instance. Any thread.</summary>
    public int ReportedLatencyFrames
    {
        get => Volatile.Read(ref _latency);
        set => Volatile.Write(ref _latency, Math.Max(0, value));
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

    public BlockOutcome ProcessBlock(int frames, ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<PluginEvent> inputEvents, Span<PluginEvent> outputEvents, in TransportState transport)
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

        if (Volatile.Read(ref _sweep) != 0 && Interlocked.Exchange(ref _sweep, 0) != 0)
        {
            inputEvents = WithSweep(inputEvents);
        }

        return _pipeline.Process(Volatile.Read(ref _exchange), frames, input, output[..(Shape.OutputChannels * frames)], inputEvents, outputEvents, transport);
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
