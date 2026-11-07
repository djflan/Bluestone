using System.Globalization;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Routing;

/// <summary>Stable identity of a connection.</summary>
public readonly record struct ConnectionId(Guid Value)
{
    public static ConnectionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum SignalNodeKind
{
    /// <summary>A track. As a source, the end of its chain (or its content when it has no chain); as a destination, the start of its chain.</summary>
    Track,

    /// <summary>A free-standing rack, named by its chain's ID, as source (the end of the chain) or destination (the start).</summary>
    Rack,

    /// <summary>A device's output inside a chain: a tap. Source only.</summary>
    Device,

    /// <summary>A part of an external instrument: a port, and the channel the connection's mapping gives. Destination only.</summary>
    ExternalInstrument,

    /// <summary>A mixer channel. Audio destination only.</summary>
    MixerChannel,

    /// <summary>The master bus. Audio destination only.</summary>
    Master,

    /// <summary>
    /// The start of a chain, before its first device: the signal arriving at the chain (a track's own content plus
    /// what is connected into it). Named by the chain's ID. Source only.
    /// </summary>
    ChainInput,

    /// <summary>
    /// An input port of a device inside a chain, such as a receiver's <c>receive</c> or a plugin's <c>sidechain</c>
    /// (<see cref="DeviceInputPort"/>). Named by the device's ID and the port's ID. Destination only.
    /// </summary>
    DeviceInput,
}

/// <summary>
/// One end of a connection. Everything is addressed by stable ID, never by a position, so reordering
/// tracks or devices rewires nothing.
/// </summary>
public readonly record struct SignalNode
{
    private SignalNode(SignalNodeKind kind, Guid id, string? port)
    {
        Kind = kind;
        Id = id;
        Port = port;
    }

    public SignalNodeKind Kind { get; }

    public Guid Id { get; }

    /// <summary>
    /// The external instrument's port (null means its first port), or the device's input port for
    /// <see cref="SignalNodeKind.DeviceInput"/>.
    /// </summary>
    public string? Port { get; }

    public static SignalNode Track(TrackId track) => new(SignalNodeKind.Track, track.Value, null);

    public static SignalNode Rack(DeviceChainId chain) => new(SignalNodeKind.Rack, chain.Value, null);

    public static SignalNode Device(DeviceId device) => new(SignalNodeKind.Device, device.Value, null);

    public static SignalNode ExternalPart(ExternalInstrumentId instrument, string? port = null) => new(SignalNodeKind.ExternalInstrument, instrument.Value, port);

    public static SignalNode Mixer(MixerChannelId channel) => new(SignalNodeKind.MixerChannel, channel.Value, null);

    public static SignalNode Master { get; } = new(SignalNodeKind.Master, Guid.Empty, null);

    /// <summary>The start of <paramref name="chain"/>, before its first device.</summary>
    public static SignalNode ChainInput(DeviceChainId chain) => new(SignalNodeKind.ChainInput, chain.Value, null);

    /// <summary>The input port <paramref name="port"/> of <paramref name="device"/>.</summary>
    public static SignalNode DeviceInput(DeviceId device, string port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(port);
        return new(SignalNodeKind.DeviceInput, device.Value, port);
    }

    public TrackId AsTrack() => new(Id);

    public DeviceChainId AsRack() => new(Id);

    /// <summary>The chain a <see cref="SignalNodeKind.ChainInput"/> or <see cref="SignalNodeKind.Rack"/> names.</summary>
    public DeviceChainId AsChain() => new(Id);

    public DeviceId AsDevice() => new(Id);

    public ExternalInstrumentId AsInstrument() => new(Id);

    public MixerChannelId AsMixerChannel() => new(Id);

    /// <summary>
    /// Whether this node is a point inside <paramref name="chain"/>: its input, the output of one of its devices, or
    /// an input port of one of its devices. The chain's owner (its track or rack) is not inside it.
    /// </summary>
    public bool IsInside(DeviceChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return Kind switch
        {
            SignalNodeKind.Device or SignalNodeKind.DeviceInput => chain.Find(AsDevice()) is not null,
            SignalNodeKind.ChainInput => Id == chain.Id.Value,
            _ => false,
        };
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}:{Id}{(Port is null ? string.Empty : "/" + Port)}");
}

/// <summary>
/// A directed route for one kind of signal, from a source to a destination (ADR 0023). The device strip
/// and the routing inspector both read and write this list and nothing else, so they cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// Sources are junctions of a chain: its input (before the first device), the point after one device (a tap), or its
/// output (a track's or rack's). Destinations are a track's or rack's input, an input port of a device inside a chain
/// (a receiver or a sidechain), an external instrument's port, a mixer channel, or the master bus. One source may have
/// many connections (one-to-many) and one destination may receive many (many-to-one); several tracks
/// feeding one instrument or one mixer channel is the ordinary case, not a special one.
/// </para>
/// <para>
/// Whether a connection is meaningful (does the source carry this signal, would it make a loop) is
/// checked by <see cref="SignalRoutingValidator"/>, not by the constructor, so a project that refers to a
/// plugin that is not installed can still be loaded and kept.
/// </para>
/// </remarks>
public sealed record SignalConnection
{
    public required ConnectionId Id { get; init; }

    public required SignalKind Kind { get; init; }

    public required SignalNode Source { get; init; }

    public required SignalNode Destination { get; init; }

    /// <summary>Channel handling for event connections. Ignored for audio.</summary>
    public ChannelMapping Mapping { get; init; } = ChannelMapping.Preserve;

    /// <summary>
    /// A voice to select on the destination part before the first event, for connections to an external
    /// instrument. Sent on the part's channel at tick 0, ahead of the track's own events.
    /// </summary>
    public VoiceAssignment? Voice { get; init; }

    public static SignalConnection Create(SignalKind kind, SignalNode source, SignalNode destination) =>
        new() { Id = ConnectionId.New(), Kind = kind, Source = source, Destination = destination };
}
