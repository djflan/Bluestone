using Bluestone.Domain.Mixing;

namespace Bluestone.Tests.Unit.Domain.Mixing;

public sealed class MixerSendTests
{
    [Fact]
    public void WithSend_ReplacesTheSendToTheSameTarget_AndAddsOthersAtTheEnd()
    {
        var reverb = MixerChannelId.New();
        var delay = MixerChannelId.New();
        var channel = MixerChannel.Create("Vocal").WithSend(new MixerSend(reverb, -6)).WithSend(new MixerSend(delay)).WithSend(new MixerSend(reverb, -3, preFader: true));

        Assert.Equal([reverb, delay], channel.Sends.Select(s => s.Target));
        Assert.Equal(-3, channel.FindSend(reverb)!.LevelDecibels);
        Assert.True(channel.FindSend(reverb)!.PreFader);
        Assert.Equal([delay], channel.WithoutSend(reverb).Sends.Select(s => s.Target));
    }

    [Fact]
    public void Sends_ToOneTargetTwice_AreRefused()
    {
        var target = MixerChannelId.New();

        Assert.Throws<ArgumentException>(() => MixerChannel.Create("A") with { Sends = [new MixerSend(target), new MixerSend(target, -6)] });
    }

    [Theory]
    [InlineData(-120.5)]
    [InlineData(24.1)]
    public void SendLevel_OutsideTheFaderRange_IsRefused(double level) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MixerSend(MixerChannelId.New(), level));

    [Fact]
    public void AChannelSendingToItself_IsRefusedByTheMixer()
    {
        var channel = MixerChannel.Create("A");

        Assert.Throws<ArgumentException>(() => new Mixer { Channels = [channel.WithSend(new MixerSend(channel.Id))] });
    }

    [Fact]
    public void Without_RemovesTheSendsToTheRemovedChannel()
    {
        var reverb = MixerChannel.Create("Reverb");
        var vocal = MixerChannel.Create("Vocal").WithSend(new MixerSend(reverb.Id, -10));
        var mixer = new Mixer { Channels = [vocal, reverb] };

        var after = mixer.Without(reverb.Id);

        Assert.Empty(Assert.Single(after.Channels).Sends);
    }

    [Fact]
    public void Channels_WithEqualSendsInDifferentArrays_AreEqual()
    {
        var target = MixerChannelId.New();
        var channel = MixerChannel.Create("A") with { Sends = [new MixerSend(target, -6)] };
        var same = channel with { Sends = [new MixerSend(target, -6)] };

        Assert.Equal(channel, same);
        Assert.Equal(channel.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(channel, channel.WithSend(new MixerSend(target, -7)));
    }
}
