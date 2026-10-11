using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

public sealed class AudioClipTests
{
    private static AudioClip Clip(long start = 0, long length = 1000) =>
        new(ClipId.New(), new Tick(start), new TickSpan(length), TickSpan.Zero, new AudioSource("a.wav", 48_000, 480_000, 2));

    [Fact]
    public void NewClip_HasUnityGainNoFades_AndLinearShape()
    {
        var clip = Clip();

        Assert.Equal(0, clip.GainDecibels);
        Assert.Equal(TickSpan.Zero, clip.FadeIn);
        Assert.Equal(TickSpan.Zero, clip.FadeOut);
        Assert.Equal(FadeShape.Linear, clip.FadeShape);
    }

    [Theory]
    [InlineData(-121)]
    [InlineData(24.5)]
    public void Gain_OutsideItsRange_IsRefused(double decibels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Clip() with { GainDecibels = decibels });

    [Fact]
    public void EffectiveFades_KeepTheFadeInFirst_AndTheFadeOutWithinWhatRemains()
    {
        var clip = Clip(length: 1000) with { FadeIn = new TickSpan(700), FadeOut = new TickSpan(500) };

        Assert.Equal((new TickSpan(700), new TickSpan(300)), clip.EffectiveFades);
        Assert.Equal((new TickSpan(1000), TickSpan.Zero), (clip with { FadeIn = new TickSpan(5000) }).EffectiveFades);
    }

    [Fact]
    public void SplitAt_LeftKeepsTheFadeIn_RightKeepsTheFadeOut_AndBothKeepGainAndShape()
    {
        var clip = Clip(length: 1000) with { GainDecibels = -6, FadeIn = new TickSpan(100), FadeOut = new TickSpan(200), FadeShape = FadeShape.EqualPower };

        var (left, right) = clip.SplitAt(new Tick(400), ClipId.New());

        var l = Assert.IsType<AudioClip>(left);
        var r = Assert.IsType<AudioClip>(right);
        Assert.Equal((new TickSpan(100), TickSpan.Zero), (l.FadeIn, l.FadeOut));
        Assert.Equal((TickSpan.Zero, new TickSpan(200)), (r.FadeIn, r.FadeOut));
        Assert.All([l, r], c => Assert.Equal((-6.0, FadeShape.EqualPower), (c.GainDecibels, c.FadeShape)));
    }

    [Fact]
    public void WithBounds_ShorterThanTheFades_ShortensThem()
    {
        var clip = Clip(length: 1000) with { FadeIn = new TickSpan(400), FadeOut = new TickSpan(400) };

        var trimmed = clip.WithBounds(new Tick(0), new Tick(500));

        Assert.Equal((new TickSpan(400), new TickSpan(100)), (trimmed.FadeIn, trimmed.FadeOut));
    }
}
