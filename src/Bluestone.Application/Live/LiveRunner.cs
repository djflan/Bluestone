using System.Collections.Concurrent;
using System.Diagnostics;
using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Signal.Live;

namespace Bluestone.Application.Live;

/// <summary>
/// Runs one track's live route (ADR 0035): its stages as a <see cref="LiveChain"/> on the live clock, and the target.
/// A route to a plugin instrument runs on the audio thread, inside the instrument's <see cref="PluginNode"/>, one audio
/// block at a time (<see cref="ILiveEventSource"/>); any other route runs where <see cref="LiveInput"/> pumps it: on
/// the thread that posts input, and on the live thread for what happens later (arpeggiator steps, plugin effect
/// blocks). Either way exactly one thread processes it at a time. Input is queued lock-free; processing allocates
/// nothing. The target keeps the notes it sounds, so a release only goes out for a note that sounds, and
/// <see cref="ILiveEventSource.Release"/> or <see cref="ReleaseNow"/> ends every one.
/// </summary>
internal sealed class LiveRunner : ILiveEventSource
{
    private const int Capacity = 512;

    // Input waiting for a block; more is dropped (and counted), so a route whose node is not rendered cannot grow without bound.
    private const int MaxQueued = 4096;
    private readonly ConcurrentQueue<(LiveEvent Event, TimeSpan Timestamp)> _queue = new();
    private readonly LiveChain _chain;
    private readonly LiveEventBuffer _input = new(Capacity);
    private readonly LiveEventBuffer _output = new(Capacity);
    private readonly bool[] _sounding = new bool[LiveEvent.NoteSlots];
    private readonly Ppqn _ppqn;
    private readonly IMidiOutput? _external;
    private readonly PlaybackEngine? _engine;
    private readonly LiveLatencyMeter? _latency;
    private readonly int _fixedFrames;
    private readonly int _sampleRate;
    private double _tick;
    private TimeSpan _clock = TimeSpan.MinValue;
    private TimeSpan _nextBlock;
    private long _pumpedTo;
    private bool _released;
    private int _queued;
    private long _overflow;

    private LiveRunner(LiveRoutePlan plan, LiveChain chain, Ppqn ppqn, IMidiOutput? external, PlaybackEngine? engine, PluginNode? node, LiveLatencyMeter? latency, int fixedFrames, int sampleRate)
    {
        Plan = plan;
        _chain = chain;
        _ppqn = ppqn;
        _external = external;
        _engine = engine;
        Node = node;
        _latency = latency;
        _fixedFrames = fixedFrames;
        _sampleRate = sampleRate;
    }

    public LiveRoutePlan Plan { get; }

    /// <summary>The instrument's node when the route ends at a plugin instrument that the audio engine plays.</summary>
    public PluginNode? Node { get; }

    /// <summary>Runs on the audio thread (a plugin instrument) rather than where input is posted.</summary>
    public bool IsAudioDriven => Node is not null;

    /// <summary>Whether input posted now can be heard.</summary>
    public bool CanPlay => Plan.Target == LiveTargetKind.External ? _external is not null : Node is not null;

    public EndpointDescriptor? ExternalEndpoint => _external?.Endpoint;

    /// <summary>The delay compensation after the instrument in the graph playing it (0 with low-latency monitoring). Any thread.</summary>
    public int CompensationFrames { get; set; }

    /// <summary>Tempo while the transport is stopped (the tempo at the playhead). Any thread.</summary>
    public double StoppedTempo { get; set; } = 120;

    /// <summary>Plugin MIDI effect stages on the route, for tests and status.</summary>
    public IReadOnlyList<PluginLiveStage> PluginStages { get; private init; } = [];

    /// <summary>Events lost because a block held more than the buffers' capacity.</summary>
    public long Dropped => _chain.Dropped + _input.Dropped + _output.Dropped + Interlocked.Read(ref _overflow);

