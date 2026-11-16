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
        Assert.Equal("Reverb", send.TargetName);
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

    private static DeviceInstance Effect(string name) =>
        DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId($"plugin:vst3:test/{name.Replace(' ', '-')}"), name));

    private async Task<Track> SeedWithInsertsAsync(params string[] devices)
    {
        var track = Track.Create("Bass", TrackRole.Audio);
        await SeedAsync(track);
        if (devices.Length > 0)
        {
            _session.Execute(new ProjectCommand("Add inserts", project =>
                project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [.. devices.Select(Effect)] })));
            await Settle();
        }

        return track;
    }

    [Fact]
    public async Task Inserts_FollowChainOrder_AndTheEnabledToggleBypassesTheDeviceAsOneUndoStep()
    {
        await SeedWithInsertsAsync("Stone EQ", "Opto", "Saturator");
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        var chain = _session.Project.Chains.Single(candidate => candidate.Owner.Kind == ChainOwnerKind.Track);
        Assert.Equal(["Stone EQ", "Opto", "Saturator"], row.Inserts.Select(insert => insert.Name));
        Assert.Equal(chain.Devices.Select(device => device.Id), row.Inserts.Select(insert => insert.DeviceId));
        Assert.All(row.Inserts, insert => Assert.True(insert.IsEnabled));
        var opto = row.Inserts[1];

        opto.IsEnabled = false;
        await Settle();

        Assert.True(_session.Project.FindDevice(opto.DeviceId)!.Value.Device.IsBypassed);
        Assert.False(opto.IsEnabled);
        Assert.Same(opto, row.Inserts[1]);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.False(_session.Project.FindDevice(opto.DeviceId)!.Value.Device.IsBypassed);
        Assert.True(opto.IsEnabled);
    }

    [Theory]
    [InlineData(0, 0, 0, "", "0 DEVICES")]
    [InlineData(1, 1, 0, "", "1 DEVICE")]
    [InlineData(3, 3, 0, "", "3 DEVICES")]
    [InlineData(5, 3, 2, "+2 more", "5 DEVICES")]
    public async Task VisibleInserts_ShowTheFirstRowsAndSummarizeTheRest(int devices, int visible, int overflow, string overflowText, string countText)
    {
        await SeedWithInsertsAsync([.. Enumerable.Range(1, devices).Select(i => $"FX {i}")]);
        var row = Assert.Single(_vm.MixerConsole.Tracks);

        Assert.Equal(row.Inserts.Take(visible), row.VisibleInserts);
        Assert.Equal((overflow, overflow > 0, overflowText, devices == 0, countText), (row.OverflowInsertCount, row.HasOverflow, row.OverflowText, row.HasNoInserts, row.InsertCountText));
    }

    [Fact]
    public async Task RemovingAnInsert_ShrinksTheVisibleRowsAndOverflowInPlace()
    {
        await SeedWithInsertsAsync("A", "B", "C", "D");
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        Assert.Equal(1, row.OverflowInsertCount);
        var first = row.Inserts[0];

        _session.Execute(DeviceCommands.RemoveDevice(row.Inserts[1].DeviceId, _vm.Devices.Lookup));
        await Settle();

        Assert.Equal(["A", "C", "D"], row.VisibleInserts.Select(insert => insert.Name));
        Assert.Same(first, row.Inserts[0]);
        Assert.False(row.HasOverflow);
    }

    [Fact]
    public async Task RemovingAnInsertAbove_KeepsEachRemainingRowOnItsOwnDevice()
    {
        await SeedWithInsertsAsync("A", "B", "C");
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        var (b, c) = (row.Inserts[1], row.Inserts[2]);
        var (bDevice, cDevice) = (b.DeviceId, c.DeviceId);

        _session.Execute(DeviceCommands.RemoveDevice(row.Inserts[0].DeviceId, _vm.Devices.Lookup));
        await Settle();

        Assert.Equal([b, c], row.Inserts);
        Assert.Equal([b, c], row.VisibleInserts);
        Assert.Equal((bDevice, cDevice), (b.DeviceId, c.DeviceId));
    }

    [Theory]
    [InlineData(MixerMeterMode.PeakRms)]
    [InlineData(MixerMeterMode.Vu)]
    [InlineData(MixerMeterMode.K14)]
    [InlineData(MixerMeterMode.LufsS)]
    public async Task TheClipLight_LatchesOverZeroDbfs_UntilReset_InEveryMeterMode(MixerMeterMode mode)
    {
        await SeedAsync(Track.Create("Audio", TrackRole.Audio));
        var timestamp = Stopwatch.GetTimestamp();
        foreach (var strip in new MixerMeterStripViewModel[] { Assert.Single(_vm.MixerConsole.Tracks), _vm.MixerConsole.Master })
        {
            var clips = 0;
            strip.PropertyChanged += (_, e) => clips += e.PropertyName == nameof(MixerMeterStripViewModel.IsClipped) ? 1 : 0;

            strip.UpdateMeter(new MixerMeterReading(new MeterReading(1.2f, 0.5f, 0.5f, 0.3f), mode, -6, -9, true), mode, timestamp);
            strip.UpdateMeter(new MixerMeterReading(new MeterReading(0.1f, 0.1f, 0.05f, 0.05f), mode, -30, -30, true), mode, timestamp + 1);
            strip.UpdateMeter(null, mode, timestamp + 2);

            Assert.True(strip.IsClipped);
            Assert.Equal(1, clips);
            strip.ResetClipCommand.Execute(null);
            Assert.False(strip.IsClipped);
            strip.UpdateMeter(new MixerMeterReading(new MeterReading(0.9f, 0.9f, 0.5f, 0.5f), mode, -1, -1, true), mode, timestamp + 3);
            Assert.False(strip.IsClipped);
        }
    }

    [Fact]
    public async Task ResetAfterADrag_IsItsOwnUndoStep_ForTrackMasterAndSend()
    {
        var audio = Track.Create("Vocal", TrackRole.Audio);
        var effect = Track.Create("Reverb", TrackRole.Effect);
        await SeedAsync(audio, effect);
        var row = _vm.MixerConsole.Tracks.Single(strip => strip.Id == audio.Id);
        row.NewSendTarget = Assert.Single(row.SendTargets);
        row.AddSendCommand.Execute(null);
        await Settle();
        var send = Assert.Single(row.Sends);
        var master = _vm.MixerConsole.Master;

        row.Gain = -8;
        row.ResetGainCommand.Execute(null);
        row.Pan = 0.4;
        row.ResetPanCommand.Execute(null);
        master.Gain = -5;
        master.ResetGainCommand.Execute(null);
        master.Pan = -0.3;
        master.ResetPanCommand.Execute(null);
        send.Level = -12;
        send.ResetLevelCommand.Execute(null);
        await Settle();

        Assert.Equal((0.0, 0.0, 0.0, 0.0, 0.0), (row.Gain, row.Pan, master.Gain, master.Pan, send.Level));
        var expected = new (Func<double> Value, double Adjusted)[]
        {
            (() => send.Level, -12), (() => master.Pan, -0.3), (() => master.Gain, -5), (() => row.Pan, 0.4), (() => row.Gain, -8),
        };
        foreach (var (value, adjusted) in expected)
        {
            _vm.UndoCommand.Execute(null);
            await Settle();
            Assert.Equal(adjusted, value(), 6);
            _vm.UndoCommand.Execute(null);
            await Settle();
            Assert.Equal(0, value(), 6);
        }
    }

    [Fact]
    public async Task OpeningAnInsert_SelectsItsTrackAndRevealsTheChainAtThatDevice()
    {
        var other = Track.Create("Other", TrackRole.Audio);
        var track = await SeedWithInsertsAsync("Stone EQ", "Opto");
        _session.Execute(new ProjectCommand("Add other", project => project with { Sequence = project.Sequence.WithTrack(other) }));
        await Settle();
        _vm.Select(_vm.Tracks.Single(row => row.Id == other.Id));
        var strip = _vm.MixerConsole.Tracks.Single(row => row.Id == track.Id);

        strip.Inserts[1].OpenCommand.Execute(null);
        await Settle();

        Assert.Equal(track.Id, Assert.Single(_vm.SelectedTracks).Id);
        Assert.Equal(strip.Inserts[1].DeviceId, _vm.Workspace.ActiveDeviceView?.SelectedDevice?.Id);
    }

    [Theory]
    [InlineData(0.0, "<C>")]
    [InlineData(0.004, "<C>")]
    [InlineData(-0.41, "L41")]
    [InlineData(0.35, "R35")]
    [InlineData(-1.0, "L100")]
    public async Task PanText_ShowsCenterOrSideAndWholePercent(double pan, string expected)
    {
        await SeedAsync(Track.Create("Audio", TrackRole.Audio));
        var row = Assert.Single(_vm.MixerConsole.Tracks);

        row.Pan = pan;
        _vm.MixerConsole.Master.Pan = pan;
        await Settle();

        Assert.Equal((expected, expected), (row.PanText, _vm.MixerConsole.Master.PanText));
    }

    [Fact]
    public async Task GainValueText_IsTheSignedNumberWithoutUnits()
    {
        await SeedAsync(Track.Create("Audio", TrackRole.Audio));
        var row = Assert.Single(_vm.MixerConsole.Tracks);

        row.Gain = -7.24;
        _vm.MixerConsole.Master.Gain = 1.5;
        await Settle();

        Assert.Equal(("-7.2", "+1.5"), (row.GainValueText, _vm.MixerConsole.Master.GainValueText));
        row.Gain = 0;
        Assert.Equal("0.0", row.GainValueText);
    }

    [Fact]
    public async Task TheSingleMeter_FillsToTheLouderRms_WithTheHeldPeakAsItsMarkerAndReadout()
    {
        await SeedAsync(Track.Create("Audio", TrackRole.Audio));
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        Assert.True(double.IsNaN(row.MeterValue));
        Assert.Equal("—", row.PeakText);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _vm.MixerConsole.MeterSource = _ => new MeterReading(0.25f, 0.66f, 0.1f, 0.2f);
        var start = Stopwatch.GetTimestamp();

        _vm.MixerConsole.RefreshMeters(start);

        // Peak + RMS: the fill is RMS (the louder side), the marker and readout the held peak.
        Assert.Equal(MeterReading.ToDecibels(0.2f), row.MeterValue, 5);
        Assert.Equal(MeterReading.ToDecibels(0.66f), row.MeterPeakHold, 5);
        Assert.True(row.MeterPeakHold > row.MeterValue);
        Assert.Equal("-3.6", row.PeakText);
        Assert.Contains(nameof(row.MeterValue), changed);
        Assert.Contains(nameof(row.MeterPeakHold), changed);
        Assert.Contains(nameof(row.PeakText), changed);

        // The level falls within the hold time: the fill follows the RMS down; the held readout neither changes nor notifies.
        changed.Clear();
        _vm.MixerConsole.MeterSource = _ => new MeterReading(0.1f, 0.1f, 0.05f, 0.05f);
        _vm.MixerConsole.RefreshMeters(start + 1);
        Assert.Equal(MeterReading.ToDecibels(0.05f), row.MeterValue, 5);
        Assert.Equal("-3.6", row.PeakText);
        Assert.Contains(nameof(row.MeterValue), changed);
        Assert.DoesNotContain(nameof(row.PeakText), changed);

        _vm.MixerConsole.MeterSource = _ => null;
        _vm.MixerConsole.RefreshMeters(start + 2);
        Assert.True(double.IsNaN(row.MeterValue));
        Assert.Equal("—", row.PeakText);
    }

    [Fact]
    public async Task RefreshMeters_WithChangingLevelsAndABoundReader_AllocatesNothingAfterWarmup()
    {
        await SeedAsync(Track.Create("Audio", TrackRole.Audio));
        var row = Assert.Single(_vm.MixerConsole.Tracks);
        var readings = new[] { new MeterReading(0.3f, 0.2f, 0.1f, 0.1f), new MeterReading(0.6f, 0.7f, 0.3f, 0.35f) };
        var frame = 0;
        _vm.MixerConsole.MeterSource = _ => readings[frame & 1];
        double sink = 0;
        var texts = 0;
        row.PropertyChanged += (_, _) =>
        {
            sink += row.MeterValue + row.MeterPeakHold;
            texts += row.PeakText.Length;
        };
        _vm.MixerConsole.Master.PropertyChanged += (_, _) => texts += _vm.MixerConsole.Master.PeakText.Length;

        // Each frame is past the hold time, so the held peak, its readout, and the fill all change every frame.
        var start = Stopwatch.GetTimestamp();
        var hold = (long)(MeterLevels.HoldTime.TotalSeconds * Stopwatch.Frequency) + 1;
        void Step()
        {
            frame++;
            _vm.MixerConsole.RefreshMeters(start + (frame * hold));
        }

        for (var i = 0; i < 10; i++)
        {
            Step();
        }

        Assert.Equal(0, Allocations.InSteadyState(Step, 300));
        Assert.True(texts > 0 && sink != 0);
    }

    [Fact]
    public async Task VisibleSends_ShowTheFirstTwoKnobs_AndCountTheRestInPlace()
    {
        var vocal = Track.Create("Vocal", TrackRole.Audio);
        var effects = Enumerable.Range(1, 3).Select(i => Track.Create($"Return {i}", TrackRole.Effect)).ToArray();
        await SeedAsync([vocal, .. effects]);
        var row = _vm.MixerConsole.Tracks.Single(strip => strip.Id == vocal.Id);
        Assert.Empty(row.VisibleSends);
        Assert.Equal("No sends, add a send", row.SendsAutomationName);

        foreach (var effect in effects)
        {
            _session.Execute(RoutingCommands.AddAudioSend(vocal.Id, effect.Id, 0, false, _vm.Devices.Lookup));
        }

        await Settle();
        Assert.Equal(row.Sends.Take(2), row.VisibleSends);
        Assert.Equal((1, true, "3 sends, show all or add"), (row.OverflowSendCount, row.HasSendOverflow, row.SendsAutomationName));
        Assert.Equal("Send to Return 1 level", row.VisibleSends[0].LevelAutomationName);
        var first = row.VisibleSends[0];

        row.Sends[2].RemoveCommand.Execute(null);
        await Settle();
        Assert.Same(first, row.VisibleSends[0]);
        Assert.Equal((2, 0, false, "2 sends, show all or add"), (row.VisibleSends.Count, row.OverflowSendCount, row.HasSendOverflow, row.SendsAutomationName));
    }

    [Fact]
    public async Task AutomationNames_NameTheTrackAndFollowRenameAndSelection()
    {
        var kick = Track.Create("Kick", TrackRole.Audio);
        var other = Track.Create("Other", TrackRole.Audio);
        await SeedAsync(kick, other);
        _session.Execute(new ProjectCommand("Add insert", project =>
            project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(kick.Id)) with { Devices = [Effect("Tone EQ")] })));
        _vm.Select(_vm.Tracks.Single(track => track.Id == other.Id));
        await Settle();
        var row = _vm.MixerConsole.Tracks.Single(strip => strip.Id == kick.Id);

        Assert.Equal(
            ("Kick gain", "Kick pan", "Mute Kick", "Solo Kick", "Record arm Kick", "Select Kick", "Output: Master", "Tone EQ power"),
            (row.GainAutomationName, row.PanAutomationName, row.MuteAutomationName, row.SoloAutomationName, row.ArmAutomationName,
                row.SelectAutomationName, row.OutputAutomationName, row.Inserts[0].PowerAutomationName));

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _vm.Select(_vm.Tracks.Single(track => track.Id == kick.Id));
        _session.Execute(new ProjectCommand("Rename", project => project with
        {
            Sequence = new Sequence(
                project.Sequence.TempoMap,
                project.Sequence.MeterMap,
                [.. project.Sequence.Tracks.Select(track => track.Id == kick.Id ? track.WithName("Kick In") : track)],
                project.Sequence.Markers),
        }));
        await Settle();

        Assert.Equal(("Kick In, selected", "Kick In gain"), (row.SelectAutomationName, row.GainAutomationName));
        Assert.Contains(nameof(row.SelectAutomationName), changed);
        Assert.Contains(nameof(row.GainAutomationName), changed);

        var master = _vm.MixerConsole.Master;
        Assert.Equal(("Master gain", "Select Master"), (master.GainAutomationName, master.SelectAutomationName));
        master.SelectCommand.Execute(null);
        Assert.Equal("Master, selected", master.SelectAutomationName);
    }

    [Fact]
    public async Task StripIdentity_MatchesTheArrangementNumberAndColour_AndGroupsCountTheirMembers()
    {
        var group = Track.Create("Drums", TrackRole.Group);
        var kick = Track.Create("Kick", TrackRole.Audio).WithGroup(group.Id);
        var snare = Track.Create("Snare", TrackRole.Audio).WithGroup(group.Id);
        var tracks = Enumerable.Range(1, 6).Select(i => Track.Create($"T{i}", TrackRole.Audio)).Concat([group, kick, snare]).ToArray();
        await SeedAsync(tracks);

        foreach (var strip in _vm.MixerConsole.Tracks)
        {
            var arrangement = _vm.Tracks.Single(track => track.Id == strip.Id);
            Assert.Equal((arrangement.Number, arrangement.ColorIndex), (strip.Number, strip.ColorIndex));
        }

        var groupStrip = _vm.MixerConsole.Tracks.Single(row => row.Id == group.Id);
        Assert.Equal(("07", 6, 2, "GROUP · 2", "GROUP"), (groupStrip.NumberText, groupStrip.ColorIndex, groupStrip.GroupChildCount, groupStrip.RoleCaption, groupStrip.RoleLabel));
        Assert.Equal(0, _vm.MixerConsole.Tracks.Single(row => row.Id == snare.Id).ColorIndex);
    }

    [Fact]
    public async Task ArmingAStrip_UsesTheArrangementsSingleArmedTrack()
    {
        var first = Track.Create("First", TrackRole.Audio);
        var second = Track.Create("Second", TrackRole.Hybrid);
        var group = Track.Create("Group", TrackRole.Group);
        await SeedAsync(first, second, group);
        var strips = _vm.MixerConsole.Tracks.ToDictionary(row => row.Id);

        strips[first.Id].IsArmed = true;
        Assert.Equal(first.Id, _vm.ArmedTrack?.Id);
        Assert.True(_vm.Tracks.Single(track => track.Id == first.Id).IsArmed);

        strips[second.Id].IsArmed = true;
        Assert.Equal(second.Id, _vm.ArmedTrack?.Id);
        Assert.False(strips[first.Id].IsArmed);

        _vm.ToggleArm(_vm.Tracks.Single(track => track.Id == second.Id));
        Assert.Null(_vm.ArmedTrack);
        Assert.False(strips[second.Id].IsArmed);

        Assert.False(strips[group.Id].CanArm);
        strips[group.Id].IsArmed = true;
        Assert.False(strips[group.Id].IsArmed);
        Assert.Null(_vm.ArmedTrack);
    }

    [Fact]
    public void MeterSettings_FromBeforeTheSingleLedMeter_StillLoad_AndSaveWithoutAPresentation()
    {
        var directory = Directory.CreateTempSubdirectory("bluestone-mixer-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "mixer-meters.json");
            File.WriteAllText(path, """{ "mode": "vu", "presentation": "round", "vuReferenceDbfs": -14 }""");
            var store = new MixerMeterDisplaySettingsStore(path);

            var loaded = store.Load();
            Assert.Equal((MixerMeterMode.Vu, -14), (loaded.Mode, loaded.VuReferenceDbfs));

            File.WriteAllText(path, """{ "mode": "k14", "presentation": "sparkle" }""");
            Assert.Equal(MixerMeterMode.K14, store.Load().Mode);

            _vm.MixerConsole.UseSettingsStore(store);
            _vm.MixerConsole.VuReferenceDbfs = -20;
            Assert.DoesNotContain("presentation", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal((MixerMeterMode.K14, -20), (store.Load().Mode, store.Load().VuReferenceDbfs));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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
