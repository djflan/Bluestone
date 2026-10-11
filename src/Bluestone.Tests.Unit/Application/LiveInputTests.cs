using Bluestone.Application.Editing;
using Bluestone.Application.Live;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

/// <summary>
/// Live input through the devices on a track's live route to an external instrument (ADR 0035), on a virtual clock.
/// New projects are 960 PPQN at 120 BPM: a beat is 500 ms, an arpeggiator sixteenth 125 ms.
/// </summary>
public sealed class LiveInputTests : IAsyncDisposable
{
    private static readonly TimeSpan Origin = TimeSpan.FromSeconds(10);

    private readonly VirtualClock _clock = new(Origin);
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _directory;
    private readonly ProjectSession _session = new();
    private readonly PlaybackController _controller;
    private readonly LoopbackPort _keys;
    private readonly LoopbackPort _synth;

    public LiveInputTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _keys = _provider.CreatePort("Keys", "keys");
        _synth = _provider.CreatePort("Synth", "synth");
        _directory = new EndpointDirectory([_provider]);
        _controller = new PlaybackController(_session, _directory, ProfileCatalog.Empty, _clock, startThread: false);
        _controller.SelectInputs([_keys.InputId]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceDefinitionLookup Definitions => DeviceCatalog.BuiltIn.Lookup;

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
        _directory.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task Thru_ThroughAnArpeggiator_StepsAtTheTempo_WhileTheKeyIsHeld()
    {
        var track = await TrackWith(BuiltInDevices.CreateArpeggiator(rate: 2));
        _controller.SetThruTrack(track);

        var ons = new List<double>();
        Play(60);
        Record(ons, 0);
        for (var ms = 1; ms <= 500; ms++)
        {
            if (ms == 500)
            {
                Release(60);
            }

            Advance(1);
            Record(ons, ms);
        }

        Advance(200);

        // Sixteenths at 120 BPM: 0, 125, 250, 375 ms; the release at 500 ms ends the phrase.
        Assert.Equal([0.0, 125, 250, 375], ons);
        Assert.Equal(4, Count(0x80, 60));
    }

    [Fact]
    public async Task TheArpeggiator_FollowsTheTempoAtThePlayhead()
    {
        _session.Execute(ProjectCommands.SetTempo(Tick.Zero, Tempo.FromBeatsPerMinute(60)));
        var track = await TrackWith(BuiltInDevices.CreateArpeggiator(rate: 2));
        _controller.SetThruTrack(track);

        var ons = new List<double>();
        Play(60);
        Record(ons, 0);
        for (var ms = 1; ms <= 600; ms++)
        {
            Advance(1);
            Record(ons, ms);
        }

        Assert.Equal([0.0, 250, 500], ons);
    }

    [Fact]
    public async Task Thru_ThroughAnEventFilterAndTranspose_PlaysWhatTheDevicesMakeOfIt()
    {
        var track = await TrackWith(BuiltInDevices.CreateEventFilter(EventClass.Controllers), BuiltInDevices.CreateTranspose(-12));
        _controller.SetThruTrack(track);

        Play(60);
        _keys.Inject([0xB0, 1, 64]);
        Release(60);
        _controller.Engine.Pump();

        Assert.Equal(["90307F", "803040"], _synth.Sent.Select(m => Convert.ToHexString(m.Bytes)));
    }

    [Fact]
    public async Task BypassingADevice_WhileAKeyIsHeld_ReleasesItsNote_AndTheLaterKeyUpSendsNothingStray()
    {
        var transpose = BuiltInDevices.CreateTranspose(12);
        var track = await TrackWith(transpose);
        _controller.SetThruTrack(track);
        Play(60);
        _controller.Engine.Pump();

        _session.Execute(DeviceCommands.SetBypassed(transpose.Id, true, Definitions));
        await _controller.RefreshAsync(Ct);
        _controller.Engine.Pump();
        Release(60);
        _controller.Engine.Pump();

        Assert.Equal(["90487F", "804840"], _synth.Sent.Select(m => Convert.ToHexString(m.Bytes)));
    }

    [Fact]
    public async Task RemovingAnArpeggiator_WhileItPlays_ReleasesTheNoteItIsSounding()
    {
        var arpeggiator = BuiltInDevices.CreateArpeggiator(rate: 0, gate: 1);
        var track = await TrackWith(arpeggiator);
        _controller.SetThruTrack(track);
        Play(64);
        Advance(10);
        Assert.Equal(1, Count(0x90, 64));

        _session.Execute(DeviceCommands.RemoveDevice(arpeggiator.Id, Definitions));
        await _controller.RefreshAsync(Ct);
        _controller.Engine.Pump();

        Assert.Equal(1, Count(0x80, 64));
        Release(64);
        Advance(1000);
        Assert.Equal(1, Count(0x80, 64));
    }

    [Fact]
    public async Task ChangingTheThruTrack_KeepsAReleaseGoingWhereItsNoteWent()
    {
        var other = _provider.CreatePort("Other", "other");
        var first = await TrackWith(BuiltInDevices.CreateTranspose(5));
        var second = AddTrack(other);
        await _controller.RefreshAsync(Ct);
        _controller.SetThruTrack(first);
        Play(60);
        _controller.SetThruTrack(second);
        Release(60);
        _controller.Engine.Pump();

        Assert.Equal(["90417F", "804140"], _synth.Sent.Select(m => Convert.ToHexString(m.Bytes)));
        Assert.Empty(other.Sent);
    }

    [Fact]
    public async Task Panic_ReleasesLiveNotes()
    {
        var track = await TrackWith(BuiltInDevices.CreateTranspose(2));
        _controller.SetThruTrack(track);
        Play(60);
        _controller.Engine.Pump();

        _controller.Panic();
        _controller.Engine.Pump();

        Assert.Contains(_synth.Sent, m => m.Bytes is [0x80, 62, _]);
    }

    [Fact]
    public async Task Audition_PlaysThroughTheArpeggiator()
    {
        var track = await TrackWith(BuiltInDevices.CreateArpeggiator(rate: 2, octaves: 2));
        _controller.Audition(track, new NoteNumber(60), new Velocity(90));
        for (var ms = 0; ms < 300; ms++)
        {
            Advance(1);
        }

        _controller.EndAudition();
        Advance(100);

        Assert.Equal([60, 72, 60], _synth.Sent.Where(m => m.Bytes[0] == 0x90).Select(m => (int)m.Bytes[1]));
        Assert.Equal(Count(0x90, 60) + Count(0x90, 72), Count(0x80, 60) + Count(0x80, 72));
    }

    [Fact]
    public async Task Recording_TakesTheInputBeforeTheDevices()
    {
        var track = await TrackWith(BuiltInDevices.CreateArpeggiator(rate: 2), BuiltInDevices.CreateTranspose(12));
        _controller.SetThruTrack(track);
        await _controller.RecordAsync(track, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        Advance(500);
        Play(60);
        Advance(500);
        Release(60);
        Advance(100);
        _controller.Stop();

        // The arpeggiator played four transposed sixteenths live; the take holds the one key that was played.
        Assert.Equal(4, Count(0x90, 72));
        var note = Assert.IsType<NoteEvent>(Assert.Single(_session.Project.Sequence.FindTrack(track)!.ArrangedEvents));
        Assert.Equal((960L, 960L, 60), (note.Position.Value, note.Duration.Value, (int)note.Note.Value));
    }

    [Fact]
    public async Task TheLiveRoute_ReachesTheNearestInstrument_WithTheDevicesOnTheWay()
    {
        var track = await TrackWith(BuiltInDevices.CreateArpeggiator());

        var plan = _controller.Live.PlanOf(track)!;

        Assert.Equal(LiveTargetKind.External, plan.Target);
        Assert.Equal("Synth", plan.Endpoint!.DisplayName);
        Assert.Equal(BuiltInDevices.Arpeggiator.Id, Assert.Single(plan.Stages).Device!.Definition.Id);
    }

    private async Task<TrackId> TrackWith(params DeviceInstance[] devices)
    {
        var track = AddTrack(_synth);
        foreach (var device in devices)
        {
            _session.Execute(DeviceCommands.InsertDevice(track, device, Definitions));
        }

        await _controller.RefreshAsync(Ct);
        return track;
    }

    private TrackId AddTrack(LoopbackPort output)
    {
        var track = Track.FromEvents(TrackId.New(), output.Name, []);
        _session.Execute(ProjectCommands.AddTrack(track));
        _session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, output.OutputId.Value, output.Name) }));
        return track.Id;
    }

    // Moves the clock, runs the live routes, and sends what they put out.
    private void Advance(double milliseconds)
    {
        _clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        _controller.Live.Pump();
        _controller.Engine.Pump();
    }

    private void Record(List<double> ons, double at)
    {
        _controller.Engine.Pump();
        while (ons.Count < _synth.Sent.Count(m => m.Bytes[0] == 0x90))
        {
            ons.Add(at);
        }
    }

    private int Count(byte status, int note) => _synth.Sent.Count(m => m.Bytes[0] == status && m.Bytes[1] == note);

    private void Play(int note, int velocity = 127) => _keys.Inject([0x90, (byte)note, (byte)velocity]);

    private void Release(int note) => _keys.Inject([0x80, (byte)note, 0x40]);
}
