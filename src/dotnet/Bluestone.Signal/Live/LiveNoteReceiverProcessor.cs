using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;

namespace Bluestone.Signal.Live;

/// <summary>
/// The live form of <see cref="BuiltInDevices.NoteReceiver"/>: the track's own live events pass with Replace off and
/// stop with it on. Live input from the receiver's source is not brought in. A release always follows a note it let
/// through, so switching Replace while a key is held never leaves a note hanging.
/// </summary>
public sealed class LiveNoteReceiverProcessor : ILiveProcessor
{
    private readonly bool[] _passed = new bool[LiveEvent.NoteSlots];
    private bool _replace;

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (parameter == BuiltInDevices.ReceiverReplace)
        {
            _replace = value.ToFraction() >= 0.5;
        }
    }

    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            var passes = !_replace;
            if (e.IsNoteOn)
            {
                _passed[e.NoteSlot] = passes;
            }
            else if (e.IsNoteOff && _passed[e.NoteSlot])
            {
                _passed[e.NoteSlot] = false;
                passes = true;
            }

            if (passes)
            {
                output.Add(e);
            }
        }
    }

    public void Release(in LiveBlock block, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        for (var slot = 0; slot < _passed.Length; slot++)
        {
            if (_passed[slot])
            {
                output.Add(LiveEvent.NoteOff(block.StartTick, slot >> 7, slot & 127));
                _passed[slot] = false;
            }
        }
    }
}
