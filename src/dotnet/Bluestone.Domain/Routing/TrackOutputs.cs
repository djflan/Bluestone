using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Domain.Routing;

/// <summary>
/// The everyday way to send a track to hardware: an instrument (by endpoint and profile), a channel, a
/// transposition, and a voice. This is how the track inspector and format 3 projects describe a track's
/// output. It is not stored as such: <see cref="TrackOutputs"/> reads it from, and writes it into, the
/// project's one routing model (an <see cref="ExternalInstrument"/>, a <see cref="SignalConnection"/>, and a
/// Transpose device in the track's chain), so the inspector and the routing inspector cannot disagree.
/// </summary>
public sealed record TrackOutput
{
    private readonly int _transpose;

    public static TrackOutput None { get; } = new();

    /// <summary>The profile describing the instrument: what it understands and its voices.</summary>
    public ProfileReference? Profile { get; init; }

    /// <summary>The endpoint the instrument is connected to.</summary>
    public EndpointReference? Endpoint { get; init; }

    /// <summary>When set, every channel event goes out on this channel.</summary>
    public MidiChannel? Channel { get; init; }

    /// <summary>Semitones added to every note, from -48 to 48.</summary>
    public int Transpose
    {
        get => _transpose;
        init => _transpose = value is >= -BuiltInDevices.MaxTranspose and <= BuiltInDevices.MaxTranspose
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Transpose), value, "Transpose must be between -48 and 48 semitones.");
    }

    /// <summary>A voice selected on the part before the track's own events.</summary>
    public VoiceAssignment? Voice { get; init; }

    /// <summary>Whether the output names an instrument or part settings, and so needs a connection.</summary>
    public bool HasPart => Profile is not null || Endpoint is not null || Channel is not null || Voice is not null;
}

/// <summary>Reads and writes a track's <see cref="TrackOutput"/> in the routing model (ADR 0023).</summary>
public static class TrackOutputs
{
    public const string UnassignedName = "Unassigned";

    /// <summary>
    /// The connection the inspector shows and edits: the track's first event connection to an external
    /// instrument. Other connections (to tracks, racks, or more instruments) are left to the routing inspector.
    /// </summary>
    public static SignalConnection? PrimaryConnection(Project project, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(project);
        var source = SignalNode.Track(track);
        foreach (var connection in project.Connections)
        {
            if (connection.Source == source && connection.Kind == SignalKind.Events && connection.Destination.Kind == SignalNodeKind.ExternalInstrument)
            {
                return connection;
            }
        }

        return null;
    }

    /// <summary>The first Transpose device of the track's chain, if it has one.</summary>
    public static DeviceInstance? TransposeDevice(Project project, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.ChainOf(track)?.Devices.FirstOrDefault(BuiltInDevices.IsTranspose);
    }

    public static TrackOutput Read(Project project, TrackId track)
    {
        ArgumentNullException.ThrowIfNull(project);
        var transpose = TransposeDevice(project, track) is { IsBypassed: false } device ? BuiltInDevices.TransposeOf(device) : 0;
        if (PrimaryConnection(project, track) is not { } connection || project.FindInstrument(connection.Destination.AsInstrument()) is not { } instrument)
        {
            return new TrackOutput { Transpose = transpose };
        }

        return new TrackOutput
        {
            Profile = instrument.Profile,
            Endpoint = instrument.FindPort(connection.Destination.Port)?.Endpoint,
            Channel = connection.Mapping.Force,
            Transpose = transpose,
            Voice = connection.Voice,
        };
    }

    /// <summary>
    /// Writes <paramref name="output"/> for <paramref name="track"/>. The instrument is the one with the same
    /// endpoint and profile, created when there is none (named after the endpoint, or
    /// <see cref="UnassignedName"/>); the primary connection is retargeted or created, keeping its channel
    /// filter; the transposition goes to the chain's first Transpose device, added at the end of the chain
    /// when needed. Nothing else in the chain or the routing changes, and no instrument is removed.
    /// </summary>
    public static Project Write(Project project, TrackId track, TrackOutput output)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        if (output.Transpose != Read(project, track).Transpose)
        {
            project = WriteTranspose(project, track, output.Transpose);
        }

        var existing = PrimaryConnection(project, track);
        if (!output.HasPart)
        {
            return existing is null ? project : project with { Connections = project.Connections.Remove(existing) };
        }

        var preferred = existing is null ? null : project.FindInstrument(existing.Destination.AsInstrument());
        var (instrument, port) = FindInstrument(project, preferred, output.Endpoint, output.Profile);
        if (instrument is null)
        {
            instrument = ExternalInstrument.Create(NameFor(project, output), output.Endpoint, output.Profile);
            port = instrument.Ports[0];
            project = project with { Instruments = project.Instruments.Add(instrument) };
        }

        var mapping = existing?.Mapping ?? ChannelMapping.Preserve;
        mapping = output.Channel is null
            ? mapping with { Force = null }
            : mapping with { Force = output.Channel, Remap = [] };
        var connection = (existing ?? SignalConnection.Create(SignalKind.Events, SignalNode.Track(track), SignalNode.ExternalPart(instrument.Id, port!.Id))) with
        {
            Destination = SignalNode.ExternalPart(instrument.Id, port!.Id),
            Mapping = mapping,
            Voice = output.Voice,
        };

        return project with
        {
            Connections = existing is null ? project.Connections.Add(connection) : project.Connections.Replace(existing, connection),
        };
    }

    private static Project WriteTranspose(Project project, TrackId track, int semitones)
    {
        var chain = project.ChainOf(track);
        if (chain?.Devices.FirstOrDefault(BuiltInDevices.IsTranspose) is { } device)
        {
            return project.WithChain(chain.Replace(BuiltInDevices.WithTranspose(device, semitones) with { IsBypassed = false }));
        }

        if (semitones == 0)
        {
            return project;
        }

        chain ??= DeviceChain.Create(ChainOwner.ForTrack(track));
        return project.WithChain(chain.Add(BuiltInDevices.CreateTranspose(semitones)));
    }

    private static (ExternalInstrument? Instrument, ExternalPort? Port) FindInstrument(Project project, ExternalInstrument? preferred, EndpointReference? endpoint, ProfileReference? profile)
    {
        IEnumerable<ExternalInstrument> candidates = preferred is null ? project.Instruments : [preferred, .. project.Instruments];
        foreach (var instrument in candidates)
        {
            if (instrument.Profile?.ProfileId != profile?.ProfileId)
            {
                continue;
            }

            foreach (var port in instrument.Ports)
            {
                if (SameEndpoint(port.Endpoint, endpoint))
                {
                    return (instrument, port);
                }
            }
        }

        return (null, null);
    }

    private static bool SameEndpoint(EndpointReference? left, EndpointReference? right) =>
        left is null ? right is null : right is not null && left.ProviderId == right.ProviderId && left.EndpointKey == right.EndpointKey;

    private static string NameFor(Project project, TrackOutput output)
    {
        var name = output.Endpoint is { } endpoint ? endpoint.DisplayName ?? endpoint.EndpointKey : UnassignedName;
        if (output.Profile is { } profile && project.Instruments.Any(i => i.Name == name))
        {
            name = $"{name} ({profile.DisplayName ?? profile.ProfileId})";
        }

        return name.Length <= ExternalInstrument.MaxNameLength ? name : name[..ExternalInstrument.MaxNameLength];
    }
}
