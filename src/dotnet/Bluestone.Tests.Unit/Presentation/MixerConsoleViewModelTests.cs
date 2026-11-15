using System.Diagnostics;
using Bluestone.Application.Editing;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>The track-owned mixer console, alongside the audio clip section it shares the inspector with.</summary>
public sealed class MixerConsoleViewModelTests : IAsyncLifetime
{
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly EndpointDirectory _endpoints = new([]);
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;

    public MixerConsoleViewModelTests()
    {
        var playback = new PlaybackController(_session, _endpoints, ProfileCatalog.Empty, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _endpoints, new FakeUi(), new ImmediateDispatcher());
    }

    public async ValueTask InitializeAsync() => await _vm.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _endpoints.Dispose();
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    private async Task SeedAsync(params Track[] tracks)
    {
        var outputs = tracks
            .Where(track => track.AudioStrip is not null)
            .Select(track => SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master))
            .ToArray();
        _session.Execute(new ProjectCommand("Set up mixer tracks", project => project with
        {
            Sequence = new Sequence(project.Sequence.TempoMap, project.Sequence.MeterMap, tracks, project.Sequence.Markers),
            Connections = [.. outputs],
        }));
        await Settle();
    }

    [Fact]
    public async Task RowsFollowTrackIdsThroughRenameReorderAndRemoval()
    {
        var audio = Track.Create("Audio", TrackRole.Audio);
        var midi = Track.Create("MIDI", TrackRole.Instrument);
        await SeedAsync(audio, midi);

        var audioRow = Assert.Single(_vm.MixerConsole.Tracks, row => row.Id == audio.Id);
        var midiRow = Assert.Single(_vm.MixerConsole.Tracks, row => row.Id == midi.Id);
        Assert.Equal([audio.Id, midi.Id], _vm.MixerConsole.Tracks.Select(row => row.Id));
        Assert.True(audioRow.HasAudioStrip);
        Assert.False(midiRow.HasAudioStrip);
        Assert.False(midiRow.CanChooseOutput);
        Assert.False(midiRow.CanAddSend);
        Assert.Empty(midiRow.Outputs);

        _session.Execute(new ProjectCommand("Rename and reorder", project => project with
        {
            Sequence = new Sequence(
                project.Sequence.TempoMap,
                project.Sequence.MeterMap,
                [project.Sequence.FindTrack(midi.Id)!, project.Sequence.FindTrack(audio.Id)!.WithName("Renamed audio")],
                project.Sequence.Markers),
        }));
        await Settle();

        Assert.Equal([midi.Id, audio.Id], _vm.MixerConsole.Tracks.Select(row => row.Id));
        Assert.Same(midiRow, _vm.MixerConsole.Tracks[0]);
        Assert.Same(audioRow, _vm.MixerConsole.Tracks[1]);
        Assert.Equal("Renamed audio", audioRow.Name);

        _session.Execute(new ProjectCommand("Remove MIDI track", project => project with
        {
            Sequence = project.Sequence.WithoutTrack(midi.Id),
        }));
        await Settle();

        Assert.DoesNotContain(_vm.MixerConsole.Tracks, row => row.Id == midi.Id);
        Assert.Same(audioRow, Assert.Single(_vm.MixerConsole.Tracks));
    }

    [Fact]
    public async Task AudioControlsChangeOnlyTheAudioStrip_AndGainDragIsOneUndoStep()
    {
        var audio = Track.Create("Audio", TrackRole.Audio).WithMuted(true).WithSoloed(true);
        await SeedAsync(audio);
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        var historyBeforeGain = _session.History.UndoCount;

        row.Gain = -6;
        row.Gain = -9;
        row.Gain = -12;
        await Settle();

        Assert.Equal(-12, _session.Project.Sequence.FindTrack(audio.Id)!.AudioStrip!.GainDecibels);
        Assert.Equal(historyBeforeGain + 1, _session.History.UndoCount);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(AudioStrip.Default, _session.Project.Sequence.FindTrack(audio.Id)!.AudioStrip);
        Assert.Equal(0, row.Gain);

        row.Pan = 0.35;
        row.IsMuted = false;
        row.IsSoloed = false;
        await Settle();

        var changed = _session.Project.Sequence.FindTrack(audio.Id)!;
        Assert.Equal(0.35, changed.AudioStrip!.Pan, 6);
        Assert.False(changed.AudioStrip.IsMuted);
        Assert.False(changed.AudioStrip.IsSoloed);
        Assert.True(changed.IsMuted);
        Assert.True(changed.IsSoloed);
    }

    [Fact]
    public async Task MasterIsAlwaysPresentAndItsControlsEditTheProjectMasterStrip()
    {
        Assert.Empty(_vm.MixerConsole.Tracks);
        Assert.NotNull(_vm.MixerConsole.Master);

        _vm.MixerConsole.Master.Gain = -3;
        _vm.MixerConsole.Master.Pan = -0.25;
        _vm.MixerConsole.Master.IsMuted = true;
        _vm.MixerConsole.Master.IsSoloed = true;
        await Settle();

        Assert.Equal((-3, -0.25, true, true), (
            _session.Project.MasterStrip.GainDecibels,
            _session.Project.MasterStrip.Pan,
            _session.Project.MasterStrip.IsMuted,
            _session.Project.MasterStrip.IsSoloed));
    }

    [Fact]
    public void SelectingMaster_UsesItsInspectorScopeWithoutSelectingAnArrangementTrack()
    {
        Assert.Empty(_vm.SelectedTracks);

        _vm.MixerConsole.Master.SelectCommand.Execute(null);

        Assert.Empty(_vm.SelectedTracks);
        Assert.True(_vm.MixerConsole.Master.IsSelected);
        Assert.True(_vm.Selection.IsMasterSelected);
        Assert.True(_vm.Selection.ShowsMasterInspector);
        Assert.False(_vm.Selection.ShowsTrackSelectionInspector);
        Assert.False(_vm.Selection.ShowsEmptyInspector);
    }

    [Fact]
    public async Task SelectingATrackAfterMasterScope_ClearsAndThenRestoresTrackSelection()
    {
        var first = Track.Create("First", TrackRole.Audio);
        var second = Track.Create("Second", TrackRole.Audio);
        await SeedAsync(first, second);

        var initiallySelected = Assert.Single(_vm.SelectedTracks);
        _vm.MixerConsole.Master.SelectCommand.Execute(null);
        await Settle();

        Assert.Empty(_vm.SelectedTracks);
        Assert.True(_vm.MixerConsole.Master.IsSelected);
        Assert.True(_vm.Selection.IsMasterSelected);
        Assert.True(_vm.Selection.ShowsMasterInspector);

        _vm.SelectedTracks.Add(_vm.Tracks.Single(track => track.Id == second.Id));
        await Settle();

        Assert.Equal(second.Id, Assert.Single(_vm.SelectedTracks).Id);
        Assert.False(_vm.MixerConsole.Master.IsSelected);
        Assert.False(_vm.Selection.IsMasterSelected);
        Assert.True(_vm.Selection.ShowsTrackSelectionInspector);
        Assert.True(_vm.MixerConsole.Tracks.Single(row => row.Id == second.Id).IsSelected);
    }

    [Fact]
    public async Task MeterHeldPeakPositions_ArePublishedForTrackAndMaster()
    {
        var track = Track.Create("Audio", TrackRole.Audio);
        await SeedAsync(track);
        var reading = new MeterReading(0.9f, 0.8f, 0.4f, 0.3f);
        _vm.MixerConsole.MeterSource = _ => reading;
        var timestamp = Stopwatch.GetTimestamp();

        _vm.MixerConsole.RefreshMeters(timestamp);
        reading = new MeterReading(0.35f, 0.25f, 0.2f, 0.15f);
        _vm.MixerConsole.RefreshMeters(timestamp + 1);

        var row = Assert.Single(_vm.MixerConsole.Tracks);
        var expectedLeftHold = MeterReading.ToDecibels(0.9f);
        var expectedRightHold = MeterReading.ToDecibels(0.8f);
        foreach (var strip in new MixerMeterStripViewModel[] { row, _vm.MixerConsole.Master })
        {
            Assert.Equal(expectedLeftHold, strip.MeterLeftPeakHold, 5);
            Assert.Equal(expectedRightHold, strip.MeterRightPeakHold, 5);
            Assert.Equal(MeterReading.ToDecibels(0.35f), strip.MeterLeftValue, 5);
            Assert.Equal(MeterReading.ToDecibels(0.25f), strip.MeterRightValue, 5);
        }
    }

    [Fact]
    public async Task GroupOutputAndEffectSendBelongToTheAudioTrack_AndSendEditsUndo()
    {
        var group = Track.Create("Group", TrackRole.Group);
        var audio = Track.Create("Vocal", TrackRole.Audio).WithGroup(group.Id);
        var effect = Track.Create("Reverb", TrackRole.Effect);
        _session.Execute(new ProjectCommand("Set up routed tracks", project => project with
        {
            Sequence = new Sequence(project.Sequence.TempoMap, project.Sequence.MeterMap, [group, audio, effect], project.Sequence.Markers),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(group.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(audio.Id), SignalNode.Track(group.Id)),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(effect.Id), SignalNode.Master),
            ],
        }));
        await Settle();

        var source = _vm.MixerConsole.Tracks.Single(row => row.Id == audio.Id);
        Assert.Equal(group.Id, source.Output!.Destination.AsTrack());
        Assert.Equal("Group", source.OutputSummary);
        var target = Assert.Single(source.SendTargets);
        Assert.Equal(effect.Id, target.Track);

        source.NewSendTarget = target;
        Assert.True(source.CanAddSend);
        source.AddSendCommand.Execute(null);
        await Settle();

        var send = Assert.Single(source.Sends);
        var connection = Assert.Single(_session.Project.Connections, item => item.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal(SignalNode.Track(effect.Id), connection.Destination);
        Assert.Equal(connection.Id, Assert.Single(_session.Project.Connections, item => item.AudioPurpose == AudioRoutePurpose.Send).Id);

        send.Level = -3;
        send.Level = -9;
        send.IsPreFader = true;
        await Settle();

        var edited = Assert.Single(_session.Project.Connections, item => item.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal((-9, true), (edited.SendLevelDecibels, edited.SendPreFader));
        _vm.UndoCommand.Execute(null);
        await Settle();
        var restoredTap = Assert.Single(_session.Project.Connections, item => item.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal((-9, false), (restoredTap.SendLevelDecibels, restoredTap.SendPreFader));
        _vm.UndoCommand.Execute(null);
        await Settle();
        var restoredLevel = Assert.Single(_session.Project.Connections, item => item.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal((0, false), (restoredLevel.SendLevelDecibels, restoredLevel.SendPreFader));

        send.RemoveCommand.Execute(null);
        await Settle();
        Assert.DoesNotContain(_session.Project.Connections, item => item.AudioPurpose == AudioRoutePurpose.Send);
    }

    [Fact]
    public async Task RefreshMetersExposeSeparatePeakAndRmsValuesAndIdleMetersFallToFloor()
    {
        var audio = Track.Create("Audio", TrackRole.Audio);
        await SeedAsync(audio);
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        _vm.MixerConsole.MeterSource = id => id is null
            ? new MeterReading(0.5f, 0.25f, 0.125f, 0.0625f)
            : new MeterReading(0.75f, 0.5f, 0.25f, 0.125f);

        _vm.MixerConsole.RefreshMeters(Stopwatch.GetTimestamp());

        Assert.Equal(-2.50, row.Meter.PeakLeft, 2);
        Assert.Equal(-6.02, row.Meter.PeakRight, 2);
        Assert.Equal(-12.04, row.Meter.RmsLeft, 2);
        Assert.Equal(-18.06, row.Meter.RmsRight, 2);
        Assert.Equal(-6.02, _vm.MixerConsole.Master.Meter.PeakLeft, 2);
        Assert.Equal(-18.06, _vm.MixerConsole.Master.Meter.RmsLeft, 2);

        _vm.MixerConsole.MeterSource = _ => null;
        _vm.MixerConsole.RefreshMeters(Stopwatch.GetTimestamp());
        Assert.False(row.Meter.IsActive);
        Assert.Equal(MeterLevels.FloorDecibels, row.Meter.PeakLeft);
        Assert.False(_vm.MixerConsole.Master.Meter.IsActive);
    }

    [Fact]
    public async Task RefreshMeters_HoldPeaksAndLatchClippingForEachAudioStrip()
    {
        var audio = Track.Create("Audio", TrackRole.Audio);
        await SeedAsync(audio);
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        _vm.MixerConsole.MeterSource = id => id is null
            ? new MeterReading(0.1f, 0.1f, 0.05f, 0.05f)
            : new MeterReading(0.5f, 1.25f, 0.25f, 0.25f);
        var start = Stopwatch.GetTimestamp();

        _vm.MixerConsole.RefreshMeters(start);

        Assert.Equal(-6.02, row.Meter.PeakLeft, 2);
        Assert.Equal(1.94, row.Meter.PeakRight, 2);
        Assert.True(row.Meter.Clipped);

        _vm.MixerConsole.MeterSource = _ => new MeterReading(0.1f, 0.1f, 0.05f, 0.05f);
        _vm.MixerConsole.RefreshMeters(start + Stopwatch.Frequency);
        Assert.Equal(-20, row.Meter.PeakLeft, 6);
        Assert.Equal(-6.02, row.Meter.HoldLeft, 2);
        Assert.True(row.Meter.Clipped);

        _vm.MixerConsole.RefreshMeters(start + (Stopwatch.Frequency * 5 / 2));
        Assert.Equal(-20, row.Meter.HoldLeft, 6);
        row.Meter.ResetClip();
        Assert.False(row.Meter.Clipped);
    }

    [Fact]
    public async Task RefreshMeters_WithAudioRunning_AllocatesNothingAfterWarmup()
    {
        var audio = Track.Create("Audio", TrackRole.Audio);
        await SeedAsync(audio);
        var reading = new MeterReading(0.5f, 0.5f, 0.25f, 0.25f);
        _vm.MixerConsole.MeterSource = _ => reading;
        var timestamp = Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++)
        {
            _vm.MixerConsole.RefreshMeters(timestamp + i);
        }

        Assert.Equal(0, Allocations.InSteadyState(() => _vm.MixerConsole.RefreshMeters(timestamp), 300));
    }

    [Fact]
    public async Task TheAudioClipSection_FollowsTheSelectedAudioClip_AndEditsItsGainAndFades()
    {
        var clip = new AudioClip(ClipId.New(), Tick.Zero, new TickSpan(4 * 960), TickSpan.Zero, new AudioSource("loop.wav", 48_000, 96_000, 2), "Loop");
        var track = Track.Create("Audio", TrackRole.Audio).WithClip(clip);
        _session.Execute(new ProjectCommand("Set up", p => p with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) }));
        await Settle();
        Assert.False(_vm.AudioClip.HasClip);

        _vm.Arrangement.SelectClip(clip.Id);

        Assert.True(_vm.AudioClip.HasClip);
        Assert.Equal(("Loop", 4.0), (_vm.AudioClip.Title, _vm.AudioClip.MaximumFade));

        _vm.AudioClip.Gain = -2;
        _vm.AudioClip.Gain = -4;
        _vm.AudioClip.FadeIn = 1;
        _vm.AudioClip.FadeOut = 0.5;
        _vm.AudioClip.IsEqualPower = true;
        await Settle();

        var edited = Assert.IsType<AudioClip>(_session.Project.Sequence.Tracks[0].Clips[0]);
        Assert.Equal((-4.0, new TickSpan(960), new TickSpan(480), FadeShape.EqualPower), (edited.GainDecibels, edited.FadeIn, edited.FadeOut, edited.FadeShape));
        Assert.Equal("1 beats", _vm.AudioClip.FadeInText);

        // The gain drag was one step; the fades another.
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(-4, Assert.IsType<AudioClip>(_session.Project.Sequence.Tracks[0].Clips[0]).GainDecibels);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(0, _vm.AudioClip.Gain);

        _vm.Arrangement.ClearSelection();
        Assert.False(_vm.AudioClip.HasClip);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class FakeUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
