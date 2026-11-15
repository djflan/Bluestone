using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

public sealed class AudioStripAndRoutingCommandsTests
{
    private static readonly DeviceDefinitionLookup Definitions = DeviceCatalog.BuiltIn.Lookup;

    [Fact]
    public void TrackAudioMuteAndSolo_AreIndependentOfContentMuteAndSolo()
    {
        var track = Track.Create("Hybrid", TrackRole.Audio)
            .WithMuted(true)
            .WithSoloed(false)
            .WithAudioStrip(new AudioStrip { IsMuted = true });
        var history = new EditHistory(Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track),
        });

        history.Execute(AudioStripCommands.SetTrackMuted(track.Id, false));
        history.Execute(AudioStripCommands.SetTrackSoloed(track.Id, true));

        var changed = history.Current.Sequence.FindTrack(track.Id)!;
        Assert.True(changed.IsMuted);
        Assert.False(changed.IsSoloed);
        Assert.False(changed.AudioStrip!.IsMuted);
        Assert.True(changed.AudioStrip.IsSoloed);
    }

    [Fact]
    public void ConsecutiveTrackGainEdits_MergeIntoOneUndoStep()
    {
        var track = Track.Create("Audio", TrackRole.Audio);
        var time = new ManualTime();
        var history = new EditHistory(Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track),
        }, time: time);
        var first = AudioStripCommands.SetTrackGain(track.Id, -6);
        var second = AudioStripCommands.SetTrackGain(track.Id, 3);

        Assert.Equal($"audio-strip:{track.Id}:gain", first.MergeKey);
        Assert.Equal(first.MergeKey, second.MergeKey);
        history.Execute(first);
        time.Advance(TimeSpan.FromMilliseconds(10));
        history.Execute(second);

        Assert.Equal(1, history.UndoCount);
        Assert.Equal(3, history.Current.Sequence.FindTrack(track.Id)!.AudioStrip!.GainDecibels);
        history.Undo();
        Assert.Equal(AudioStrip.Default, history.Current.Sequence.FindTrack(track.Id)!.AudioStrip);
    }

    [Fact]
    public void ChangingAnAudioOutput_PreservesTheConnectionId()
    {
        var group = Track.Create("Group", TrackRole.Group);
        var source = Track.Create("Audio", TrackRole.Audio).WithGroup(group.Id);
        var original = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(source.Id), SignalNode.Master);
        var history = new EditHistory(Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(source).WithTrack(group),
            Connections =
            [
                original,
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(group.Id), SignalNode.Master),
            ],
        });
        var command = RoutingCommands.SetAudioOutput(source.Id, SignalNode.Track(group.Id), Definitions);

        Assert.Equal($"audio-output:{source.Id}", command.MergeKey);
        history.Execute(command);

        var output = Assert.Single(history.Current.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Output && connection.Source == SignalNode.Track(source.Id));
        Assert.Equal(original.Id, output.Id);
        Assert.Equal(SignalNode.Track(group.Id), output.Destination);
    }

    [Fact]
    public void AudioSendUpdates_KeepTheirIdAndMergeIntoOneUndoStep()
    {
        var source = Track.Create("Vocal", TrackRole.Audio);
        var firstEffect = Track.Create("Reverb", TrackRole.Effect);
        var secondEffect = Track.Create("Delay", TrackRole.Effect);
        var thirdEffect = Track.Create("Chorus", TrackRole.Effect);
        var history = new EditHistory(Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default)
                .WithTrack(source).WithTrack(firstEffect).WithTrack(secondEffect).WithTrack(thirdEffect),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(source.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(firstEffect.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(secondEffect.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(thirdEffect.Id), SignalNode.Master),
            ],
        }, time: new ManualTime());
        var add = RoutingCommands.AddAudioSend(source.Id, firstEffect.Id, -3, false, Definitions);

        Assert.Equal($"audio-send:{source.Id}:{firstEffect.Id}", add.MergeKey);
        history.Execute(add);
        var sendId = Assert.Single(history.Current.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send).Id;
        history.Execute(RoutingCommands.AddAudioSend(source.Id, firstEffect.Id, -9, true, Definitions));
        Assert.Equal(sendId, Assert.Single(history.Current.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send).Id);

        const string mergeKey = "test-send-adjustment";
        history.Execute(RoutingCommands.UpdateAudioSend(sendId, secondEffect.Id, -6, true, Definitions, mergeKey));
        history.Execute(RoutingCommands.UpdateAudioSend(sendId, thirdEffect.Id, -1.5, false, Definitions, mergeKey));

        Assert.Equal(2, history.UndoCount);
        var changed = Assert.Single(history.Current.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal(sendId, changed.Id);
        Assert.Equal(SignalNode.Track(thirdEffect.Id), changed.Destination);
        Assert.Equal((-1.5, false), (changed.SendLevelDecibels, changed.SendPreFader));

        history.Undo();
        var restored = Assert.Single(history.Current.Connections, connection => connection.AudioPurpose == AudioRoutePurpose.Send);
        Assert.Equal(sendId, restored.Id);
        Assert.Equal(SignalNode.Track(firstEffect.Id), restored.Destination);
        Assert.Equal((-9d, true), (restored.SendLevelDecibels, restored.SendPreFader));
    }

    [Fact]
    public void InvalidAudioOutputAndSend_AreRefusedWithoutChangingTheProject()
    {
        var source = Track.Create("Source", TrackRole.Audio);
        var otherAudio = Track.Create("Other", TrackRole.Audio);
        var effect = Track.Create("Effect", TrackRole.Effect);
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(source).WithTrack(otherAudio).WithTrack(effect),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(source.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(otherAudio.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(effect.Id), SignalNode.Master),
            ],
        };
        var history = new EditHistory(project);

        var invalidOutput = Assert.Throws<CommandRefusedException>(() =>
            history.Execute(RoutingCommands.SetAudioOutput(source.Id, SignalNode.Track(otherAudio.Id), Definitions)));
        Assert.Contains(invalidOutput.Issues, issue => issue.Code == RoutingIssueCode.InvalidAudioOwner);
        Assert.Same(project, history.Current);

        var invalidSend = Assert.Throws<CommandRefusedException>(() =>
            history.Execute(RoutingCommands.AddAudioSend(source.Id, otherAudio.Id, -6, false, Definitions)));
        Assert.Contains(invalidSend.Issues, issue => issue.Code == RoutingIssueCode.InvalidSendTarget);
        Assert.Same(project, history.Current);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan amount) => _ticks += amount.Ticks;
    }
}
