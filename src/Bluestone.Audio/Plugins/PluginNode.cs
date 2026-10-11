using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio.Plugins;

/// <summary>
/// A plugin instance (in its worker process) driven by the audio engine: every block it hands the instance the
/// node's input audio, the scheduled events and parameter changes that fall in the block, and the transport, through
/// <see cref="PluginInstance.ProcessBlock"/>. Plugins run every block, playing or not, so tails ring out. On a jump
/// (start, stop, seek, loop wrap) and when its schedule is replaced, every note the node started is released first.
/// The instance's failure policy decides the output when the worker cannot deliver: silence, never stale audio.
/// The output lags by <see cref="LatencyFrames"/> (pipelining plus the plugin's own latency); the graph compensates
/// (ADR 0035). Live input reaches the instrument through <see cref="LiveSource"/>, at the next block.
/// </summary>
public sealed class PluginNode : AudioNode
{
    private const int NoteSlots = 16 * 128;
    private readonly PluginInstance _instance;
    private readonly float[] _input;
    private readonly float[] _output;
    private readonly PluginEvent[] _events;
    private readonly PluginEvent[] _outputEvents;
    private readonly bool[] _sounding = new bool[NoteSlots];
    private PluginSchedule _schedule = PluginSchedule.Empty;
    private PluginSchedule? _cursorSchedule;
    private ILiveEventSource? _liveSource;
    private ILiveEventSource? _liveActive;
    private int _nextEvent;
    private int _nextParameter;
    private long _expectedFrame = long.MinValue;
    private long _blocks;
    private long _failedBlocks;
    private long _droppedEvents;

    /// <param name="instance">The instance; its block size must hold the engine's blocks.</param>
    /// <param name="name">For diagnostics.</param>
    /// <param name="engineBlockFrames">The engine's block size.</param>
    public PluginNode(PluginInstance instance, string name, int engineBlockFrames)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        var request = instance.Request;
        if (engineBlockFrames > request.MaxBlockFrames)
        {
            throw new ArgumentException($"The plugin takes at most {request.MaxBlockFrames} frames per block; the engine renders {engineBlockFrames}.", nameof(engineBlockFrames));
        }

