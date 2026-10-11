using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;

namespace Bluestone.Signal.Live;

/// <summary>
/// A connection's forced channel on a live route: every channel event that passes is sent on one channel. Not a
/// device; it stands where the connection is, between the chains a live route crosses.
/// </summary>
public sealed class LiveChannelProcessor(MidiChannel channel) : ILiveProcessor
{
    public MidiChannel Channel { get; } = channel;

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
    }

    public void Process(in LiveBlock block, ReadOnlySpan<LiveEvent> input, LiveEventBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            output.Add(e with { Channel = Channel.Index });
        }
    }

    public void Release(in LiveBlock block, LiveEventBuffer output)
    {
    }
}
