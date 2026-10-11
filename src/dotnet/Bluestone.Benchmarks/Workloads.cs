using Bluestone.Application.Routing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Benchmarks;

/// <summary>Deterministic synthetic material for measurements.</summary>
internal static class Workloads
{
    public static readonly Ppqn Resolution = new(960);

    /// <summary>A dense sequence: <paramref name="tracks"/> tracks of sixteenth notes plus controller automation.</summary>
    public static Sequence Dense(int tracks, int notesPerTrack)
    {
        var random = new Random(1234);
        var sixteenth = Resolution.TicksPerQuarterNote / 4;
        var sequence = Sequence.CreateEmpty(Resolution);
        for (var t = 0; t < tracks; t++)
        {
            var channel = MidiChannel.FromIndex(t % 16);
            var events = new List<TrackEvent>(notesPerTrack * 2);
            for (var i = 0; i < notesPerTrack; i++)
            {
                var tick = new Tick((long)i * sixteenth);
                events.Add(new NoteEvent(EventId.New(), tick, new TickSpan(sixteenth - 10), channel, new NoteNumber(36 + random.Next(48)), new Velocity(40 + random.Next(87)), Velocity.DefaultRelease));
                events.Add(new ControllerEvent(tick, channel, ControllerNumber.ModulationWheel, ControlValue.FromSevenBit(random.Next(128))));
            }

            sequence = sequence.WithTrack(Track.FromEvents(TrackId.New(), $"Track {t + 1}", events));
        }

        return sequence;
    }

    public static readonly EndpointDescriptor Sink = new(new EndpointId("bench", "sink"), "Sink", EndpointDirection.Output, EndpointTransport.Test, EndpointCapabilities.None);

    public static Project ProjectFor(Sequence sequence) => Project.CreateNew("Benchmark") with { Sequence = sequence };

    /// <summary>The sequence with every track sent to one external instrument on <see cref="Sink"/>.</summary>
    public static Project Routed(Sequence sequence)
    {
        var endpoint = new EndpointReference(Sink.Id.Provider, Sink.Id.Value, Sink.DisplayName);
        return sequence.Tracks.Aggregate(ProjectFor(sequence), (project, track) => TrackOutputs.Write(project, track.Id, new TrackOutput { Endpoint = endpoint }));
    }

    /// <summary>What the controller does after every edit: evaluate the routing and the signal graph, then compile.</summary>
    public static PlaybackPlan Compile(Project project) =>
        PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, ProfileCatalog.Empty, [Sink]).Compile(project.Sequence);
}

/// <summary>An output that accepts everything and only counts, so measurements see Bluestone's own cost.</summary>
internal sealed class CountingOutput(EndpointCapabilities capabilities) : IMidiOutput
{
    public long Count;

    public EndpointDescriptor Endpoint { get; } = new(new EndpointId("bench", "sink"), "Sink", EndpointDirection.Output, EndpointTransport.Test, capabilities);

    public EndpointState State => EndpointState.Open;

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        Count++;
        return SendResult.Sent;
    }

    public void Dispose()
    {
    }
}