        _input = new float[request.InputChannels * request.MaxBlockFrames];
        _output = new float[request.OutputChannels * request.MaxBlockFrames];
        _events = new PluginEvent[request.EventCapacity];
        _outputEvents = new PluginEvent[request.EventCapacity];
    }

    public override string Name { get; }

    public PluginInstance Instance => _instance;

    /// <summary>Pipelining plus the plugin's own latency, in frames.</summary>
    public override int LatencyFrames => _instance.ReportedLatencyFrames;

    /// <summary>The events and parameter changes to play. Any thread; takes effect at the next block.</summary>
    public PluginSchedule Schedule
    {
        get => Volatile.Read(ref _schedule);
        set => Volatile.Write(ref _schedule, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>
    /// Where live input for this instrument comes from (ADR 0035), or null. Any thread; the audio thread picks it up at
    /// the next block, after asking the source it replaces to release what it sounds.
    /// </summary>
    public ILiveEventSource? LiveSource
    {
        get => Volatile.Read(ref _liveSource);
        set => Volatile.Write(ref _liveSource, value);
    }

    /// <summary>Blocks processed.</summary>
    public long Blocks => Interlocked.Read(ref _blocks);

    /// <summary>Blocks the plugin did not deliver (late, failed, unavailable): their output followed the failure policy.</summary>
    public long FailedBlocks => Interlocked.Read(ref _failedBlocks);

    /// <summary>Events that did not fit in a block's event capacity.</summary>
    public long DroppedEvents => Interlocked.Read(ref _droppedEvents);

    public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
    {
        var frames = context.Frames;
        var request = _instance.Request;
        var count = 0;
        var schedule = Volatile.Read(ref _schedule);
        if (context.Jumped || !ReferenceEquals(schedule, _cursorSchedule) || context.SongFrame != _expectedFrame)
        {
            count = Relocate(schedule, context.SongFrame, 0, count);
        }

        if (context.Playing)
        {
            count = Collect(schedule, context.SongFrame, context.FirstLength, 0, count);
            if (context.Wraps)
            {
                count = Relocate(schedule, context.WrapFrame, context.WrapOffset, count);
                count = Collect(schedule, context.WrapFrame, frames - context.WrapOffset, context.WrapOffset, count);
            }
        }

        count = CollectLive(context, count);
        var end = context.EndFrame;
        _expectedFrame = context.Playing ? end : context.SongFrame;
        var inputAudio = _input.AsSpan(0, request.InputChannels * frames);
        Deinterleave(input, inputAudio, request.InputChannels, frames);
        var outputAudio = _output.AsSpan(0, request.OutputChannels * frames);
        var outcome = _instance.ProcessBlock(frames, inputAudio, outputAudio, _events.AsSpan(0, count), _outputEvents, new TransportState(context.SongFrame, context.Tempo, context.Playing));
        Interlocked.Increment(ref _blocks);
        if (outcome.Outcome is not (ProcessOutcome.Processed or ProcessOutcome.Priming))
        {
            Interlocked.Increment(ref _failedBlocks);
        }

        if (request.OutputChannels >= 2)
        {
            outputAudio[..frames].CopyTo(output.Left);
            outputAudio.Slice(frames, frames).CopyTo(output.Right);
        }
        else if (request.OutputChannels == 1)
        {
            outputAudio[..frames].CopyTo(output.Left);
            outputAudio[..frames].CopyTo(output.Right);
        }
    }

    // Live events after the scheduled ones; a replaced source releases its notes first.
    private int CollectLive(in AudioRenderContext context, int count)
    {
        var source = Volatile.Read(ref _liveSource);
        var free = _events.AsSpan(count);
        if (!ReferenceEquals(source, _liveActive))
        {
            if (_liveActive is { } replaced)
            {
                var released = replaced.Release(free);
                count += released;
                free = free[released..];
            }

            _liveActive = source;
        }

        return source is null ? count : count + source.Render(context, free);
    }

    // Releases what sounds (at the given offset) and moves the cursors to a song frame.
    private int Relocate(PluginSchedule schedule, long songFrame, int offset, int count)
    {
        count = ReleaseSounding(count, offset);
        _cursorSchedule = schedule;
        _nextEvent = schedule.FirstEventAt(songFrame);
        _nextParameter = schedule.FirstParameterAt(songFrame);
        return count;
    }

    // Events and parameter changes in song frames [songFrame, songFrame + frames), placed from block offset `offset`.
    private int Collect(PluginSchedule schedule, long songFrame, int frames, int offset, int count)
    {
        var end = songFrame + frames;
        var events = schedule.Events;
        while (_nextEvent < events.Length && events[_nextEvent].Frame < end)
        {
            var scheduled = events[_nextEvent++];
            if (scheduled.Frame < songFrame)
            {
                continue;
            }

            if (count < _events.Length)
            {
                var e = scheduled.Event.WithSampleOffset(offset + (int)(scheduled.Frame - songFrame));
                Track(e);
                _events[count++] = e;
            }
            else
            {
                Interlocked.Increment(ref _droppedEvents);
            }
        }

        var parameters = schedule.Parameters;
        while (_nextParameter < parameters.Length && parameters[_nextParameter].Frame < end)
        {
            var change = parameters[_nextParameter++];
            if (change.Frame >= songFrame)
            {
                _instance.QueueParameterChange(change.Parameter, change.Value, offset + (int)(change.Frame - songFrame));
            }
        }

        return count;
    }

    private static void Deinterleave(StereoBuffer input, Span<float> planar, int channels, int frames)
    {
        if (channels == 0)
        {
            return;
        }

        if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                planar[i] = 0.5f * (input.Left[i] + input.Right[i]);
            }

            return;
        }

        planar.Clear();
        input.Left.CopyTo(planar);
        input.Right.CopyTo(planar[frames..]);
    }

    // Note-offs at offset 0 for every note this node started and has not ended.
    private int ReleaseSounding(int count, int offset)
    {
        for (var slot = 0; slot < NoteSlots && count < _events.Length; slot++)
        {
            if (_sounding[slot])
            {
                _sounding[slot] = false;
                _events[count++] = PluginEvent.NoteOff(offset, slot >> 7, slot & 127);
            }
        }

        return count;
    }

    private void Track(in PluginEvent e)
    {
        if (e.Kind == PluginEventKind.NoteOn && e.Data2 > 0)
        {
            _sounding[(e.Channel << 7) | e.Data1] = true;
        }
        else if (e.Kind == PluginEventKind.NoteOff || (e.Kind == PluginEventKind.NoteOn && e.Data2 == 0))
        {
            _sounding[(e.Channel << 7) | e.Data1] = false;
        }
    }
}
