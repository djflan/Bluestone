using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;

namespace Bluestone.Signal.Live;

/// <summary>
/// The live form of <see cref="BuiltInDevices.Transpose"/>. A note-off is moved by the amount its note-on was moved,
/// so a change of transposition while a key is held never leaves a note hanging. Notes that would leave 0-127 are
/// dropped (with their releases), as at plan time.
/// </summary>
public sealed class LiveTransposeProcessor : ILiveProcessor
{
    // For each played note (channel × note), the note it sounds as, or -1.
    private readonly short[] _sounding = new short[LiveEvent.NoteSlots];
    private int _semitones;

    public LiveTransposeProcessor() => Array.Fill(_sounding, (short)-1);

    /// <summary>Notes dropped because they would leave the MIDI range.</summary>
    public long Dropped { get; private set; }

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (parameter == BuiltInDevices.TransposeSemitones)
        {
            _semitones = BuiltInDevices.SemitonesFrom(value);
        }
    }

    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            switch (e.Kind)
            {
                case LiveEventKind.NoteOn:
                    var on = e.Data1 + _semitones;
                    if (on is < 0 or > 127)
                    {
                        _sounding[e.NoteSlot] = -1;
                        Dropped++;
                        continue;
                    }

                    _sounding[e.NoteSlot] = (short)on;
                    output.Add(e with { Data1 = (byte)on });
                    break;
                case LiveEventKind.NoteOff:
                    var slot = e.NoteSlot;
                    var off = _sounding[slot] >= 0 ? _sounding[slot] : e.Data1 + _semitones;
                    _sounding[slot] = -1;
                    if (off is >= 0 and <= 127)
                    {
                        output.Add(e with { Data1 = (byte)off });
                    }

                    break;
                case LiveEventKind.PolyPressure:
                    var pressed = e.Data1 + _semitones;
                    if (pressed is >= 0 and <= 127)
                    {
                        output.Add(e with { Data1 = (byte)pressed });
                    }

                    break;
                default:
                    output.Add(e);
                    break;
            }
        }
    }

    public void Release(in LiveBlock block, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var slot = 0; slot < _sounding.Length; slot++)
        {
            if (_sounding[slot] >= 0)
            {
                output.Add(LiveEvent.NoteOff(block.StartTick, slot >> 7, _sounding[slot]));
                _sounding[slot] = -1;
            }
        }
    }
}
