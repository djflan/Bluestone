using System.Diagnostics;
using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Signal;
using Bluestone.Signal.Live;

namespace Bluestone.Engine;

/// <summary>
/// One live route the engine runs (ADR 0035, 0044), as Bluestone's <c>LiveRunner</c> runs it: built-in live devices,
/// forced channels, and plugin MIDI effects, to an external instrument's output or into a plugin instrument of the audio
/// graph.
/// </summary>
/// <remarks>
/// <para>
/// To an output, input is played at once on the thread that receives it, and the live thread advances the route every
/// millisecond for what happens later (arpeggiator steps); with a plugin effect on the way it runs fixed blocks of
/// <see cref="EffectBlockFrames"/> instead, as the effect needs one block size throughout. The caller holds the engine's
/// route lock for those calls.
/// </para>
/// <para>
/// Into a plugin instrument (migration step 7) the route is the instrument node's <see cref="ILiveEventSource"/>: it
/// runs in the device callback, one audio block at a time. Input waits in a small fixed queue under its own lock (held
/// only to copy an event) and goes into the next block at its first frame; generated events (arpeggiator steps) land on
/// their own frame. Each note-on's latency, from its input timestamp to when it is heard (the plugins' latency and the
/// compensation on the way to the master included), goes to the <see cref="LiveMeter"/>.
/// </para>
/// <para>Processing allocates nothing.</para>
/// </remarks>
internal sealed class EngineRoute : ILiveEventSource
{
    /// <summary>The block a plugin MIDI effect gets when the live thread runs it (1.3 ms at 48 kHz), as in Bluestone.</summary>
    public const int EffectBlockFrames = 64;

    private const int Capacity = 512;
    private const int MaxQueued = 4096;
    private readonly LiveChain _chain;
    private readonly LiveEventBuffer _pending = new(Capacity);
    private readonly LiveEventBuffer _input = new(Capacity);
    private readonly LiveEventBuffer _output = new(Capacity);
    private readonly bool[] _sounding = new bool[LiveEvent.NoteSlots];
    private readonly Ppqn _ppqn;
    private readonly PlaybackEngine _engine;
    private readonly LiveMeter _meter;
    private readonly EnginePluginStage[] _plugins;
    private readonly int _fixedFrames;
    private readonly int _sampleRate;

    // Input waiting for the audio thread (a plugin instrument), under _inboxGate.
    private readonly Lock _inboxGate = new();
    private readonly (LiveEvent Event, long Timestamp)[] _inbox;
    private int _inboxStart;
    private int _inboxCount;
    private long _overflow;

    private double _tick;
    private TimeSpan _clock = TimeSpan.MinValue;
    private TimeSpan _nextBlock;
    private long _pumpedTo;
    private PluginNode? _node;
    private int _delayToMaster;
    private double _stoppedTempo = 120;

    private EngineRoute(SetRoute spec, IMidiOutput? output, LiveChain chain, Ppqn ppqn, PlaybackEngine engine, LiveMeter meter, EnginePluginStage[] plugins, int fixedFrames, int sampleRate)
    {
        Id = spec.Route;
        Track = spec.Track;
        Instrument = spec.Instrument;
        Output = output;
        PluginDevices = [.. spec.Stages.Where(s => s.Plugin is not null).Select(s => s.Plugin!.Device)];
        _chain = chain;
        _ppqn = ppqn;
        _engine = engine;
        _meter = meter;
        _plugins = plugins;
        _fixedFrames = fixedFrames;
        _sampleRate = sampleRate;
        _inbox = Instrument is null ? [] : new (LiveEvent, long)[MaxQueued];
    }

    public int Id { get; }

    public Guid Track { get; }

    /// <summary>The plugin instrument the route plays (rendered in the device callback), or null for an output.</summary>
    public Guid? Instrument { get; }

    /// <summary>The output the route plays to, or null for a plugin instrument.</summary>
    public IMidiOutput? Output { get; }

    /// <summary>The plugin MIDI effects on the route, by device.</summary>
    public IReadOnlyList<Guid> PluginDevices { get; }

    public bool Released { get; private set; }

    /// <summary>The instrument node the route is the live source of now, if any. Under the route lock.</summary>
    public PluginNode? Node => _node;

    /// <summary>Events lost because a block or the queue was full.</summary>
    public long Dropped => _chain.Dropped + _input.Dropped + _output.Dropped + Interlocked.Read(ref _overflow);

    /// <summary>The tempo while the transport is stopped (the tempo at the playhead). Any thread.</summary>
    public double StoppedTempo
    {
        get => Volatile.Read(ref _stoppedTempo);
        set => Volatile.Write(ref _stoppedTempo, value);
    }

