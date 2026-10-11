using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Application;

/// <summary>
/// Recording through loopback ports on a virtual clock. New projects are 960 PPQN at 120 BPM, so
/// 500 ms is one beat (960 ticks) and a 4/4 bar is two seconds.
/// </summary>
public sealed class RecordingTests : IAsyncDisposable
{
    private static readonly TimeSpan Origin = TimeSpan.FromSeconds(10);

    private readonly VirtualClock _clock = new(Origin);
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _directory;
    private readonly ProjectSession _session = new();
    private readonly PlaybackController _controller;
    private readonly LoopbackPort _keys;
    private readonly LoopbackPort _synth;

    public RecordingTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _keys = _provider.CreatePort("Keys", "keys");
        _synth = _provider.CreatePort("Synth", "synth");
        _directory = new EndpointDirectory([_provider]);
        _controller = new PlaybackController(_session, _directory, ProfileCatalog.Empty, _clock, startThread: false);
        _controller.SelectInputs([_keys.InputId]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
        _directory.Dispose();
        _provider.Dispose();
    }

    private Track AddTrack(LoopbackPort output, params TrackEvent[] events)
    {
        var track = Track.FromEvents(TrackId.New(), "Keys", events);
        _session.Execute(ProjectCommands.AddTrack(track));
        _session.Execute(ProjectCommands.SetTrackOutput(track.Id, new TrackOutput { Endpoint = new EndpointReference(LoopbackMidiProvider.ProviderId, output.OutputId.Value, output.Name) }));
        return track;
    }

