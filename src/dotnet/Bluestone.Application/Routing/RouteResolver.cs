using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Profiles;

namespace Bluestone.Application.Routing;

public enum EndpointBindingKind
{
    /// <summary>The port names no endpoint.</summary>
    Unassigned,

    /// <summary>The endpoint with the stored stable ID is present.</summary>
    Bound,

    /// <summary>
    /// The stored ID is gone, but exactly one endpoint from the same provider has the remembered name.
    /// Playback uses it; the UI should offer to confirm the rebinding.
    /// </summary>
    BoundByName,

    /// <summary>Nothing matches; the port is kept and waits for the endpoint or an explicit rebinding.</summary>
    Missing,

    /// <summary>Several endpoints share the remembered name; the user must choose.</summary>
    Ambiguous,
}

public enum ProfileBindingKind
{
    Unassigned,
    Resolved,
    Missing,
}

public sealed record EndpointResolution(EndpointBindingKind Kind, EndpointDescriptor? Endpoint, ImmutableArray<EndpointDescriptor> Candidates)
{
    public bool IsUsable => Kind is EndpointBindingKind.Bound or EndpointBindingKind.BoundByName;
}

public sealed record ProfileResolution(ProfileBindingKind Kind, DeviceProfile? Profile);

/// <summary>One port of an external instrument, checked against the endpoints available now.</summary>
public sealed record ResolvedPort(ExternalPort Port, EndpointResolution Endpoint, ImmutableArray<string> Problems);

/// <summary>An external instrument checked against the installed profiles and the endpoints available now.</summary>
public sealed record ResolvedInstrument(ExternalInstrument Instrument, ProfileResolution Profile, ImmutableArray<ResolvedPort> Ports)
{
    public ResolvedPort? FindPort(string? id) => Instrument.FindPort(id) is { } port ? Ports.FirstOrDefault(p => p.Port.Id == port.Id) : null;
}

/// <summary>
/// Where live input and auditioned notes for a track go: the first playable external instrument reached
/// from the track through its connections (and the chains of the tracks and racks on the way), the channel
/// the last forcing connection sets, and the Transpose devices on the way added up. Other devices in those
/// chains are not applied to live notes (they run when the plan is compiled).
/// </summary>
public sealed record LiveRoute(EndpointDescriptor Endpoint, MidiChannel? Channel, int Transpose, ResolvedInstrument Instrument);

/// <summary>A track's output as the inspector shows it, checked now, with plain-language problems.</summary>
/// <param name="Track">The track.</param>
/// <param name="Output">The track's simple output settings (<see cref="TrackOutputs"/>).</param>
/// <param name="Instrument">The instrument of the track's primary connection, if it has one.</param>
/// <param name="Port">The port of that connection.</param>
/// <param name="Live">Where live and auditioned notes go, or null when nothing playable is reached.</param>
/// <param name="Problems">Problems in words a musician can read.</param>
public sealed record ResolvedTrackOutput(TrackId Track, TrackOutput Output, ResolvedInstrument? Instrument, ResolvedPort? Port, LiveRoute? Live, ImmutableArray<string> Problems)
{
    /// <summary>Whether the primary connection reaches an endpoint that is available now.</summary>
    public bool CanPlay => Port?.Endpoint.IsUsable == true;
}

/// <summary>
/// Resolves the project's external instruments against the current profile catalog and endpoints.
/// Profiles are found by ID only; endpoints by stable ID, then by remembered name within the same
/// provider. Endpoint names are never used to pick a profile, and a missing profile never prevents playback.
/// </summary>
public static class RouteResolver
{
    public const string NoOutputProblem = "No output is assigned; the track will not play.";