    /// <summary>
    /// The route described by <paramref name="spec"/>. Built-in devices the engine has no live processor for are left
    /// out (they pass events through, as a device that runs nowhere does in Bluestone) and named in
    /// <paramref name="unknown"/>; a plugin effect gets its instance from <paramref name="plugin"/>.
    /// </summary>
    public static EngineRoute Create(SetRoute spec, IMidiOutput? output, PlaybackEngine engine, LiveMeter meter, Func<PluginStageSpec, IPluginProcessor> plugin, out string? unknown)
    {
        ArgumentNullException.ThrowIfNull(spec);
        unknown = null;
        var ppqn = new Ppqn(spec.Ppqn);
        var stages = new List<(ILiveProcessor, EventClass)>();
        var plugins = new List<EnginePluginStage>();
        var rate = 48_000;
        foreach (var stage in spec.Stages)
        {
            if (stage.Channel >= 0)
            {
                stages.Add((new LiveChannelProcessor(Domain.Midi.MidiChannel.FromIndex(stage.Channel)), EventClass.Notes | EventClass.Controllers | EventClass.Programs));
                continue;
            }

            if (stage.Plugin is { } effect)
            {
                // Plugin MIDI effects handle notes, as their device definitions say.
                var pluginStage = new EnginePluginStage(plugin(effect));
                if (plugins.Count == 0)
                {
                    rate = effect.SampleRate;
                }

                plugins.Add(pluginStage);
                stages.Add((pluginStage, EventClass.Notes));
                continue;
            }

            var id = new DeviceDefinitionId(stage.Device);
            if (DeviceCatalog.BuiltIn.Find(id) is not { } definition || DeviceCatalog.BuiltIn.CreateLiveProcessor(id, ppqn) is not { } processor)
            {
                unknown = stage.Device;
                continue;
            }

            foreach (var parameter in definition.Parameters)
            {
                var value = Find(stage, parameter.Id.Value);
                processor.SetParameter(parameter.Id, value is { } stored ? new Domain.Midi.ControlValue(stored) : parameter.DefaultValue);
            }

            stages.Add((processor, definition.Handles));
        }

        var fixedFrames = spec.Instrument is null && plugins.Count > 0 ? plugins.Min(p => Math.Min(EffectBlockFrames, p.Instance.MaxBlockFrames)) : 0;
        return new EngineRoute(spec, output, new LiveChain(stages, Capacity), ppqn, engine, meter, [.. plugins], fixedFrames, rate);
    }

    /// <summary>
    /// Makes the route the live source of <paramref name="node"/> (or of nothing), with the frames from the node's output
    /// to the master for the latency meter. Under the route lock; <paramref name="sets"/> holds each node's sources.
    /// </summary>
    public void Bind(PluginNode? node, int delayToMaster, Dictionary<PluginNode, LiveSourceSet> sets)
    {
        Volatile.Write(ref _delayToMaster, delayToMaster);
        if (ReferenceEquals(node, _node))
        {
            return;
        }

        Unbind(sets);
        _node = node;
        if (node is not null)
        {
            if (!sets.TryGetValue(node, out var set))
            {
                sets[node] = set = new LiveSourceSet();
            }

            set.Add(this);
            node.LiveSource = set;
        }
    }

    /// <summary>Leaves the node's live sources; the audio thread releases what the route sounds at its next block. Under the route lock.</summary>
    public void Unbind(Dictionary<PluginNode, LiveSourceSet> sets)
    {
        if (_node is not { } node)
        {
            return;
        }

        if (sets.TryGetValue(node, out var set))
        {
            set.Remove(this);
            if (set.Count == 0)
            {
                sets.Remove(node);
                if (ReferenceEquals(node.LiveSource, set))
                {
                    node.LiveSource = null;
                }
            }
        }

        _node = null;
    }

    /// <summary>
    /// Queues input received at <paramref name="timestamp"/>. To an output it plays at the next <see cref="Pump"/>; into
    /// an instrument, at the next audio block. Allocation-free.
    /// </summary>
    public void Post(in LiveEvent e, TimeSpan timestamp)
    {
        if (Released)
        {
            return;
        }

        if (Instrument is null)
        {
            _pending.Add(e);
            return;
        }

        lock (_inboxGate)
        {
            if (_inboxCount == _inbox.Length)
            {
                Interlocked.Increment(ref _overflow);
                return;
            }

            _inbox[(_inboxStart + _inboxCount) % _inbox.Length] = (e, timestamp.Ticks);
            _inboxCount++;
        }
    }

    /// <summary>Advances a route to an output to <paramref name="now"/> at <paramref name="tempo"/> beats per minute, sending what it puts out.</summary>
    public void Pump(TimeSpan now, double tempo)
    {
        if (Released || Instrument is not null)
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
            RunPending(block);
            Send();
            return;
        }

        // Fixed blocks: each runs once its start has come; with input waiting, the next one runs at once (up to a block early).
        var period = TimeSpan.FromSeconds((double)_fixedFrames / _sampleRate);
        if (now - _nextBlock > TimeSpan.FromSeconds(1))
        {
            _nextBlock = now;
        }

