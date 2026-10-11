using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal.Live;

/// <summary>
/// The live form of <see cref="BuiltInDevices.EventFilter"/>. A release always follows a note the filter let through,
/// even if notes were blocked in between, so switching the filter while a key is held never leaves a note hanging.
/// </summary>
public sealed class LiveEventFilterProcessor : ILiveProcessor
{
    private readonly bool[] _passed = new bool[LiveEvent.NoteSlots];
    private EventClass _blocked;

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        foreach (var (id, eventClass) in BuiltInDevices.FilterSwitches)
        {
            if (id == parameter)
            {
                _blocked = value.ToFraction() >= 0.5 ? _blocked & ~eventClass : _blocked | eventClass;
            }
        }
    }

    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            var passes = (e.Class & _blocked) == EventClass.None;
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