    public static ImmutableArray<ResolvedInstrument> ResolveInstruments(Project project, ProfileCatalog profiles, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Instruments.Select(i => Resolve(i, profiles, endpoints))];
    }

    public static ResolvedInstrument Resolve(ExternalInstrument instrument, ProfileCatalog profiles, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(endpoints);
        var ports = instrument.Ports.Select(port =>
        {
            var endpoint = ResolveEndpoint(port.Endpoint, endpoints);
            ImmutableArray<string> problems = endpoint.Kind switch
            {
                EndpointBindingKind.Unassigned => [NoOutputProblem],
                EndpointBindingKind.BoundByName => [$"\"{endpoint.Endpoint!.DisplayName}\" was matched by name because its ID changed. Confirm the connection to keep it."],
                EndpointBindingKind.Missing => [$"\"{port.Endpoint!.DisplayName ?? port.Endpoint.EndpointKey}\" is not connected. Reconnect it or choose another output."],
                EndpointBindingKind.Ambiguous => [$"{endpoint.Candidates.Length} outputs are named \"{port.Endpoint!.DisplayName}\". Choose one."],
                _ => [],
            };
            return new ResolvedPort(port, endpoint, problems);
        });

        return new ResolvedInstrument(instrument, ResolveProfile(instrument.Profile, profiles), [.. ports]);
    }

    /// <summary>Every track's output, in track order.</summary>
    public static ImmutableArray<ResolvedTrackOutput> ResolveTracks(Project project, ImmutableArray<ResolvedInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Sequence.Tracks.Select(t => ResolveTrack(project, t.Id, instruments))];
    }

    public static ResolvedTrackOutput ResolveTrack(Project project, TrackId track, ImmutableArray<ResolvedInstrument> instruments)
    {
        ArgumentNullException.ThrowIfNull(project);
        var output = TrackOutputs.Read(project, track);
        var problems = ImmutableArray.CreateBuilder<string>();
        ResolvedInstrument? instrument = null;
        ResolvedPort? port = null;
        if (TrackOutputs.PrimaryConnection(project, track) is { } connection)
        {
            instrument = instruments.FirstOrDefault(i => i.Instrument.Id == connection.Destination.AsInstrument());
            port = instrument?.FindPort(connection.Destination.Port);
            if (instrument is not null && port is not null)
            {
                problems.AddRange(ProblemsOf(instrument, port, connection.Voice));
            }
        }

        var live = FindLiveRoute(project, track, instruments);
        if (port is null && live is null && !HasOtherDestination(project, track))
        {
            problems.Add(NoOutputProblem);
        }

        return new ResolvedTrackOutput(track, output, instrument, port, live, problems.ToImmutable());
    }

    /// <summary>The problems with sending to <paramref name="port"/> of <paramref name="instrument"/>, selecting <paramref name="voice"/>.</summary>
    public static IEnumerable<string> ProblemsOf(ResolvedInstrument instrument, ResolvedPort port, VoiceAssignment? voice)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(port);
        if (instrument.Profile.Kind == ProfileBindingKind.Missing)
        {
            var reference = instrument.Instrument.Profile!;
            yield return $"Profile \"{reference.DisplayName ?? reference.ProfileId}\" is not installed; notes still play, but voice names and setup messages are unavailable.";
        }

        foreach (var problem in port.Problems)
        {
            yield return problem;
        }

        if (voice is not null && instrument.Profile.Profile is { } profile && profile.FindBank(voice.BankId) is null)
        {
            yield return $"Bank \"{voice.BankId}\" is not defined by profile \"{profile.Name}\"; the voice will not be selected.";
        }
    }

    /// <summary>Creates a stored reference for an endpoint, capturing its identity hints.</summary>
    public static EndpointReference ReferenceTo(EndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new EndpointReference(endpoint.Id.Provider, endpoint.Id.Value, endpoint.DisplayName, endpoint.Manufacturer, endpoint.Model);
    }

    public static ProfileReference ReferenceTo(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileReference(profile.Id, profile.Name);
    }

    public static EndpointResolution ResolveEndpoint(EndpointReference? reference, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (reference is null)
        {
            return new EndpointResolution(EndpointBindingKind.Unassigned, null, []);
        }

        var outputs = endpoints.Where(e => e.Direction == EndpointDirection.Output).ToList();
        var exact = outputs.FirstOrDefault(e => e.Id.Provider == reference.ProviderId && e.Id.Value == reference.EndpointKey);
        if (exact is not null)
        {
            return new EndpointResolution(EndpointBindingKind.Bound, exact, []);
        }

        if (reference.DisplayName is { Length: > 0 } name)
        {
            var byName = outputs
                .Where(e => e.Id.Provider == reference.ProviderId && string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
            if (byName.Length == 1)
            {
                return new EndpointResolution(EndpointBindingKind.BoundByName, byName[0], byName);
            }

            if (byName.Length > 1)
            {
                return new EndpointResolution(EndpointBindingKind.Ambiguous, null, byName);
            }
        }

        return new EndpointResolution(EndpointBindingKind.Missing, null, []);
    }

    private static ProfileResolution ResolveProfile(ProfileReference? reference, ProfileCatalog profiles)
    {
        if (reference is null)
        {
            return new ProfileResolution(ProfileBindingKind.Unassigned, null);
        }

        return profiles.Find(reference.ProfileId) is { } profile
            ? new ProfileResolution(ProfileBindingKind.Resolved, profile)
            : new ProfileResolution(ProfileBindingKind.Missing, null);
    }

    // Breadth-first from the track along event connections out of chain ends, so the nearest instrument wins.
    private static LiveRoute? FindLiveRoute(Project project, TrackId track, ImmutableArray<ResolvedInstrument> instruments)
    {
        var queue = new Queue<(SignalNode Node, MidiChannel? Channel, int Transpose)>();
        var seen = new HashSet<SignalNode>();
        var start = SignalNode.Track(track);
        queue.Enqueue((start, null, 0));
        seen.Add(start);
        while (queue.Count > 0)
        {
            var (node, channel, transpose) = queue.Dequeue();
            var chain = node.Kind == SignalNodeKind.Track ? project.ChainOf(node.AsTrack()) : project.FindChain(node.AsRack());
            transpose += chain?.Devices.Where(d => !d.IsBypassed && BuiltInDevices.IsTranspose(d)).Sum(BuiltInDevices.TransposeOf) ?? 0;
            foreach (var connection in project.ConnectionsFrom(node).Where(c => c.Kind == SignalKind.Events))
            {
                var forced = connection.Mapping.Force ?? channel;
                var destination = connection.Destination;
                if (destination.Kind == SignalNodeKind.ExternalInstrument)
                {
                    var instrument = instruments.FirstOrDefault(i => i.Instrument.Id == destination.AsInstrument());
                    if (instrument?.FindPort(destination.Port) is { Endpoint.IsUsable: true } port)
                    {
                        return new LiveRoute(port.Endpoint.Endpoint!, forced, transpose, instrument);
                    }
                }
                else if (destination.Kind is SignalNodeKind.Track or SignalNodeKind.Rack && seen.Add(destination))
                {
                    queue.Enqueue((destination, forced, transpose));
                }
            }
        }

        return null;
    }

    private static bool HasOtherDestination(Project project, TrackId track) =>
        project.ConnectionsFrom(SignalNode.Track(track)).Any(c => c.Kind == SignalKind.Events && c.Destination.Kind != SignalNodeKind.ExternalInstrument)
        || (project.ChainOf(track) is { } chain && project.Connections.Any(c => c.Source.Kind == SignalNodeKind.Device && chain.Find(c.Source.AsDevice()) is not null));
}
