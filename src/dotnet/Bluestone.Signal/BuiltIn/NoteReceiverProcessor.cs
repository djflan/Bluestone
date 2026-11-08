using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal.BuiltIn;

/// <summary>
/// Runs <see cref="BuiltInDevices.NoteReceiver"/>'s own part: it passes the chain's events (mix) or drops them
/// (replace). What it receives is added by <see cref="ChainRunner"/>, which knows where the connections come from. A
/// sustain or sostenuto pedal-up still passes in replace for a pedal-down it passed, so Replace switched on by
/// automation while a pedal is down never leaves notes sustained.
/// </summary>
public sealed class NoteReceiverProcessor : ISignalProcessor
{
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

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            var passes = !_replace;
            if (e.Event is ControllerEvent { Controller.Value: 64 or 66 } pedal)
            {
                var down = pedal.Controller.Value == 64 ? _sustainDown : _sostenutoDown;
                var channel = pedal.Channel.Index;
                if (pedal.Value.ToSevenBit() >= 64)
                {
                    down[channel] |= passes;
                }
                else if (down[channel])
                {
                    down[channel] = false;
                    passes = true;
                }
            }

            if (passes)
            {
                output.Add(e);
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_sustainDown);
        Array.Clear(_sostenutoDown);
    }
}
