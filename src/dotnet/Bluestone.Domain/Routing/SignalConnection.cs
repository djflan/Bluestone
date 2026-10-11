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

    /// <summary>The external instrument port; null means the instrument's first port.</summary>
    public string? Port { get; }

    public static SignalNode Track(TrackId track) => new(SignalNodeKind.Track, track.Value, null);

    public static SignalNode Rack(DeviceChainId chain) => new(SignalNodeKind.Rack, chain.Value, null);

    public static SignalNode Device(DeviceId device) => new(SignalNodeKind.Device, device.Value, null);

    public static SignalNode ExternalPart(ExternalInstrumentId instrument, string? port = null) => new(SignalNodeKind.ExternalInstrument, instrument.Value, port);

    public static SignalNode Mixer(MixerChannelId channel) => new(SignalNodeKind.MixerChannel, channel.Value, null);

    public static SignalNode Master { get; } = new(SignalNodeKind.Master, Guid.Empty, null);

    public TrackId AsTrack() => new(Id);

    public DeviceChainId AsRack() => new(Id);

    public DeviceId AsDevice() => new(Id);

    public ExternalInstrumentId AsInstrument() => new(Id);

    public MixerChannelId AsMixerChannel() => new(Id);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind}:{Id}{(Port is null ? string.Empty : "/" + Port)}");
}

/// <summary>
/// A directed route for one kind of signal, from a source to a destination (ADR 0023). The device strip
/// and the routing inspector both read and write this list and nothing else, so they cannot disagree.
/// </summary>
/// <remarks>
/// <para>
/// Sources are a track's output, a rack's output, or a tap after one device. Destinations are a track's or
/// rack's input, an external instrument's port, a mixer channel, or the master bus. One source may have
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