    /// <summary>Pumps the engine every 10 ms up to <paramref name="milliseconds"/> after the origin.</summary>
    private void RunTo(double milliseconds)
    {
        var target = Origin + TimeSpan.FromMilliseconds(milliseconds);
        while (_clock.Now < target)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(Math.Min(10, (target - _clock.Now).TotalMilliseconds)));
            _controller.Engine.Pump();
        }
    }

    private void Play(int note, int velocity = 100) => _keys.Inject([0x90, (byte)note, (byte)velocity]);

    private void Release(int note) => _keys.Inject([0x80, (byte)note, 0x40]);

    private Track Recorded(Track track) => _session.Project.Sequence.FindTrack(track.Id)!;

    [Fact]
    public async Task RecordFromStop_CountsInThenCapturesNotesWherePlayed()
    {
        var track = AddTrack(_synth);
        await _controller.RefreshAsync(Ct);

        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 1), Ct);
        _controller.Engine.Pump();
        Assert.True(_controller.Engine.IsCountingIn);

        RunTo(2500);
        Play(60);
        RunTo(2750);
        Release(60);
        RunTo(3000);
        var take = _controller.Stop();

        var note = Assert.IsType<NoteEvent>(Assert.Single(Recorded(track).ArrangedEvents));
        Assert.Equal((960L, 480L), (note.Position.Value, note.Duration.Value));
        Assert.Equal(100, note.Velocity.Value);
        Assert.Equal(new TickRange(Tick.Zero, new Tick(1920)), take!.Range);
        Assert.Equal("Record", _session.History.UndoLabel);
    }

    [Fact]
    public async Task PianoKeys_WhileRecordingWithoutThru_SoundOnTheTrackAndAreRecorded()
    {
        var track = AddTrack(_synth);
        await _controller.RefreshAsync(Ct);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(500);

        // Thru is off here, so the key sounds on the track's output directly, and is still recorded.
        _controller.PlayKey(track.Id, new NoteNumber(64), new Velocity(80));
        RunTo(750);
        _controller.EndAudition();
        RunTo(800);
        _controller.Stop();

        Assert.Contains(_synth.Sent, m => m.Bytes is [0x90, 64, 80]);
        Assert.Contains(_synth.Sent, m => m.Bytes is [0x80, 64, _]);
        var note = Assert.IsType<NoteEvent>(Assert.Single(Recorded(track).ArrangedEvents));
        Assert.Equal((960L, 480L, 64), (note.Position.Value, note.Duration.Value, (int)note.Note.Value));
    }

    [Fact]
    public async Task WhileRecording_OnlyKeysOnTheRecordedTrackAreRecorded()
    {
        var other = _provider.CreatePort("Other", "other");
        var track = AddTrack(_synth);
        var elsewhere = AddTrack(other);
        await _controller.RefreshAsync(Ct);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(500);
        var received = _controller.Recorder.MessagesReceived;

        // Hearing a note being edited, and a key of another track's piano roll, are not playing into the take.
        _controller.Audition(track.Id, new NoteNumber(60), new Velocity(80));
        RunTo(550);
        _controller.PlayKey(elsewhere.Id, new NoteNumber(62), new Velocity(80));
        RunTo(600);
        _controller.EndAudition();
        RunTo(650);
        _controller.Stop();

        Assert.Empty(Recorded(track).ArrangedEvents);
        Assert.Contains(other.Sent, m => m.Bytes is [0x90, 62, 80]);
        Assert.Equal(received, _controller.Recorder.MessagesReceived);
    }

    [Fact]
    public async Task OnScreenAndKeyboardNotes_OfTheSamePitch_KeepTheirOwnReleases()
    {
        var track = AddTrack(_synth);
        await _controller.RefreshAsync(Ct);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(500);

        Play(60);
        RunTo(750);
        _controller.PlayKey(track.Id, new NoteNumber(60), new Velocity(90));
        RunTo(1000);
        _controller.EndAudition();
        RunTo(1500);
        Release(60);
        RunTo(1600);
        _controller.Stop();

        var notes = Recorded(track).ArrangedEvents.Cast<NoteEvent>().Select(n => (n.Position.Value, n.Duration.Value, (int)n.Velocity.Value)).ToList();
        Assert.Equal([(960L, 1920L, 100), (1440L, 480L, 90)], notes);
    }

    [Fact]
    public async Task Recording_ClicksTheMetronomeOnTheTracksOutput()
    {
        var track = AddTrack(_synth);
        await _controller.RefreshAsync(Ct);

        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 1), Ct);
        RunTo(2600);

        // Four count-in clicks, then the downbeat and the second beat of bar 1 (channel 10).
        Assert.Equal(6, _synth.Sent.Count(m => m.Bytes[0] == 0x99));
    }

    [Fact]
    public async Task Thru_EchoesToTheTrackWithItsChannelAndTranspose()
    {
        var track = AddTrack(_synth);
        _session.Execute(ProjectCommands.SetTrackOutput(track.Id, TrackOutputs.Read(_session.Project, track.Id) with { Channel = MidiChannel.FromNumber(3), Transpose = 12 }));
        _controller.SetThruTrack(track.Id);
        await _controller.RefreshAsync(Ct);

        Play(60, 90);
        _controller.Engine.Pump();
        _controller.SetThruTrack(null);
        Release(60);
        _controller.Engine.Pump();

        // The release follows its note even though thru was turned off in between.
        Assert.Equal(["92485A", "824840"], _synth.Sent.Select(m => Convert.ToHexString(m.Bytes)));
        Assert.Equal(2, _controller.Recorder.MessagesReceived);
    }

    [Fact]
    public async Task Thru_NeverEchoesBackIntoTheBusItCameFrom()
    {
        var track = AddTrack(_keys);
        _controller.SetThruTrack(track.Id);
        await _controller.RefreshAsync(Ct);

        Play(60);
        _controller.Engine.Pump();

        Assert.Empty(_keys.Sent);
    }

    [Fact]
    public async Task HeldNotes_FollowsKeysDownOnInputs()
    {
        AddTrack(_synth);
        await _controller.RefreshAsync(Ct);

        Play(60);
        Play(64);
        Play(67);
        Release(60);
        _keys.Inject([0x90, 64, 0]);

        Assert.Equal(UInt128.One << 67, _controller.Recorder.HeldNotes);

        // All Notes Off clears what is held on its channel.
        Play(72);
        _keys.Inject([0xB0, 123, 0]);

        Assert.Equal(UInt128.Zero, _controller.Recorder.HeldNotes);
    }

    [Fact]
    public async Task PunchIn_ReplaceRemovesOnlyWhatWasRecordedOver()
    {
        var before = new NoteEvent(new Tick(100), new TickSpan(10), MidiChannel.FromIndex(0), new NoteNumber(40), Velocity.Max);
        var over = new NoteEvent(new Tick(1500), new TickSpan(10), MidiChannel.FromIndex(0), new NoteNumber(41), Velocity.Max);
        var track = AddTrack(_synth, before, over);
        await _controller.PlayAsync(Tick.Zero, Ct);
        RunTo(500);

        await _controller.RecordAsync(track.Id, new RecordOptions(Replace: true), Ct);
        Play(64);
        RunTo(600);
        Release(64);
        RunTo(1000);
        _controller.FinishRecording();

        Assert.Equal(TransportState.Playing, _controller.Engine.State);
        Assert.Equal([40, 64], Recorded(track).ArrangedEvents.Cast<NoteEvent>().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public async Task HeldNotes_EndWhereRecordingStopped()
    {
        var track = AddTrack(_synth);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(250);
        Play(60);
        RunTo(1000);
        _controller.Stop();

        var note = Assert.IsType<NoteEvent>(Assert.Single(Recorded(track).ArrangedEvents));
        Assert.Equal((480L, 1920L), (note.Position.Value, note.EndPosition.Value));
    }

    [Fact]
    public async Task CycleRecording_MergesPassesAndEndsWrappedNotesAtTheLoopEnd()
    {
        var track = AddTrack(_synth);
        _session.Execute(ProjectCommands.SetLoop(new TickRange(Tick.Zero, new Tick(3840))));
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();

        RunTo(1900);
        Play(60);
        RunTo(2100);
        Release(60);
        RunTo(2500);
        Play(62);
        RunTo(2750);
        Release(62);
        RunTo(3000);
        _controller.Stop();

        var notes = Recorded(track).ArrangedEvents.Cast<NoteEvent>().ToList();
        Assert.Equal([(960L, 1440L, 62), (3648L, 3840L, 60)], notes.Select(n => (n.Position.Value, n.EndPosition.Value, (int)n.Note.Value)));
    }

    [Fact]
    public async Task CycleRecording_PairsReleasesWithTheNoteTheyBelongTo()
    {
        var track = AddTrack(_synth);
        _session.Execute(ProjectCommands.SetLoop(new TickRange(Tick.Zero, new Tick(3840))));
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();

        // Pass 1 holds C4 across the wrap; pass 2 plays C4 again early in the loop.
        RunTo(1900);
        Play(60);
        RunTo(2100);
        Release(60);
        RunTo(2200);
        Play(60);
        RunTo(2400);
        Release(60);
        RunTo(3000);
        _controller.Stop();

        var notes = Recorded(track).ArrangedEvents.Cast<NoteEvent>().Select(n => (n.Position.Value, n.EndPosition.Value)).ToList();
        Assert.Equal([(384L, 768L), (3648L, 3840L)], notes);
    }

    [Fact]
    public async Task Replace_WithALoopThatNeverEngaged_ReplacesOnlyWhatWasPlayedOver()
    {
        var early = new NoteEvent(new Tick(200), new TickSpan(10), MidiChannel.FromIndex(0), new NoteNumber(40), Velocity.Max);
        var later = new NoteEvent(new Tick(31000), new TickSpan(10), MidiChannel.FromIndex(0), new NoteNumber(41), Velocity.Max);
        var track = AddTrack(_synth, early, later);
        _session.Execute(ProjectCommands.SetLoop(new TickRange(new Tick(30720), new Tick(46080))));
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0, Replace: true), Ct);
        _controller.Engine.Pump();
        RunTo(500);
        Play(64);
        RunTo(600);
        Release(64);
        RunTo(1000);
        _controller.Stop();

        Assert.Equal([64, 41], Recorded(track).ArrangedEvents.Cast<NoteEvent>().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public async Task NotesPlayedDuringACountInFromTheStart_AreNotPiledAtZero()
    {
        var track = AddTrack(_synth);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 1), Ct);
        _controller.Engine.Pump();
        RunTo(1500);
        Play(60);
        RunTo(1600);
        Release(60);
        RunTo(2500);
        _controller.Stop();

        Assert.Empty(Recorded(track).ArrangedEvents);
    }

    [Fact]
    public void DefaultInputs_LeaveOutBuiltInBusesAndBusesBluestoneIsPlayingTo()
    {
        static EndpointDescriptor Port(string provider, string name, EndpointDirection direction, EndpointTransport transport) =>
            new(new EndpointId(provider, $"{name}/{direction}"), name, direction, transport, EndpointCapabilities.None);
        var keyboard = Port("coremidi", "Keyboard", EndpointDirection.Input, EndpointTransport.Physical);
        var iacIn = Port("coremidi", "IAC Bus 1", EndpointDirection.Input, EndpointTransport.Virtual);
        var monitor = Port("loopback", "Bluestone Monitor", EndpointDirection.Input, EndpointTransport.Test);
        var iacOut = Port("coremidi", "IAC Bus 1", EndpointDirection.Output, EndpointTransport.Virtual);

        Assert.Equal([keyboard.Id], PlaybackController.DefaultInputs([keyboard, iacIn, monitor], [iacOut]));
        Assert.Equal([keyboard.Id, iacIn.Id], PlaybackController.DefaultInputs([keyboard, iacIn, monitor], []));
    }

    [Fact]
    public async Task ControllersAndPitchBend_AreRecordedAsEvents()
    {
        var track = AddTrack(_synth);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(500);
        _keys.Inject([0xB0, 0x01, 0x40]);
        _keys.Inject([0xE0, 0x00, 0x50]);
        _keys.Inject([0xF8]);
        RunTo(600);
        _controller.Stop();

        var events = Recorded(track).ArrangedEvents;
        Assert.Equal([typeof(ControllerEvent), typeof(PitchBendEvent)], events.Select(e => e.GetType()));
        Assert.Equal(0x50 << 7, Assert.IsType<PitchBendEvent>(events[1]).Value.ToFourteenBit());
        Assert.All(events, e => Assert.Equal(960, e.Position.Value));
    }

    [Fact]
    public async Task Preview_ShowsHeldNotesWhileRecording()
    {
        var track = AddTrack(_synth);
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(500);
        Play(60);
        RunTo(750);

        var preview = Assert.Single(_controller.Recorder.Preview(_controller.Engine.Position, null));

        Assert.True(preview.IsHeld);
        Assert.Equal((960L, 1440L), (preview.Start, preview.End));
    }

    [Fact]
    public async Task EmptyTake_DoesNotCreateAnUndoStep()
    {
        var track = AddTrack(_synth);
        var label = _session.History.UndoLabel;
        await _controller.RecordAsync(track.Id, new RecordOptions(CountInBars: 0), Ct);
        _controller.Engine.Pump();
        RunTo(500);
        _controller.Stop();

        Assert.Equal(label, _session.History.UndoLabel);
        Assert.Empty(Recorded(track).ArrangedEvents);
    }
}
