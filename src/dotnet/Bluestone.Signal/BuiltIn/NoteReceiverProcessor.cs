using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;

namespace Bluestone.Signal.BuiltIn;

/// <summary>
/// Runs <see cref="BuiltInDevices.NoteReceiver"/>'s own part: it passes the chain's events (mix) or drops them
/// (replace). What it receives is added by <see cref="ChainRunner"/>, which knows where the connections come from.
/// </summary>
public sealed class NoteReceiverProcessor : ISignalProcessor
{
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
        if (!_replace)
        {
            output.AddRange(input);
        }
    }

    public void Reset()
    {
    }
}
