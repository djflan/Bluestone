using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal.BuiltIn;

/// <summary>
/// Runs <see cref="BuiltInDevices.EventFilter"/>: removes whole classes of events. It is the explicit way
/// to drop events; no other built-in device removes what it does not understand.
/// </summary>
public sealed class EventFilterProcessor : ISignalProcessor
{
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

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            if ((EventClassification.Of(e.Event) & _blocked) == EventClass.None)
            {
                output.Add(e);
            }
        }
    }

    public void Reset()
    {
    }
}
