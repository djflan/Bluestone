using System.Collections.Immutable;
using Bluestone.Application.Routing;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>
/// What a track header says about a track: its chain's instrument, its destination, or a neutral "No instrument" for an empty
/// track, with warnings kept for real problems (notes nothing plays, a failed instrument, a missing hardware output).
/// </summary>
public sealed class TrackStatusTests : IAsyncLifetime
{
    private static readonly DeviceDefinitionLookup Definitions = DeviceCatalog.BuiltIn.With(TestDevices.Synth).With(TestDevices.MidiFx).Lookup;

    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _endpoints;
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;

    public TrackStatusTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _provider.CreatePort("Synth", "synth");
        _endpoints = new EndpointDirectory([_provider]);
        var playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _endpoints, new NoUi(), new ImmediateDispatcher());
    }

    public async ValueTask InitializeAsync() => await _vm.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _endpoints.Dispose();
        _provider.Dispose();
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    private static ResolvedTrackOutput Unrouted(TrackId track, params string[] problems) => new(track, TrackOutput.None, null, null, null, [.. problems]);

    [Fact]
    public async Task NewTrack_IsAnEmptyHybridTrackHeardAtTheMaster_WithNoWarning()
    {
        _vm.AddTrackCommand.Execute(null);
        await Settle();

        var track = Assert.Single(_vm.Tracks);
        Assert.Equal(TrackRole.Hybrid, track.Role);
        Assert.True(track.IsIdle);
        Assert.Equal("Empty", track.HealthText);
        Assert.Equal("No instrument", track.StatusLine);
        Assert.Equal("No instrument → Master", track.RouteText);
        Assert.Empty(track.Problems);
        Assert.Empty(_vm.Selection.Problems);
        Assert.Equal("Master", track.Sound.AudioDestination);
        Assert.Same(track, _vm.SelectedTrack);

        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Empty(_vm.Tracks);
        Assert.Empty(_session.Project.Connections);
    }

    [Fact]
    public void ATrackPlayedByItsChain_ReadsAsItsInstrument()
    {
        var track = Track.Create("Lead", TrackRole.Hybrid);
        var row = new TrackViewModel(_vm, track.Id);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { Instrument = "Surge XT", AudioDestination = "Master", DeviceCount = 2 });

        Assert.True(row.IsReady);
        Assert.Equal("Surge XT", row.StatusLine);
        Assert.Equal("Surge XT → Master", row.RouteText);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { Instrument = "Surge XT", IsInstrumentUnavailable = true });
        Assert.True(row.NeedsAttention);
        Assert.Equal("Surge XT · Unavailable", row.StatusLine);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { Instrument = "Surge XT", IsInstrumentBypassed = true });
        Assert.Equal("Surge XT · Bypassed", row.StatusLine);
    }

    [Fact]
    public void ABypassedInstrument_IsAChoice_NotAProblem_AndLayeredInstrumentsAreCounted()
    {
        var track = Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max)]);
        var row = new TrackViewModel(_vm, track.Id);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { Instrument = "Surge XT", HoldsNotes = true, IsInstrumentBypassed = true, AudioDestination = "Master" });

        Assert.True(row.IsIdle);
        Assert.Empty(row.Problems);
        Assert.Equal("Surge XT · Bypassed", row.StatusLine);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { Instrument = "Surge XT", InstrumentDevices = [DeviceId.New(), DeviceId.New()], HoldsNotes = true });
        Assert.True(row.IsReady);
        Assert.Equal("Surge XT +1", row.StatusLine);
    }

    [Fact]
    public void NotesThatNothingPlays_AreAProblem_ButOnlyThen()
    {
        var track = Track.FromEvents(TrackId.New(), "Lead", [new NoteEvent(Tick.Zero, new TickSpan(240), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max)]);
        var row = new TrackViewModel(_vm, track.Id);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { HoldsNotes = true, AudioDestination = "Master" });
        Assert.True(row.NeedsAttention);
        Assert.Contains(TrackViewModel.NoInstrumentProblem, row.Problems);
        Assert.Equal("No instrument", row.StatusLine);

        row.Sync(1, track, Unrouted(track.Id), [], new TrackSound { HoldsNotes = true, NoteDestination = "Strings" });
        Assert.True(row.IsReady);
        Assert.Empty(row.Problems);
        Assert.Equal("→ Strings", row.StatusLine);

        // An imported file's track with no destination at all keeps the resolver's "No output", once.
        row.Sync(1, track, Unrouted(track.Id, RouteResolver.NoOutputProblem), [], new TrackSound { HoldsNotes = true });
        Assert.True(row.IsOffline);
        Assert.Equal("No output", row.StatusLine);
        Assert.Equal([RouteResolver.NoOutputProblem], row.Problems);
    }

    [Fact]
    public void TheSoundOfATrack_IsReadFromItsChainAndConnections()
    {
        var track = Track.Create("Pad", TrackRole.Hybrid);
        var rack = DeviceChain.Create(ChainOwner.Rack, "Strings");
        var channel = MixerChannel.Create("Pads");
        var fx = TestDevices.Instance(TestDevices.MidiFx);
        var synth = TestDevices.Instance(TestDevices.Synth);
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track),
            Mixer = Project.CreateNew().Mixer.With(channel),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Mixer(channel.Id)),
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.Rack(rack.Id)),
            ],
        };
        project = project.WithChain(rack).WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = ImmutableArray.Create(fx, synth) });

        var sound = TrackSound.Of(project, track.Id, Definitions);

        Assert.Equal("Synth", sound.Instrument);
        Assert.Equal(synth.Id, sound.InstrumentDevice);
        Assert.Equal("2 devices", sound.DevicesText);
        Assert.Equal("Pads", sound.AudioDestination);
        Assert.Equal("Strings", sound.NoteDestination);
        Assert.False(sound.HoldsNotes);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
