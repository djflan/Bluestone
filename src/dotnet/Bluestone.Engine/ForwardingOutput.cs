using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;

namespace Bluestone.Engine;

/// <summary>
/// An output only Bluestone can reach (its monitor and test buses, its own virtual ports): what the engine sends here
/// goes back to Bluestone through the <see cref="OutboundRing"/>, which sends it on. Not real-time beyond the engine,
/// which is fine for those endpoints. Sending allocates nothing.
/// </summary>
internal sealed class ForwardingOutput(int handle, EndpointDescriptor endpoint, OutboundRing ring) : IMidiOutput
{
    private volatile bool _closed;

    public EndpointDescriptor Endpoint { get; } = endpoint;

    public EndpointState State => _closed ? EndpointState.Closed : EndpointState.Open;

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        if (_closed)
        {
            return SendResult.Closed;
        }

        if (message.IsEmpty || message.Length > EngineLimits.MaxMidiMessageBytes)
        {
            return SendResult.Rejected;
        }

        return ring.TryWrite(MidiEventKind.Output, handle, timestamp.IsImmediate ? 0 : timestamp.Time.Ticks, message) ? SendResult.Sent : SendResult.QueueFull;
    }

    public void Dispose() => _closed = true;
}
