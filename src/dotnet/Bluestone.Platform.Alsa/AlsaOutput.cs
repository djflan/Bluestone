using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;

namespace Bluestone.Platform.Alsa;

/// <summary>
/// Sends to an ALSA sequencer port, or to the subscribers of a Bluestone virtual port, with
/// <c>snd_seq_event_output_direct</c>, which delivers immediately. Bytes are converted to a sequencer
/// event by ALSA's own encoder, so system exclusive needs no special path. Nothing is allocated per send.
/// </summary>
/// <remarks>Like every <see cref="IMidiOutput"/>, sends come from one thread at a time.</remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class AlsaOutput : IMidiOutput
{
    /// <summary>The longest message accepted, matching the other adapters.</summary>
    public const int MaxMessage = ushort.MaxValue;

    private readonly AlsaProvider _provider;
    private readonly IntPtr _seq;
    private readonly byte _sourcePort;
    private readonly byte _destClient;
    private readonly byte _destPort;
    private IntPtr _encoder;
    private int _state = (int)EndpointState.Open;
    private int _sending;

    /// <param name="provider">The provider that owns this output.</param>
    /// <param name="endpoint">The endpoint being opened.</param>
    /// <param name="seq">The provider's sequencer handle used for sends.</param>
    /// <param name="sourcePort">The local port events are sent from.</param>
    /// <param name="destination">The client and port to send to, or null for the source port's subscribers.</param>
    public AlsaOutput(AlsaProvider provider, EndpointDescriptor endpoint, IntPtr seq, int sourcePort, (int Client, int Port)? destination)
    {
        _provider = provider;
        Endpoint = endpoint;
        _seq = seq;
        _sourcePort = (byte)sourcePort;
        (_destClient, _destPort) = destination is { } d ? ((byte)d.Client, (byte)d.Port) : (Alsa.AddressSubscribers, Alsa.AddressUnknown);
        var status = Alsa.snd_midi_event_new(MaxMessage, out _encoder);
        if (status < 0)
        {
            throw new EndpointUnavailableException(endpoint.Id, $"ALSA could not create a MIDI encoder ({Alsa.Error(status)})");
        }
    }

    public EndpointDescriptor Endpoint { get; }

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        // Dispose marks the handle closed, then waits for in-flight sends before freeing the encoder.
        Interlocked.Increment(ref _sending);
        try
        {
            switch (State)
            {
                case EndpointState.Disconnected:
                    return SendResult.Disconnected;
                case EndpointState.Closed or EndpointState.Faulted:
                    return SendResult.Closed;
            }

            if (MidiWire.Classify(message) == MidiMessageClass.Invalid || message.Length > MaxMessage)
            {
                return SendResult.Rejected;
            }

            var ev = default(Alsa.SeqEvent);
            Alsa.snd_midi_event_reset_encode(_encoder);
            long consumed;
            fixed (byte* bytes = message)
            {
                consumed = Alsa.snd_midi_event_encode(_encoder, bytes, new CLong(message.Length), &ev).Value;
            }

            if (consumed != message.Length || ev.Type == 0)
            {
                return SendResult.Rejected;
            }

            ev.Queue = Alsa.QueueDirect;
            ev.SourcePort = _sourcePort;
            ev.DestClient = _destClient;
            ev.DestPort = _destPort;
            var status = Alsa.snd_seq_event_output_direct(_seq, &ev);
            return status >= 0 ? SendResult.Sent : Map(-status);
        }
        finally
        {
            Interlocked.Decrement(ref _sending);
        }
    }

    public void Dispose()
    {
        if (!Transition(EndpointState.Closed, null))
        {
            return;
        }

        var spinner = default(SpinWait);
        while (Volatile.Read(ref _sending) != 0)
        {
            spinner.SpinOnce();
        }

        Alsa.snd_midi_event_free(_encoder);
        _encoder = IntPtr.Zero;
        _provider.Forget(this);
    }

    private SendResult Map(int errno)
    {
        switch (errno)
        {
            case Alsa.EAGAIN:
                return SendResult.QueueFull;
            case Alsa.ENOENT or Alsa.ENXIO or Alsa.ENODEV:
                Transition(EndpointState.Disconnected, "The ALSA port went away.");
                return SendResult.Disconnected;
            default:
                Transition(EndpointState.Faulted, "ALSA reported an error.");
                return SendResult.Closed;
        }
    }

    private bool Transition(EndpointState state, string? reason)
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == (int)EndpointState.Closed || current == (int)state)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, (int)state, current) == current)
            {
                break;
            }
        }

        StateChanged?.Invoke(this, new EndpointStateChangedEventArgs(state, reason));
        return true;
    }
}
