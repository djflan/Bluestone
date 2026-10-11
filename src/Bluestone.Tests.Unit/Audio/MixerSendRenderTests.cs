using Bluestone.Audio;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>Sends in the render graph: exact levels before and after the fader, mute, solo, ramps, and order.</summary>
public sealed class MixerSendRenderTests
{
    private const int Rate = 48_000;
    private const int Block = 64;

    private sealed class ConstantNode(float value) : AudioNode
    {
        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            output.Left.Fill(value);
            output.Right.Fill(value);
        }
    }

    private sealed record Mix(AudioGraph Graph, MixerStripState Source, MixerStripState Return);

    // A constant 1.0 into strip Source, which goes to the master and sends to strip Return (also to the master).
    private static Mix Build(MixerStripSettings source, MixerStripSettings @return, double sendDecibels, bool preFader, MixerStripState? sourceState = null, MixerStripState? returnState = null)
    {
        var builder = new AudioGraphBuilder();
        var sourceStrip = sourceState ?? new MixerStripState();
        var returnStrip = returnState ?? new MixerStripState();

        // The return is added first: strips render in dependency order, not in the order they were added.
        var r = builder.AddStrip(returnStrip, @return);
        var s = builder.AddStrip(sourceStrip, source);
        builder.ConnectToStrip(builder.AddNode(new ConstantNode(1f)), s);
        builder.SendStrip(s, r, sendDecibels, preFader);
        return new Mix(builder.Build(Block, new MixerStripState()), sourceStrip, returnStrip);
    }

    private static float[] Render(AudioGraph graph, int frames = Block)
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(graph);
        return new OfflineAudioDriver(engine, serviceStreams: false).Render(frames, Block);
    }

    [Fact]
    public void PostFaderSend_CarriesTheFaderAndPan_TimesTheSendLevel()
    {
        var mix = Build(new MixerStripSettings(GainDecibels: -6, Pan: 0.5), new MixerStripSettings(), sendDecibels: -10, preFader: false);

        var output = Render(mix.Graph);

        var fader = PanLaw.DecibelsToGain(-6);
        var (left, right) = PanLaw.Gains(0.5);
        var level = PanLaw.DecibelsToGain(-10);
        var sentLeft = level * fader * left;
        var sentRight = level * fader * right;
        Assert.Equal(sentLeft, mix.Return.Meter.Read().PeakLeft);
        Assert.Equal(sentRight, mix.Return.Meter.Read().PeakRight);
        Assert.Equal((fader * left) + sentLeft, output[0]);
        Assert.Equal((fader * right) + sentRight, output[1]);
    }

    [Fact]
    public void PreFaderSend_IgnoresTheFaderAndPan()
    {
        var mix = Build(new MixerStripSettings(GainDecibels: -120, Pan: -1), new MixerStripSettings(GainDecibels: -3), sendDecibels: -10, preFader: true);

        var output = Render(mix.Graph);

        var level = PanLaw.DecibelsToGain(-10);

        // The return's meter is after its own fader.
        Assert.Equal(level * PanLaw.DecibelsToGain(-3), mix.Return.Meter.Read().PeakLeft);
        Assert.Equal(level * PanLaw.DecibelsToGain(-3), mix.Return.Meter.Read().PeakRight);
        Assert.Equal(0f, mix.Source.Meter.Read().PeakLeft);
        Assert.Equal(level * PanLaw.DecibelsToGain(-3), output[0]);
        Assert.Equal(level * PanLaw.DecibelsToGain(-3), output[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mute_SilencesTheSendsToo(bool preFader)
    {
        var mix = Build(new MixerStripSettings(Muted: true), new MixerStripSettings(), 0, preFader);

        var output = Render(mix.Graph);

        Assert.All(output, s => Assert.Equal(0f, s));
        Assert.Equal(0f, mix.Return.Meter.Read().PeakLeft);
    }

    [Fact]
    public void SoloOfTheSource_KeepsItsReturn()
    {
        var mix = Build(new MixerStripSettings(Soloed: true), new MixerStripSettings(), 0, preFader: false);

        var output = Render(mix.Graph);

        Assert.Equal(2f, output[0]);
        Assert.Equal(1f, mix.Return.Meter.Read().PeakLeft);
    }

    [Fact]
    public void SoloOfTheReturn_PlaysWhatIsSentToIt_WithoutTheDryChannel()
    {
        var mix = Build(new MixerStripSettings(), new MixerStripSettings(Soloed: true), -6, preFader: false);

        var output = Render(mix.Graph);

        Assert.Equal(PanLaw.DecibelsToGain(-6), output[0]);
        Assert.Equal(0f, mix.Source.Meter.Read().PeakLeft);
    }

    [Fact]
    public void SoloOfAnotherStrip_SilencesTheSendOfAStripThatDoesNotSound()
    {
        var builder = new AudioGraphBuilder();
        var r = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var a = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var b = builder.AddStrip(new MixerStripState(), new MixerStripSettings(Soloed: true));
        builder.ConnectToStrip(builder.AddNode(new ConstantNode(1f)), a);
        builder.ConnectToStrip(builder.AddNode(new ConstantNode(0.25f)), b);
        builder.SendStrip(a, r, 0, preFader: true);

        var output = Render(builder.Build(Block, new MixerStripState()));

        Assert.Equal(0.25f, output[0]);
    }

    [Fact]
    public void ASendLevelChange_RampsOverOneBlock()
    {
        var source = new MixerStripState();
        var @return = new MixerStripState();
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(Build(new MixerStripSettings(GainDecibels: -120), new MixerStripSettings(), -120, preFader: true, source, @return).Graph);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        driver.Render(Block, Block);

        engine.Publish(Build(new MixerStripSettings(GainDecibels: -120), new MixerStripSettings(), 0, preFader: true, source, @return).Graph);
        var ramp = driver.Render(Block, Block);
        var steady = driver.Render(Block, Block);

        Assert.Equal(1f / Block, ramp[0], 6);
        Assert.Equal(0.5f, ramp[2 * ((Block / 2) - 1)], 6);
        Assert.Equal(1f, ramp[2 * (Block - 1)], 6);
        Assert.All(steady, s => Assert.Equal(1f, s));
    }

    [Fact]
    public void Builder_RefusesASendThatClosesALoop_AndASendToItself()
    {
        var builder = new AudioGraphBuilder();
        var a = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var b = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.RouteStrip(b, a);
        builder.SendStrip(a, b, 0, preFader: false);

        Assert.Throws<ArgumentException>(() => builder.SendStrip(a, a, 0, preFader: false));
        Assert.Throws<InvalidOperationException>(() => builder.Build(Block, new MixerStripState()));
    }

    [Fact]
    public void Render_WithSends_AllocatesNothing()
    {
        var builder = new AudioGraphBuilder();
        var reverb = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -3));
        var delay = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        for (var i = 0; i < 8; i++)
        {
            var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -6, Pan: (i - 4) / 4.0));
            builder.ConnectToStrip(builder.AddNode(new ToneNode(200 * (i + 1), 0.1)), strip);
            builder.SendStrip(strip, reverb, -12, preFader: i % 2 == 0);
            builder.SendStrip(strip, delay, -18, preFader: false);
        }

        builder.SendStrip(delay, reverb, -6, preFader: false);
        using var engine = new AudioEngine(Rate, 128, streamFromDisk: false);
        engine.Publish(builder.Build(128, new MixerStripState()));
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        var buffer = new float[512 * 2];
        for (var i = 0; i < 50; i++)
        {
            driver.Render(buffer, 512);
        }

        Assert.Equal(0, Allocations.InSteadyState(() => driver.Render(buffer, 512), 200));
    }
}