    public static LiveRunner Create(LiveRoutePlan plan, LiveContext context, LiveLatencyMeter latency)
    {
        var stages = new List<(ILiveProcessor, EventClass)>();
        var plugins = new List<PluginLiveStage>();
        foreach (var stage in plan.Stages)
        {
            if (stage.Channel is { } channel)
            {
                stages.Add((new LiveChannelProcessor(channel), EventClass.Notes | EventClass.Controllers | EventClass.Programs));
            }
            else if (stage.Device is { } device)
            {
                if (context.Devices.CreateLiveStage(device, context.Ppqn) is { } builtIn)
                {
                    stages.Add(builtIn);
                }
                else if (context.Plugins(device.Id) is { } instance && context.Devices.Find(device.Definition.Id) is { } definition)
                {
                    var plugin = new PluginLiveStage(instance);
                    plugins.Add(plugin);
                    stages.Add((plugin, definition.Handles));
                }

                // A device that runs nowhere passes events through unchanged, as at plan time.
            }
        }

        IMidiOutput? external = null;
        PluginNode? node = null;
        if (plan.Target == LiveTargetKind.External && plan.Endpoint is { } endpoint)
        {
            external = context.Outputs.GetValueOrDefault(endpoint.Id);
        }
        else if (plan.Target == LiveTargetKind.Software && plan.Instrument is { } instrument)
        {
            node = context.Nodes?.GetValueOrDefault(instrument);
        }

        // A route with plugin effects that the live thread runs needs fixed blocks at their rate (one block size throughout).
        var rate = plugins.Count > 0 ? (int)plugins[0].Instance.Request.SampleRate : 48_000;
        var fixedFrames = node is null && plugins.Count > 0 ? plugins.Min(p => Math.Min(LiveInput.EffectBlockFrames, p.Instance.Request.MaxBlockFrames)) : 0;
        return new LiveRunner(plan, new LiveChain(stages, Capacity), context.Ppqn, external, context.Engine, node, node is null ? null : latency, fixedFrames, rate)
        {
            PluginStages = plugins,
        };
    }

    /// <summary>Queues input; it is processed in the next block. Any thread.</summary>
    public void Enqueue(in LiveEvent e, TimeSpan timestamp)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _overflow);
            return;
        }

        _queue.Enqueue((e, timestamp));
    }

    /// <summary>Advances a route the audio engine does not run to <paramref name="now"/>, sending what it puts out. One thread at a time.</summary>
    public void Pump(TimeSpan now, double tempo)
    {
        if (IsAudioDriven || _released)
        {
            return;
        }

        if (_clock == TimeSpan.MinValue)
        {
            _clock = now;
            _nextBlock = now;
        }

        if (_fixedFrames == 0)
        {
            // Up to and including the tick that is due now, so a step falls exactly when it is due.
            _tick += Math.Max(0, (now - _clock).TotalSeconds) * TicksPerSecond(tempo);
            _clock = now;
            var end = Math.Max(_pumpedTo, (long)Math.Floor(_tick + 1e-6) + 1);
            var block = new LiveBlock(_pumpedTo, end);
            _pumpedTo = end;
            Run(block);
            Send(block);
            return;
        }

        // Fixed blocks: each runs once its start has come; with input waiting, the next one runs at once (up to a block early).
        var period = TimeSpan.FromSeconds((double)_fixedFrames / _sampleRate);
        if (now - _nextBlock > TimeSpan.FromSeconds(1))
        {
            _nextBlock = now;
        }

        while (_nextBlock <= now || (!_queue.IsEmpty && _nextBlock <= now + period))
        {
            var block = Advance(period.TotalSeconds * TicksPerSecond(tempo), _fixedFrames);
            _nextBlock += period;
            Run(block);
            Send(block);
        }
    }

    /// <summary>Ends every note the route sounds, sending the releases now. For routes the audio engine does not run.</summary>
    public void ReleaseNow()
    {
        if (IsAudioDriven || _released)
        {
            return;
        }

        _released = true;
        var start = _fixedFrames == 0 ? _pumpedTo : (long)_tick;
        var block = new LiveBlock(start, start, _fixedFrames);
        _output.Clear();
        _chain.Release(block, _output);
        Send(block);
        for (var slot = 0; slot < _sounding.Length; slot++)
        {
            if (_sounding[slot])
            {
                _sounding[slot] = false;
                SendExternal(LiveEvent.NoteOff(block.StartTick, slot >> 7, slot & 127));
            }
        }
    }

    public int Render(in AudioRenderContext context, Span<PluginEvent> events)
    {
        var tempo = context.Playing ? context.Tempo : StoppedTempo;
        var block = Advance((double)context.Frames / context.SampleRate * TicksPerSecond(tempo), context.Frames);
        if (_latency is { } meter && context.HeardTimestamp != 0 && Node is { } node)
        {
            // What follows the block's start: the effects' and the instrument's latency, and the compensation after it.
            var heard = Stopwatch.GetElapsedTime(0, context.HeardTimestamp);
            var effects = 0;
            foreach (var stage in PluginStages)
            {
                effects += stage.Instance.ReportedLatencyFrames;
            }

            var after = TimeSpan.FromSeconds((double)(effects + node.LatencyFrames + CompensationFrames) / context.SampleRate);
            Run(block, heard + after, meter);
        }
        else
        {
            Run(block);
        }

        var count = 0;
        foreach (var e in _output.Events)
        {
            if (count < events.Length && Admit(e))
            {
                events[count++] = PluginLiveStage.ToPlugin(e, block.OffsetOf(e.Tick));
            }
        }

        return count;
    }

    public int Release(Span<PluginEvent> events)
    {
        var block = new LiveBlock((long)_tick, (long)_tick, Node?.Instance.Request.MaxBlockFrames ?? 0);
        _output.Clear();
        _chain.Release(block, _output);
        var count = 0;
        foreach (var e in _output.Events)
        {
            if (count < events.Length && Admit(e))
            {
                events[count++] = PluginLiveStage.ToPlugin(e, 0);
            }
        }

        for (var slot = 0; slot < _sounding.Length && count < events.Length; slot++)
        {
            if (_sounding[slot])
            {
                _sounding[slot] = false;
                events[count++] = PluginEvent.NoteOff(0, slot >> 7, slot & 127);
            }
        }

        return count;
    }

    private double TicksPerSecond(double tempo) => tempo / 60 * _ppqn.TicksPerQuarterNote;

    private LiveBlock Advance(double ticks, int frames)
    {
        var origin = _tick;
        var start = (long)origin;
        _tick += ticks;
        return new LiveBlock(start, Math.Max(start, (long)_tick), frames, origin, ticks > 0 ? frames / ticks : 0);
    }

    // Drains the queue into this block (input is placed at its start: it is played now) and runs the chain.
    private void Run(in LiveBlock block, TimeSpan heard = default, LiveLatencyMeter? meter = null)
    {
        _input.Clear();
        while (_input.Count < _input.Capacity && _queue.TryDequeue(out var queued))
        {
            Interlocked.Decrement(ref _queued);
            _input.Add(queued.Event.At(block.StartTick));
            if (meter is not null && queued.Event.IsNoteOn)
            {
                meter.Record(heard - queued.Timestamp);
            }
        }

        _output.Clear();
        _chain.Process(block, _input.Events, _output);
    }

    private void Send(in LiveBlock block)
    {
        foreach (var e in _output.Events)
        {
            if (Admit(e))
            {
                SendExternal(e);
            }
        }
    }

    // Keeps the target's sounding notes; a release for a note that does not sound is not sent.
    private bool Admit(in LiveEvent e)
    {
        if (e.IsNoteOn)
        {
            _sounding[e.NoteSlot] = true;
        }
        else if (e.IsNoteOff)
        {
            if (!_sounding[e.NoteSlot])
            {
                return false;
            }

            _sounding[e.NoteSlot] = false;
        }

        return true;
    }

    private void SendExternal(in LiveEvent e)
    {
        if (_external is { } output && _engine is { } engine && ChannelMessage.TryCreate(e.Status, e.Data1, e.Kind is LiveEventKind.ProgramChange or LiveEventKind.ChannelPressure ? (byte)0 : e.Data2, out var message))
        {
            engine.SendNow(output, message);
        }
    }
}

