using Bluestone.Application.Editing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

public sealed class MixerSendAndClipCommandsTests
{
    private static readonly DeviceDefinitionLookup Definitions = DeviceCatalog.BuiltIn.Lookup;

    private static (EditHistory History, MixerChannel Vocal, MixerChannel Reverb) TwoChannels()
    {
        var vocal = MixerChannel.Create("Vocal");
        var reverb = MixerChannel.Create("Reverb");
        return (new EditHistory(Project.CreateNew() with { Mixer = new Mixer { Channels = [vocal, reverb] } }), vocal, reverb);
    }

    [Fact]
    public void AddMixerSend_AddsIt_AndUndoRemovesIt()
    {
        var (history, vocal, reverb) = TwoChannels();

        history.Execute(RoutingCommands.AddMixerSend(vocal.Id, new MixerSend(reverb.Id, -6), Definitions));

        Assert.Equal(-6, history.Current.Mixer.Find(vocal.Id)!.FindSend(reverb.Id)!.LevelDecibels);
        history.Undo();
        Assert.Empty(history.Current.Mixer.Find(vocal.Id)!.Sends);
    }

    [Fact]
    public void ASendThatClosesALoop_IsRefused_AndChangesNothing()
    {
        var (history, vocal, reverb) = TwoChannels();
        history.Execute(RoutingCommands.UpdateMixerChannel(reverb with { Output = vocal.Id }, Definitions));
        var before = history.Current;

        var refused = Assert.Throws<CommandRefusedException>(() => history.Execute(RoutingCommands.AddMixerSend(vocal.Id, new MixerSend(reverb.Id), Definitions)));

        Assert.Contains("feed back", refused.Message, StringComparison.Ordinal);
        Assert.Same(before, history.Current);
    }

    [Fact]
    public void ASendToItself_IsRefused()
    {
        var (history, vocal, _) = TwoChannels();

        Assert.Throws<CommandRefusedException>(() => history.Execute(RoutingCommands.AddMixerSend(vocal.Id, new MixerSend(vocal.Id), Definitions)));
    }

    [Fact]
    public void SendLevelDrag_WithOneMergeKey_IsOneUndoStep()
    {
        var (history, vocal, reverb) = TwoChannels();
        history.Execute(RoutingCommands.AddMixerSend(vocal.Id, new MixerSend(reverb.Id), Definitions));
        var key = $"send:{vocal.Id}:{reverb.Id}";

        foreach (var level in new[] { -2.0, -4.0, -6.0 })
        {
            history.Execute(RoutingCommands.UpdateMixerSend(vocal.Id, new MixerSend(reverb.Id, level), Definitions, key));
        }

        Assert.Equal(-6, history.Current.Mixer.Find(vocal.Id)!.FindSend(reverb.Id)!.LevelDecibels);
        history.Undo();
        Assert.Equal(0, history.Current.Mixer.Find(vocal.Id)!.FindSend(reverb.Id)!.LevelDecibels);
    }

    [Fact]
    public void RemoveMixerSend_And_RemoveMixerChannel_TakeTheSendAway()
    {
        var (history, vocal, reverb) = TwoChannels();
        history.Execute(RoutingCommands.AddMixerSend(vocal.Id, new MixerSend(reverb.Id), Definitions));

        history.Execute(RoutingCommands.RemoveMixerSend(vocal.Id, reverb.Id));
        Assert.Empty(history.Current.Mixer.Find(vocal.Id)!.Sends);

        history.Undo();
        history.Execute(RoutingCommands.RemoveMixerChannel(reverb.Id));
        Assert.Empty(Assert.Single(history.Current.Mixer.Channels).Sends);
    }

    private static (EditHistory History, Track Track, AudioClip Clip) OneAudioClip()
    {
        var clip = new AudioClip(ClipId.New(), Tick.Zero, new TickSpan(1920), TickSpan.Zero, new AudioSource("a.wav", 48_000, 96_000, 2));
        var track = Track.Create("Audio", TrackRole.Audio).WithClip(clip);
        return (new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) }), track, clip);
    }

    private static AudioClip ClipIn(EditHistory history, Track track, AudioClip clip) =>
        Assert.IsType<AudioClip>(history.Current.Sequence.FindTrack(track.Id)!.FindClip(clip.Id));

    [Fact]
    public void ClipGainDrag_IsOneUndoStep_AndIsClampedToItsRange()
    {
        var (history, track, clip) = OneAudioClip();

        history.Execute(ClipCommands.SetAudioClipGain(track.Id, clip.Id, -3));
        history.Execute(ClipCommands.SetAudioClipGain(track.Id, clip.Id, 40));

        Assert.Equal(AudioClip.MaxGainDecibels, ClipIn(history, track, clip).GainDecibels);
        history.Undo();
        Assert.Equal(0, ClipIn(history, track, clip).GainDecibels);
    }

    [Fact]
    public void ClipFades_AreKeptWithinTheClip_AndUndoable()
    {
        var (history, track, clip) = OneAudioClip();

        history.Execute(ClipCommands.SetAudioClipFades(track.Id, clip.Id, new TickSpan(1500), new TickSpan(960), FadeShape.EqualPower));

        var faded = ClipIn(history, track, clip);
        Assert.Equal((new TickSpan(1500), new TickSpan(420), FadeShape.EqualPower), (faded.FadeIn, faded.FadeOut, faded.FadeShape));
        history.Undo();
        Assert.Equal((TickSpan.Zero, TickSpan.Zero), (ClipIn(history, track, clip).FadeIn, ClipIn(history, track, clip).FadeOut));
    }
}