        while (_nextBlock <= now || (_pending.Count > 0 && _nextBlock <= now + period))
        {
            var block = Advance(period.TotalSeconds * TicksPerSecond(tempo), _fixedFrames);
            _nextBlock += period;
            RunPending(block);
            Send();
        }
    }

    /// <summary>Ends every note a route to an output sounds, now; the route plays nothing after this. A route into an instrument is released by the audio thread instead.</summary>
    public void ReleaseNow()
    {
        if (Released)
        {
            return;
        }

        Released = true;
        if (Instrument is not null)
        {
            return;
        }

        var start = _fixedFrames == 0 ? _pumpedTo : (long)_tick;
        var block = new LiveBlock(start, start, _fixedFrames);
        _output.Clear();
        _chain.Release(block, _output);
        Send();
        for (var slot = 0; slot < _sounding.Length; slot++)
        {
            if (_sounding[slot])
            {
                _sounding[slot] = false;
                SendExternal(LiveEvent.NoteOff(block.StartTick, slot >> 7, slot & 127));
            }
        }
    }

    /// <summary>The live events for this audio block, into the instrument. The device callback.</summary>
    public int Render(in AudioRenderContext context, Span<PluginEvent> events)
    {
        var tempo = context.Playing ? context.Tempo : StoppedTempo;
        var block = Advance((double)context.Frames / context.SampleRate * TicksPerSecond(tempo), context.Frames);
        var heard = TimeSpan.Zero;
        if (context.HeardTimestamp != 0 && _node is { } node)
        {
            // What follows the block's start: the effects' and the instrument's latency, and its way to the master.
            var after = node.LatencyFrames + Volatile.Read(ref _delayToMaster);
            foreach (var stage in _plugins)
            {
                after += stage.Instance.ReportedLatencyFrames;
            }

            heard = Stopwatch.GetElapsedTime(0, context.HeardTimestamp) + TimeSpan.FromSeconds((double)after / context.SampleRate);
        }

        _input.Clear();
        lock (_inboxGate)
        {
            while (_inboxCount > 0 && _input.Count < _input.Capacity)
            {
                var (e, timestamp) = _inbox[_inboxStart];
                _inboxStart = (_inboxStart + 1) % _inbox.Length;
                _inboxCount--;
                _input.Add(e.At(block.StartTick));
                if (heard != TimeSpan.Zero && e.IsNoteOn)
                {
                    _meter.Record(heard - TimeSpan.FromTicks(timestamp));
                }
            }
        }

        _output.Clear();
        _chain.Process(block, _input.Events, _output);
        var count = 0;
        foreach (var e in _output.Events)
        {
            if (count < events.Length && Admit(e))
            {
                events[count++] = EnginePluginStage.ToPlugin(e, block.OffsetOf(e.Tick));
            }
        }

        return count;
    }

    /// <summary>Ends every note the route sounds in the instrument. The device callback.</summary>
    public int Release(Span<PluginEvent> events)
    {
        var block = new LiveBlock((long)_tick, (long)_tick, _node?.Processor.MaxBlockFrames ?? 0);
        _output.Clear();
        _chain.Release(block, _output);
        var count = 0;
        foreach (var e in _output.Events)
        {
            if (count < events.Length && Admit(e))
            {
                events[count++] = EnginePluginStage.ToPlugin(e, 0);
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

    private static uint? Find(RouteStageSpec stage, uint id)
    {
        foreach (var parameter in stage.Parameters)
        {
            if (parameter.Id == id)
            {
                return parameter.Value;
            }
        }

        return null;
    }

    private double TicksPerSecond(double tempo) => tempo / 60 * _ppqn.TicksPerQuarterNote;

    private LiveBlock Advance(double ticks, int frames)
    {
        var origin = _tick;
        var start = (long)origin;
        _tick += ticks;
        return new LiveBlock(start, Math.Max(start, (long)_tick), frames, origin, ticks > 0 ? frames / ticks : 0);
    }

    // Input waiting for a route to an output goes into this block, at its start, and the chain runs.
    private void RunPending(in LiveBlock block)
    {
        _input.Clear();
        foreach (var e in _pending.Events)
        {
            _input.Add(e.At(block.StartTick));
        }

        _pending.Clear();
        _output.Clear();
        _chain.Process(block, _input.Events, _output);
    }

    private void Send()
    {
        foreach (var e in _output.Events)
        {
            if (Admit(e))
            {
                SendExternal(e);
            }
        }
    }

    // The target keeps its sounding notes; a release only goes out for a note that sounds.
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
        if (Output is { } output && ChannelMessage.TryCreate(e.Status, e.Data1, e.Kind is LiveEventKind.ProgramChange or LiveEventKind.ChannelPressure ? (byte)0 : e.Data2, out var message))
        {
            _engine.SendNow(output, message);
        }
    }
}