/// <summary>Live input's latency from the input's timestamp to when its first frame is heard, measured as it is played.</summary>
public sealed class LiveLatencyMeter
{
    private long _count;
    private long _sumTicks;
    private long _minTicks = long.MaxValue;
    private long _maxTicks;

    /// <summary>A note-on measured on the audio thread. Lock-free.</summary>
    internal void Record(TimeSpan latency)
    {
        var ticks = latency.Ticks;
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumTicks, ticks);
        long seen;
        while (ticks < (seen = Interlocked.Read(ref _minTicks)) && Interlocked.CompareExchange(ref _minTicks, ticks, seen) != seen)
        {
        }

        while (ticks > (seen = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, seen) != seen)
        {
        }
    }

    /// <summary>Note-ons measured, and their average, shortest, and longest latency.</summary>
    public LiveLatency Read()
    {
        var count = Interlocked.Read(ref _count);
        return count == 0
            ? new LiveLatency(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)
            : new LiveLatency(count, TimeSpan.FromTicks(Interlocked.Read(ref _sumTicks) / count), TimeSpan.FromTicks(Interlocked.Read(ref _minTicks)), TimeSpan.FromTicks(Interlocked.Read(ref _maxTicks)));
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _sumTicks, 0);
        Interlocked.Exchange(ref _minTicks, long.MaxValue);
        Interlocked.Exchange(ref _maxTicks, 0);
    }
}

/// <summary>Measured live-input latency: from a note's input timestamp until it is heard (device-reported output latency included).</summary>
public readonly record struct LiveLatency(long Count, TimeSpan Average, TimeSpan Min, TimeSpan Max);
