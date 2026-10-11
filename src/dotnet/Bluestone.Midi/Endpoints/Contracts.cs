namespace Bluestone.Midi.Endpoints;

/// <summary>When a message should be delivered, in <see cref="Timing.IMonotonicClock"/> time.</summary>
public readonly record struct MidiTimestamp
{
    private MidiTimestamp(TimeSpan time) => Time = time;

    public static readonly MidiTimestamp Immediate;

    public static MidiTimestamp At(TimeSpan monotonicTime) => new(monotonicTime);

    /// <summary>The requested delivery time; <see cref="TimeSpan.Zero"/> means as soon as possible.</summary>
    public TimeSpan Time { get; }

    public bool IsImmediate => Time == TimeSpan.Zero;
}

public enum SendResult
{
    Sent,

    /// <summary>The message was not exactly one complete, valid MIDI message, or the endpoint cannot carry it.</summary>
    Rejected,

    /// <summary>The adapter's buffer is full; the message was dropped.</summary>
    QueueFull,

    /// <summary>The endpoint is no longer present.</summary>
    Disconnected,

    /// <summary>The handle was disposed or faulted.</summary>
    Closed,
}

/// <summary>An open connection to an output endpoint.</summary>
/// <remarks>
/// <see cref="Send"/> is called from the playback thread. Implementations must not block, allocate
/// per call, log, or throw for expected conditions; they report outcomes through <see cref="SendResult"/>.
/// Disposal closes the OS port deterministically.
/// </remarks>
public interface IMidiOutput : IDisposable
{
    EndpointDescriptor Endpoint { get; }

    EndpointState State { get; }

    /// <summary>Raised on an arbitrary thread when <see cref="State"/> changes.</summary>
    event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Sends one complete MIDI 1.0 message (no running status). A future <paramref name="timestamp"/> is
    /// honored only by endpoints with <see cref="EndpointCapabilities.ScheduledDelivery"/>; others
    /// deliver immediately, and the caller is responsible for waiting.
    /// </summary>
    SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp);
}

/// <summary>Receives one complete MIDI message. The span is only valid during the call.</summary>
public delegate void MidiReceiveHandler(ReadOnlySpan<byte> message, TimeSpan timestamp);

/// <summary>An open connection to an input endpoint.</summary>
public interface IMidiInput : IDisposable
{
    EndpointDescriptor Endpoint { get; }

    EndpointState State { get; }

    event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Sets the handler for incoming messages, or <see langword="null"/> to stop receiving. The handler
    /// runs on an adapter thread and must return quickly without blocking.
    /// </summary>
    void SetReceiver(MidiReceiveHandler? handler);
}

/// <summary>
/// An adapter for one family of endpoints (CoreMIDI, Windows MIDI Services, ALSA, network sessions,
/// test doubles). Capability discovery belongs here, never in the domain.
/// </summary>
public interface IMidiEndpointProvider : IDisposable
{
    /// <summary>Stable machine-readable identifier, used in <see cref="EndpointId.Provider"/>.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>A factual description of the adapter's timing behavior, shown in diagnostics.</summary>
    string TimingDescription { get; }

    IReadOnlyList<EndpointDescriptor> GetEndpoints();

    /// <summary>Raised on an arbitrary thread when endpoints appear, disappear, or change.</summary>
    event EventHandler? EndpointsChanged;

    /// <exception cref="EndpointUnavailableException">The endpoint cannot be opened for output.</exception>
    ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default);

    /// <exception cref="EndpointUnavailableException">The endpoint cannot be opened for input.</exception>
    ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default);
}
