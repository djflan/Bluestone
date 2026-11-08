using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;

namespace Bluestone.Signal.Live;

/// <summary>
/// The live form of <see cref="BuiltInDevices.NoteReceiver"/>: the track's own live events pass with Replace off and
/// stop with it on. Live input from the receiver's source is not brought in. A release always follows a note it let
/// through, and a pedal-up a sustain or sostenuto pedal-down it let through, so switching Replace while a key or pedal is
/// held never leaves a note sounding.
/// </summary>
public sealed class LiveNoteReceiverProcessor : ILiveProcessor
{
    private const int Sustain = 64;
    private const int Sostenuto = 66;
    private readonly bool[] _passed = new bool[LiveEvent.NoteSlots];
    private readonly bool[] _sustainDown = new bool[16];
    private readonly bool[] _sostenutoDown = new bool[16];
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
                // A blocked repeat of a note already let through keeps that note's release coming.
                _passed[e.NoteSlot] |= passes;
            }
            else if (e.IsNoteOff && _passed[e.NoteSlot])
            {
                _passed[e.NoteSlot] = false;
                passes = true;
            }
            else if (e.Kind == LiveEventKind.ControlChange && e.Data1 is Sustain or Sostenuto)
            {
                var down = e.Data1 == Sustain ? _sustainDown : _sostenutoDown;
                if (e.Data2 >= 64)
                {
                    down[e.Channel] |= passes;
                }
                else if (down[e.Channel])
                {
                    down[e.Channel] = false;
                    passes = true;
                }
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

        for (var channel = 0; channel < _sustainDown.Length; channel++)
        {
            if (_sustainDown[channel])
            {
                output.Add(new LiveEvent(block.StartTick, LiveEventKind.ControlChange, (byte)channel, Sustain, 0));
                _sustainDown[channel] = false;
            }

            if (_sostenutoDown[channel])
            {
                output.Add(new LiveEvent(block.StartTick, LiveEventKind.ControlChange, (byte)channel, Sostenuto, 0));
                _sostenutoDown[channel] = false;
            }
        }
    }
}
