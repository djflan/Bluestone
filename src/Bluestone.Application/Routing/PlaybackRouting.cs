using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Application.Routing;

/// <summary>Everything playback needs from a project's routing, worked out against what is available now.</summary>
/// <param name="Slots">One endpoint per output slot; parts sending to the same endpoint share a slot.</param>
/// <param name="Parts">One plan part per external-instrument connection whose endpoint is available.</param>
/// <param name="Diagnostics">What will not play, and why, by track.</param>
/// <param name="Instruments">Every external instrument, resolved.</param>
/// <param name="Tracks">Every track's output, resolved, in track order.</param>
/// <param name="Graph">The evaluated signal graph, including software-instrument feeds and parameter feeds.</param>
public sealed record PreparedRouting(
    ImmutableArray<EndpointId> Slots,
    ImmutableArray<PlanPart> Parts,
    ImmutableArray<PlanDiagnostic> Diagnostics,
    ImmutableArray<ResolvedInstrument> Instruments,
    ImmutableArray<ResolvedTrackOutput> Tracks,
    SignalGraphResult Graph)
{
    /// <summary>Translated external instrument parameter automation (ADR 0031), after <see cref="Parts"/> in plan order.</summary>
    public ImmutableArray<ParameterPart> ParameterParts { get; init; } = [];

    public ResolvedTrackOutput? FindTrack(TrackId track) => Tracks.FirstOrDefault(t => t.Track == track);

    public PlaybackPlan Compile(Sequence sequence) => PlaybackPlanCompiler.Compile(sequence, Parts, Diagnostics, ParameterParts);
}

/// <summary>Outputs opened for a <see cref="PreparedRouting"/>. Disposing closes every port.</summary>
public sealed class OpenedOutputs : IDisposable
{
    internal OpenedOutputs(IMidiOutput?[] outputs, ImmutableArray<string> problems)
    {
        Outputs = outputs;
        Problems = problems;
    }

    /// <summary>One output per slot; <see langword="null"/> where opening failed.</summary>
    public IReadOnlyList<IMidiOutput?> Outputs { get; }

    public ImmutableArray<string> Problems { get; }

    public void Dispose()
    {
        foreach (var output in Outputs)
        {
            output?.Dispose();
        }
    }
}

/// <summary>Turns a project's routing into playback parts and open outputs (ADR 0023).</summary>
public static class PlaybackRouting
{
    public const string NotRoutedMessage = "The track is not routed to an output and will not play.";

    /// <summary>
    /// Evaluates the signal graph, gives each distinct available endpoint an output slot (in track order of
    /// the connections that use it), and turns each external-instrument part into a plan part, with its
    /// voice selection from the instrument's profile. Parts whose endpoint is unavailable are left out, and a
    /// playing track that then reaches nothing is reported as not routed. External instrument parameter lanes are
    /// translated into MIDI for their parts (<see cref="HardwareAutomation"/>).
    /// </summary>
    public static PreparedRouting Prepare(Project project, DeviceCatalog devices, ProfileCatalog profiles, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(devices);
        var sequence = project.Sequence;
        var instruments = RouteResolver.ResolveInstruments(project, profiles, endpoints);
        var graph = SignalGraph.Evaluate(project, devices);
        var trackIndex = sequence.Tracks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var connectionIndex = new Dictionary<ConnectionId, int>();
        var connections = new Dictionary<ConnectionId, SignalConnection>();
        for (var i = 0; i < project.Connections.Length; i++)
        {
            connectionIndex.TryAdd(project.Connections[i].Id, i);
            connections.TryAdd(project.Connections[i].Id, project.Connections[i]);
        }


        var slots = new List<EndpointId>();
        var parts = ImmutableArray.CreateBuilder<PlanPart>();
        var reached = new HashSet<TrackId>();
        var unplayable = new HashSet<TrackId>();
        var feeds = graph.ExternalParts
            .OrderBy(f => SourceOrder(project, connections[f.Connection].Source, trackIndex))
            .ThenBy(f => connectionIndex[f.Connection]);
        foreach (var feed in feeds)
        {
            var connection = connections[feed.Connection];
            var sourceTrack = SourceTrack(project, connection.Source);
            var instrument = instruments.First(i => i.Instrument.Id == feed.Instrument);
            if (instrument.FindPort(feed.Port) is not { Endpoint.IsUsable: true } port)
            {
                if (sourceTrack is { } unrouted)
                {
                    unplayable.Add(unrouted);
                }

                continue;
            }

            if (sourceTrack is { } routed)
            {
                reached.Add(routed);
            }

            var endpoint = port.Endpoint.Endpoint!.Id;
            var slot = slots.IndexOf(endpoint);
            if (slot < 0)
            {
                slots.Add(endpoint);
                slot = slots.Count - 1;
            }

            parts.Add(new PlanPart(slot, feed.Events) { InitialEvents = VoiceSelection(project, feed, connection, instrument, sourceTrack, trackIndex) });
        }

        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        foreach (var diagnostic in graph.Diagnostics)
        {
            if (TrackOf(project, diagnostic) is { } track && trackIndex.ContainsKey(track))
            {
                diagnostics.Add(new PlanDiagnostic(track, diagnostic.Message));
            }
        }

        var anySolo = sequence.Tracks.Any(t => t.IsSoloed);
        foreach (var track in sequence.Tracks.Where(t => unplayable.Contains(t.Id) && !reached.Contains(t.Id)))
        {
            var plays = anySolo ? track.IsSoloed : !track.IsMuted;
            var elsewhere = graph.SoftwareInstruments.Any(i => project.ChainOf(track.Id)?.Id == i.Chain)
                || project.ConnectionsFrom(SignalNode.Track(track.Id)).Any(c => c.Kind == SignalKind.Events && c.Destination.Kind != SignalNodeKind.ExternalInstrument);
            if (plays && !elsewhere)
            {
                diagnostics.Add(new PlanDiagnostic(track.Id, NotRoutedMessage));
            }
        }

        // Hardware parameter automation goes to its part's endpoint; a new endpoint gets a slot after the others.
        var parameterParts = HardwareAutomation.Plan(project, instruments, slots, diagnostics);
        return new PreparedRouting([.. slots], parts.ToImmutable(), diagnostics.ToImmutable(), instruments, RouteResolver.ResolveTracks(project, instruments), graph)
        {
            ParameterParts = parameterParts,
        };
    }

