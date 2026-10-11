using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Signal.Live;

namespace Bluestone.Application.Live;

/// <summary>
/// A plugin MIDI effect on a live route (ADR 0035): each block goes to the device's own running instance through its
/// data plane (<see cref="PluginInstance.ProcessBlock"/>), not to the plan-time render. Output comes back one pipeline
/// block later. While the instance cannot deliver (its worker crashed, hung, or is restarting) events pass through
/// unchanged, as at plan time; whenever that changes either way, every note the stage had put out is released first,
/// so nothing it started can hang. The only thread that processes the instance is the one running this stage.
/// </summary>
internal sealed class PluginLiveStage : ILiveProcessor
{
    private readonly PluginInstance _instance;
    private readonly PluginEvent[] _input;
    private readonly PluginEvent[] _output;
    private readonly float[] _audioIn;
    private readonly float[] _audioOut;
    private readonly bool[] _fed = new bool[LiveEvent.NoteSlots];
    private readonly bool[] _emitted = new bool[LiveEvent.NoteSlots];
    private bool _failing;
    private long _songFrame;

    public PluginLiveStage(PluginInstance instance)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        var request = instance.Request;
        _input = new PluginEvent[request.EventCapacity];
        _output = new PluginEvent[request.EventCapacity];
        _audioIn = new float[request.InputChannels * request.MaxBlockFrames];
        _audioOut = new float[request.OutputChannels * request.MaxBlockFrames];
    }

    public PluginInstance Instance => _instance;

    /// <summary>Blocks the instance did not deliver (its events passed through instead).</summary>
    public long FailedBlocks { get; private set; }

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
    }

    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var frames = block.Frames;
        if (frames < 1 || frames > _instance.Request.MaxBlockFrames)
        {
            PassThrough(input, output);
            return;
        }

        var count = 0;
        foreach (var e in input)
        {
            if (count < _input.Length)
            {
                _input[count++] = ToPlugin(e, block.OffsetOf(e.Tick));
                Track(_fed, e);
            }
        }

        var request = _instance.Request;
        var outcome = _instance.ProcessBlock(
            frames,
            _audioIn.AsSpan(0, request.InputChannels * frames),
            _audioOut.AsSpan(0, request.OutputChannels * frames),
            _input.AsSpan(0, count),
            _output,
            new TransportState(_songFrame, 120, false));
        _songFrame += frames;
        var delivered = outcome.Outcome is ProcessOutcome.Processed or ProcessOutcome.Priming;
        if (delivered == _failing)
        {
            ReleaseEmitted(block.StartTick, output);
            _failing = !delivered;
        }

        if (!delivered)
        {
            FailedBlocks++;
            PassThrough(input, output);
            return;
        }

        for (var i = 0; i < outcome.OutputEventCount && i < _output.Length; i++)
        {
            if (FromPlugin(_output[i], block) is { } e)
            {
                Track(_emitted, e);
                output.Add(e);
            }
        }
    }

    /// <summary>
    /// Releases what the stage put out, and tells the plugin that the keys it was given are up (in one more block,
    /// whose output is dropped), so its own state does not keep a note held.
    /// </summary>
    public void Release(in LiveBlock block, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        ReleaseEmitted(block.StartTick, output);
        var count = 0;
        for (var slot = 0; slot < _fed.Length && count < _input.Length; slot++)
        {
            if (_fed[slot])
            {
                _fed[slot] = false;
                _input[count++] = PluginEvent.NoteOff(0, slot >> 7, slot & 127);
            }
        }

        var frames = Math.Clamp(block.Frames, 1, _instance.Request.MaxBlockFrames);
        if (count > 0 && !_failing)
        {
            var request = _instance.Request;
            _instance.ProcessBlock(frames, _audioIn.AsSpan(0, request.InputChannels * frames), _audioOut.AsSpan(0, request.OutputChannels * frames), _input.AsSpan(0, count), _output, new TransportState(_songFrame, 120, false));
            _songFrame += frames;
        }
    }

    private void ReleaseEmitted(long tick, LiveEventBuffer output)
    {
        for (var slot = 0; slot < _emitted.Length; slot++)
        {
            if (_emitted[slot])
            {
                _emitted[slot] = false;
                output.Add(LiveEvent.NoteOff(tick, slot >> 7, slot & 127));
            }
        }
    }

    private void PassThrough(ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        foreach (var e in input)
        {
            Track(_emitted, e);
            output.Add(e);
        }
    }

    private static void Track(bool[] notes, in LiveEvent e)
    {
        if (e.IsNoteOn)
        {
            notes[e.NoteSlot] = true;
        }
        else if (e.IsNoteOff)
        {
            notes[e.NoteSlot] = false;
        }
    }

    internal static PluginEvent ToPlugin(in LiveEvent e, int offset) => e.Kind switch
    {
        LiveEventKind.NoteOn => PluginEvent.NoteOn(offset, e.Channel, e.Data1, e.Data2),
        LiveEventKind.NoteOff => PluginEvent.NoteOff(offset, e.Channel, e.Data1, e.Data2),
        LiveEventKind.PolyPressure => PluginEvent.PolyPressure(offset, e.Channel, e.Data1, e.Data2),
        LiveEventKind.ControlChange => PluginEvent.ControlChange(offset, e.Channel, e.Data1, e.Data2),
        LiveEventKind.ProgramChange => PluginEvent.ProgramChange(offset, e.Channel, e.Data1),
        LiveEventKind.ChannelPressure => PluginEvent.ChannelPressure(offset, e.Channel, e.Data1),
        _ => PluginEvent.PitchBend(offset, e.Channel, e.Data1 | (e.Data2 << 7)),
    };

    private static LiveEvent? FromPlugin(in PluginEvent e, in LiveBlock block)
    {
        var tick = block.TickOf(e.SampleOffset);
        return e.Kind switch
        {
            PluginEventKind.NoteOn => LiveEvent.NoteOn(tick, e.Channel, e.Data1, e.Data2),
            PluginEventKind.NoteOff => LiveEvent.NoteOff(tick, e.Channel, e.Data1, e.Data2),
            PluginEventKind.PolyPressure => new LiveEvent(tick, LiveEventKind.PolyPressure, e.Channel, e.Data1, e.Data2),
            PluginEventKind.ControlChange => new LiveEvent(tick, LiveEventKind.ControlChange, e.Channel, e.Data1, e.Data2),
            PluginEventKind.ProgramChange => new LiveEvent(tick, LiveEventKind.ProgramChange, e.Channel, e.Data1, 0),
            PluginEventKind.ChannelPressure => new LiveEvent(tick, LiveEventKind.ChannelPressure, e.Channel, e.Data1, 0),
            PluginEventKind.PitchBend => new LiveEvent(tick, LiveEventKind.PitchBend, e.Channel, e.Data1, e.Data2),
            _ => null,
        };
    }
}
