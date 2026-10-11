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

/// <summary>The mixer console's view model (shared with the inspector's Mixer section) and the audio clip section.</summary>
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

    private async Task<(MixerChannelRow Vocal, MixerChannelRow Reverb)> TwoChannelsAsync()
    {
        _vm.Mixer.NewChannelCommand.Execute(null);
        _vm.Mixer.NewChannelCommand.Execute(null);
        await Settle();
        _vm.Mixer.Channels[0].Name = "Vocal";
        _vm.Mixer.Channels[1].Name = "Reverb";
        await Settle();
        return (_vm.Mixer.Channels[0], _vm.Mixer.Channels[1]);
    }

    [Fact]
    public async Task Sends_AreAddedChangedAndRemoved_ALevelDragIsOneUndoStep_AndALoopIsRefused()
    {
        var (vocal, reverb) = await TwoChannelsAsync();
        Assert.Equal(["Reverb"], vocal.SendTargets.Select(t => t.Name));

        vocal.AddSendCommand.Execute(vocal.SendTargets[0]);
        await Settle();
        var send = Assert.Single(vocal.Sends);
        Assert.Equal(("Reverb", 0.0, false), (send.TargetName, send.Level, send.IsPreFader));
        Assert.Empty(vocal.SendTargets);
        Assert.False(vocal.CanAddSend);
        Assert.Equal("Return", reverb.Role);
        Assert.Equal("From Vocal (send)", reverb.Sources);

        send.Level = -3;
        send.Level = -6;
        send.Level = -12;
        await Settle();
        Assert.Equal(-12, _session.Project.Mixer.Channels[0].Sends[0].LevelDecibels);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal(0, _session.Project.Mixer.Channels[0].Sends[0].LevelDecibels);
        Assert.Equal(0, vocal.Sends[0].Level);

        vocal.Sends[0].IsPreFader = true;
        await Settle();
        Assert.True(_session.Project.Mixer.Channels[0].Sends[0].PreFader);

        // The return cannot feed the channel that sends to it.
        reverb.Output = reverb.Outputs.Single(o => o.Name == "Vocal");
        await Settle();
        Assert.Null(_session.Project.Mixer.Channels[1].Output);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("feed back", StringComparison.Ordinal));

        vocal.Sends[0].RemoveCommand.Execute(null);
        await Settle();
        Assert.Empty(vocal.Sends);
        Assert.Empty(_session.Project.Mixer.Channels[0].Sends);
    }

    [Fact]
    public async Task Sources_SayWhatFeedsAChannel_AndAGroupTracksChannelIsAGroup()
    {
        var (vocal, reverb) = await TwoChannelsAsync();
        var group = Track.Create("Band", TrackRole.Group);
        var project = _session.Project;
        _session.Execute(new ProjectCommand("Set up", p => p with
        {
            Sequence = p.Sequence.WithTrack(group),
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(group.Id), SignalNode.Mixer(project.Mixer.Channels[0].Id))],
            Mixer = p.Mixer.With(p.Mixer.Channels[1] with { Output = p.Mixer.Channels[0].Id }),
        }));
        await Settle();

        Assert.Equal("Group", vocal.Role);
        Assert.Equal("From track \"Band\", Reverb (output)", vocal.Sources);
        Assert.Equal("Nothing feeds this channel.", reverb.Sources);
    }

    [Fact]
    public async Task RefreshMeters_ReadsEveryStripAndTheMaster_HoldsPeaks_AndLatchesClipping()
    {
        var (vocal, _) = await TwoChannelsAsync();
        var readings = new Dictionary<MixerChannelId, MeterReading> { [vocal.Id] = new(0.5f, 1.25f, 0.25f, 0.25f) };
        var master = new MeterReading(0.1f, 0.1f, 0.05f, 0.05f);
        _vm.Mixer.MeterSource = id => id is { } channel ? (readings.TryGetValue(channel, out var r) ? r : null) : master;
        var start = Stopwatch.GetTimestamp();

        _vm.Mixer.RefreshMeters(start);

        Assert.Equal(-6.02, vocal.Meter.PeakLeft, 2);
        Assert.Equal(1.94, vocal.Meter.PeakRight, 2);
        Assert.Equal(-12.04, vocal.Meter.RmsLeft, 2);
        Assert.True(vocal.Meter.Clipped);
        Assert.True(vocal.Meter.IsActive);
        Assert.False(_vm.Mixer.Channels[1].Meter.IsActive);
        Assert.Equal(MeterLevels.FloorDecibels, _vm.Mixer.Channels[1].Meter.PeakLeft);
        Assert.Equal(-20, _vm.Mixer.MasterMeter.PeakLeft, 6);

        // A quieter reading: the hold stays at the earlier peak for two seconds, then follows.
        readings[vocal.Id] = new MeterReading(0.1f, 0.1f, 0.05f, 0.05f);
        _vm.Mixer.RefreshMeters(start + Stopwatch.Frequency);
        Assert.Equal(-20, vocal.Meter.PeakLeft, 6);
        Assert.Equal(-6.02, vocal.Meter.HoldLeft, 2);
        Assert.True(vocal.Meter.Clipped);

        _vm.Mixer.RefreshMeters(start + (Stopwatch.Frequency * 5 / 2));
        Assert.Equal(-20, vocal.Meter.HoldLeft, 6);

        vocal.Meter.ResetClip();
        Assert.False(vocal.Meter.Clipped);
    }

    [Fact]
    public async Task RefreshMeters_WithAudioOff_ShowsTheFloor_AndAllocatesNothing()
    {
        await TwoChannelsAsync();
        var reading = new MeterReading(0.5f, 0.5f, 0.25f, 0.25f);
        var on = true;
        _vm.Mixer.MeterSource = _ => on ? reading : null;
        var updates = 0;
        _vm.Mixer.Channels[0].Meter.Updated += (_, _) => updates++;
        for (var i = 0; i < 10; i++)
        {
            _vm.Mixer.RefreshMeters(Stopwatch.GetTimestamp());
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++)
        {
            _vm.Mixer.RefreshMeters(Stopwatch.GetTimestamp());
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(310, updates);

        on = false;
        _vm.Mixer.RefreshMeters(Stopwatch.GetTimestamp());
        Assert.False(_vm.Mixer.MasterMeter.IsActive);
        Assert.Equal(MeterLevels.FloorDecibels, _vm.Mixer.Channels[0].Meter.PeakLeft);
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