    /// <summary>Opens one output per slot. Failures leave the slot empty and are reported, not thrown.</summary>
    public static async Task<OpenedOutputs> OpenAsync(PreparedRouting routing, EndpointDirectory directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(directory);
        var outputs = new IMidiOutput?[routing.Slots.Length];
        var problems = ImmutableArray.CreateBuilder<string>();
        try
        {
            for (var i = 0; i < outputs.Length; i++)
            {
                try
                {
                    outputs[i] = await directory.OpenOutputAsync(routing.Slots[i], cancellationToken).ConfigureAwait(false);
                }
                catch (EndpointUnavailableException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }
        catch
        {
            foreach (var output in outputs)
            {
                output?.Dispose();
            }

            throw;
        }

        return new OpenedOutputs(outputs, problems.ToImmutable());
    }

    // The voice is selected on the part's channel: the connection's forced channel, else the channel the
    // source track's first event is sent on, else channel 1. It goes ahead of everything at tick 0.
    private static ImmutableArray<SignalEvent> VoiceSelection(Project project, ExternalPartFeed feed, SignalConnection connection, ResolvedInstrument instrument, TrackId? sourceTrack, Dictionary<TrackId, int> trackIndex)
    {
        if (feed.Voice is not { } voice || instrument.Profile.Profile?.FindBank(voice.BankId) is not { } bank)
        {
            return [];
        }

        var first = sourceTrack is { } t ? project.Sequence.FindTrack(t)?.FirstChannel : null;
        var channel = feed.ForcedChannel
            ?? (first is { } written && connection.Mapping.TryMap(written, out var mapped) ? mapped : first)
            ?? MidiChannel.FromIndex(0);
        var origin = sourceTrack is { } track && trackIndex.TryGetValue(track, out var index) ? index : -1;
        return [new SignalEvent(new ProgramEvent(Tick.Zero, channel, bank.Select(voice.Program)), origin, -1)];
    }

    // Parts are ordered by the track they come from (taps count as their chain's track), racks last.
    private static int SourceOrder(Project project, SignalNode source, Dictionary<TrackId, int> trackIndex) =>
        SourceTrack(project, source) is { } track && trackIndex.TryGetValue(track, out var index) ? index : int.MaxValue;

    private static TrackId? SourceTrack(Project project, SignalNode source) => source.Kind switch
    {
        SignalNodeKind.Track => source.AsTrack(),
        SignalNodeKind.Device when project.FindDevice(source.AsDevice()) is { Chain.Owner.Kind: ChainOwnerKind.Track } found => found.Chain.Owner.Track,
        _ => null,
    };

    // A diagnostic about a device belongs to the track whose chain holds it; about a connection, to its source track.
    private static TrackId? TrackOf(Project project, SignalDiagnostic diagnostic) =>
        diagnostic.Track
        ?? (diagnostic.Device is { } device && project.FindDevice(device) is { Chain.Owner.Kind: ChainOwnerKind.Track } found ? found.Chain.Owner.Track : (TrackId?)null)
        ?? (diagnostic.Connection is { } id && project.Connections.FirstOrDefault(c => c.Id == id) is { } connection ? SourceTrack(project, connection.Source) : null);
}
