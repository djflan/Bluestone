using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Signal;
using Bluestone.Signal.Live;

namespace Bluestone.Engine;

/// <summary>
/// One live route the engine runs (ADR 0035, 0044): built-in live devices and forced channels, to an external
/// instrument's output, as Bluestone's <c>LiveRunner</c> runs it for an external target. Input is played at once, on
/// the thread that receives it; the live thread advances it every millisecond for what happens later (arpeggiator
/// steps). The caller holds the engine's route lock for every call. Processing allocates nothing.
/// </summary>
internal sealed class EngineRoute
{
    private const int Capacity = 512;
    private readonly LiveChain _chain;
    private readonly LiveEventBuffer _pending = new(Capacity);
    private readonly LiveEventBuffer _input = new(Capacity);
    private readonly LiveEventBuffer _output = new(Capacity);
    private readonly bool[] _sounding = new bool[LiveEvent.NoteSlots];
    private readonly Ppqn _ppqn;
    private readonly PlaybackEngine _engine;
    private double _tick;
    private TimeSpan _clock = TimeSpan.MinValue;
    private long _pumpedTo;

    private EngineRoute(int id, Guid track, IMidiOutput output, LiveChain chain, Ppqn ppqn, PlaybackEngine engine)
    {
        Id = id;
        Track = track;
        Output = output;
        _chain = chain;
        _ppqn = ppqn;
        _engine = engine;
    }

    public int Id { get; }

    public Guid Track { get; }

    public IMidiOutput Output { get; }

    public bool Released { get; private set; }

    /// <summary>
    /// The route described by <paramref name="spec"/>. Devices the engine has no live processor for are left out
    /// (they pass events through, as a device that runs nowhere does in Bluestone) and named in <paramref name="unknown"/>.
    /// </summary>
    public static EngineRoute Create(SetRoute spec, IMidiOutput output, PlaybackEngine engine, out string? unknown)
    {
        unknown = null;
        var ppqn = new Ppqn(spec.Ppqn);
        var stages = new List<(ILiveProcessor, EventClass)>();
        foreach (var stage in spec.Stages)
        {
            if (stage.Channel >= 0)
            {
                stages.Add((new LiveChannelProcessor(Domain.Midi.MidiChannel.FromIndex(stage.Channel)), EventClass.Notes | EventClass.Controllers | EventClass.Programs));
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

        return new EngineRoute(spec.Route, spec.Track, output, new LiveChain(stages, Capacity), ppqn, engine);
    }

    /// <summary>Queues input for the next <see cref="Pump"/>; it is played at that block's start.</summary>
    public void Post(in LiveEvent e)
    {
        if (!Released)
        {
            _pending.Add(e);
        }
    }

    /// <summary>Advances the route to <paramref name="now"/> at <paramref name="tempo"/> beats per minute, sending what it puts out.</summary>
    public void Pump(TimeSpan now, double tempo)
    {
        if (Released)
        {
            return;
        }

        if (_clock == TimeSpan.MinValue)
        {
            _clock = now;
        }

        // Up to and including the tick that is due now, so a step falls exactly when it is due.
        _tick += Math.Max(0, (now - _clock).TotalSeconds) * (tempo / 60 * _ppqn.TicksPerQuarterNote);
        _clock = now;
        var end = Math.Max(_pumpedTo, (long)Math.Floor(_tick + 1e-6) + 1);
        var block = new LiveBlock(_pumpedTo, end);
        _pumpedTo = end;
        _input.Clear();
        foreach (var e in _pending.Events)
        {
            _input.Add(e.At(block.StartTick));
        }

        _pending.Clear();
        _output.Clear();
        _chain.Process(block, _input.Events, _output);
        Send();
    }

    /// <summary>Ends every note the route sounds, now; the route plays nothing after this.</summary>
    public void ReleaseNow()
    {
        if (Released)
        {
            return;
        }

        Released = true;
        var block = new LiveBlock(_pumpedTo, _pumpedTo);
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

    private void Send()
    {
        foreach (var e in _output.Events)
        {
            // The target keeps its sounding notes; a release only goes out for a note that sounds.
            if (e.IsNoteOn)
            {
                _sounding[e.NoteSlot] = true;
            }
            else if (e.IsNoteOff)
            {
                if (!_sounding[e.NoteSlot])
                {
                    continue;
                }

                _sounding[e.NoteSlot] = false;
            }

            SendExternal(e);
        }
    }

    private void SendExternal(in LiveEvent e)
    {
        if (ChannelMessage.TryCreate(e.Status, e.Data1, e.Kind is LiveEventKind.ProgramChange or LiveEventKind.ChannelPressure ? (byte)0 : e.Data2, out var message))
        {
            _engine.SendNow(Output, message);
        }
    }
}
