using Bluestone.Application.Routing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;
using static Bluestone.Tests.Unit.Application.RouteResolverTests;

namespace Bluestone.Tests.Unit.Application;

public sealed class PlaybackRoutingTests
{
    private static readonly MidiChannel One = MidiChannel.FromIndex(0);

    private static NoteEvent Note(long at, int note) => new(new Tick(at), new TickSpan(10), One, new NoteNumber(note), Velocity.Max);

    private static Project ProjectWith(Sequence sequence, params (Track Track, TrackOutput Output)[] outputs) =>
        outputs.Aggregate(Project.CreateNew() with { Sequence = sequence }, (project, x) => TrackOutputs.Write(project, x.Track.Id, x.Output));

    [Fact]
    public void Prepare_SharesSlotsPerEndpointAndSkipsUnplayableParts()
    {
        var a = Track.Create("a");
        var b = Track.FromEvents(TrackId.New(), "b", [Note(0, 60)]);
        var c = Track.Create("c");
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(a).WithTrack(b).WithTrack(c);
        var synth = Output("loopback", "s", "Synth");
        var project = ProjectWith(
            sequence,
            (a, new TrackOutput { Endpoint = RouteResolver.ReferenceTo(synth) }),
            (b, new TrackOutput { Endpoint = RouteResolver.ReferenceTo(synth), Channel = MidiChannel.FromNumber(2) }),
            (c, new TrackOutput { Endpoint = new EndpointReference("loopback", "gone", "Gone") }));

        var prepared = PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, ProfileCatalog.Empty, [synth]);

        Assert.Equal([synth.Id], prepared.Slots);
        Assert.Equal(2, prepared.Parts.Length);
        Assert.All(prepared.Parts, p => Assert.Equal(0, p.OutputSlot));
        Assert.Equal(MidiChannel.FromNumber(2), ((NoteEvent)Assert.Single(prepared.Parts[1].Events).Event).Channel);
        var diagnostic = Assert.Single(prepared.Diagnostics);
        Assert.Equal((c.Id, PlaybackRouting.NotRoutedMessage), (diagnostic.Track, diagnostic.Message));
    }

    [Fact]
    public void Prepare_RendersVoiceSelectionFromTheInstrumentsProfile()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 60)]);
        var synth = Output("loopback", "s", "Synth");
        var project = ProjectWith(Sequence.CreateEmpty(Ppqn.Default).WithTrack(track), (track, new TrackOutput
        {
            Endpoint = RouteResolver.ReferenceTo(synth),
            Profile = new ProfileReference("p.one"),
            Voice = new VoiceAssignment("main", ProgramNumber.FromNumber(5)),
            Channel = MidiChannel.FromNumber(3),
        }));

        var prepared = PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, Catalog("p.one"), [synth]);

        var voice = Assert.IsType<ProgramEvent>(Assert.Single(prepared.Parts[0].InitialEvents).Event);
        Assert.Equal(["B2 0 0", "B2 32 3", "C2 4 0"], Midi1Encoder.Encode(voice).Select(m => $"{m.Status:X2} {m.Data1} {m.Data2}"));
    }

    [Fact]
    public void Compile_PlacesVoiceSelectionBeforeTheTracksOwnEvents()
    {
        var program = new ProgramEvent(Tick.Zero, One, new ProgramSelection(new ProgramNumber(9)));
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 60), program]);
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track);
        var part = new PlanPart(0, [.. track.ArrangedEvents.Select((e, i) => new SignalEvent(e, 0, i))])
        {
            InitialEvents = [new SignalEvent(new ProgramEvent(Tick.Zero, One, new ProgramSelection(new ProgramNumber(2), BankMsb: new SevenBitValue(1))), 0, -1)],
        };

        var plan = PlaybackPlanCompiler.Compile(sequence, [part]);

        Assert.Equal(["B0 0", "C0 2", "C0 9", "90 3C"], plan.Events.Select(e => $"{e.Message.Status:X2} {e.Message.Data1:X}"));
    }

    [Fact]
    public void Prepare_TransposesThroughTheTracksChain_AndReportsNotesDropped()
    {
        var rawOff = new NoteOffEvent(EventId.New(), new Tick(5), One, new NoteNumber(100), Velocity.DefaultRelease);
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 60), Note(1, 120), rawOff]);
        var synth = Output("loopback", "s", "Synth");
        var project = ProjectWith(Sequence.CreateEmpty(Ppqn.Default).WithTrack(track), (track, new TrackOutput { Endpoint = RouteResolver.ReferenceTo(synth), Transpose = 12 }));

        var plan = PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, ProfileCatalog.Empty, [synth]).Compile(project.Sequence);

        Assert.Equal([72, 112], plan.Events.Select(e => (int)e.Message.Data1));
        var diagnostic = Assert.Single(plan.Diagnostics);
        Assert.Equal(track.Id, diagnostic.Track);
        Assert.Contains("1 notes were transposed outside", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_RoutedTrackPlaysThroughTheLoopbackWithItsVoice()
    {
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        var port = provider.CreatePort("Module", "m");
        using var directory = new EndpointDirectory([provider]);

        var track = Track.FromEvents(TrackId.New(), "Bass", [Note(0, 40)]);
        var sequence = Sequence.CreateEmpty(new Ppqn(500)).WithTrack(track);
        var project = ProjectWith(sequence, (track, new TrackOutput
        {
            Endpoint = RouteResolver.ReferenceTo(directory.GetEndpoints(EndpointDirection.Output).Single()),
            Profile = new ProfileReference("p.one"),
            Voice = new VoiceAssignment("main", ProgramNumber.FromNumber(34)),
        }));

        var prepared = PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, Catalog("p.one"), directory.GetEndpoints());
        using var outputs = await PlaybackRouting.OpenAsync(prepared, directory, TestContext.Current.CancellationToken);
        using var engine = new PlaybackEngine(clock, sequence.TempoMap);
        engine.SetOutputs(outputs.Outputs);
        engine.Load(prepared.Compile(sequence));
        engine.Play(Tick.Zero);
        engine.Pump();

        Assert.Empty(outputs.Problems);
        Assert.Equal(["B00000", "B02003", "C021", "B00000", "B02003", "C021", "90287F", "802840"], port.Sent.Select(m => Convert.ToHexString(m.Bytes)));
    }

    [Fact]
    public async Task OpenAsync_ReportsEndpointsThatVanishedSinceResolution()
    {
        using var provider = new LoopbackMidiProvider(new VirtualClock());
        var port = provider.CreatePort("Gone soon", "g");
        using var directory = new EndpointDirectory([provider]);
        var prepared = new PreparedRouting([port.OutputId], [], [], [], [], new SignalGraphResult());
        provider.RemovePort("g");

        using var outputs = await PlaybackRouting.OpenAsync(prepared, directory, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(outputs.Outputs));
        Assert.Single(outputs.Problems);
    }
}
