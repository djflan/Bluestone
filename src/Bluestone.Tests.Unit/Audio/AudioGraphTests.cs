using Bluestone.Audio;

namespace Bluestone.Tests.Unit.Audio;

public sealed class AudioGraphTests
{
    private const int Rate = 48_000;
    private const int Block = 64;

    [Fact]
    public void ToneToMaster_RendersExactSineSamples_OnBothChannels()
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(new ToneNode(1000, 0.5)));
        engine.Publish(builder.Build(Block, new MixerStripState()));

        // 300 frames in callbacks of 100: blocks of 64 inside each callback, so the phase must carry across both.
        var output = new OfflineAudioDriver(engine).Render(300, 100);

        for (var n = 0; n < 300; n++)
        {
            var expected = 0.5 * Math.Sin(2 * Math.PI * 1000 * n / Rate);
            Assert.Equal(expected, output[2 * n], 5);
            Assert.Equal(expected, output[(2 * n) + 1], 5);
        }
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(-1, 1, 0)]
    [InlineData(0.5, 0.70710678, 1)]
    [InlineData(-0.5, 1, 0.70710678)]
    public void PanLaw_IsABalanceWithASineTaper(double pan, double left, double right)
    {
        var (l, r) = PanLaw.Gains(pan);

        Assert.Equal(left, l, 6);
        Assert.Equal(right, r, 6);
    }

    [Fact]
    public void Strip_AppliesGainAndPan_AndMetersAfterTheFader()
    {
        var (output, strip) = RenderThroughStrip(new MixerStripSettings(GainDecibels: -6.0206, Pan: 0.5), frames: 4800);

        // -6.02 dB halves the 0.5 tone; pan 0.5 leaves the right side at unity and the left at cos(π/4).
        var peakLeft = output.Where((_, i) => i % 2 == 0).Max(Math.Abs);
        var peakRight = output.Where((_, i) => i % 2 == 1).Max(Math.Abs);
        Assert.Equal(0.25 * Math.Cos(Math.PI / 4), peakLeft, 3);
        Assert.Equal(0.25, peakRight, 3);
        var reading = strip.Meter.Read();
        Assert.Equal(0.25, reading.PeakRight, 3);
        Assert.Equal(0.25 * Math.Cos(Math.PI / 4), reading.PeakLeft, 3);
    }

    [Fact]
    public void Meter_RmsOfASteadySine_SettlesAtPeakOverRootTwo()
    {
        var (_, strip) = RenderThroughStrip(new MixerStripSettings(), frames: Rate * 3);

        var reading = strip.Meter.Read();
        Assert.Equal(0.5 / Math.Sqrt(2), reading.RmsLeft, 3);
        Assert.Equal(0.5 / Math.Sqrt(2), reading.RmsRight, 3);
    }

    [Fact]
    public void Meter_PeakFallsTwentyDecibelsPerSecond_AfterTheSoundStops()
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var strip = new MixerStripState();
        engine.Publish(StripGraph(strip, new MixerStripSettings()));
        var driver = new OfflineAudioDriver(engine);
        driver.Render(Rate / 10, Block);
        engine.Publish(StripGraph(strip, new MixerStripSettings(Muted: true)));

        // The first block ramps the mute in; after one second of silence the held peak has fallen 20 dB.
        driver.Render(Block, Block);
        var start = strip.Meter.Read();
        driver.Render(Rate, Block);

        var end = strip.Meter.Read();
        Assert.Equal(-20, MeterReading.ToDecibels(end.PeakLeft) - MeterReading.ToDecibels(start.PeakLeft), 1);
        // The mean square decays by e^(-t / 0.3 s), so the RMS falls by e^(-1 / 0.6) in a second.
        Assert.Equal(Math.Exp(-1 / 0.6), end.RmsLeft / start.RmsLeft, 2);
    }

    [Fact]
    public void Mute_SilencesTheStripExactly_AfterAOneBlockRamp()
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var strip = new MixerStripState();
        engine.Publish(StripGraph(strip, new MixerStripSettings()));
        var driver = new OfflineAudioDriver(engine);
        driver.Render(Block, Block);

        engine.Publish(StripGraph(strip, new MixerStripSettings(Muted: true)));
        var ramp = driver.Render(Block, Block);
        var after = driver.Render(Block * 4, Block);

        Assert.Contains(ramp, s => s != 0);
        Assert.All(after, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Solo_SilencesOtherStrips_ButKeepsTheBusTheSoloedStripFeeds()
    {
        var builder = new AudioGraphBuilder();
        var soloed = builder.AddStrip(new MixerStripState(), new MixerStripSettings(Soloed: true));
        var other = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var bus = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.RouteStrip(soloed, bus);
        builder.ConnectToStrip(builder.AddNode(new ToneNode(1000, 0.25)), soloed);
        builder.ConnectToStrip(builder.AddNode(new ToneNode(3000, 0.25)), other);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));

        var output = new OfflineAudioDriver(engine).Render(480, Block);

        // Only the 1 kHz tone (through the bus) is heard.
        for (var n = 0; n < 480; n++)
        {
            Assert.Equal(0.25 * Math.Sin(2 * Math.PI * 1000 * n / Rate), output[2 * n], 5);
        }
    }

    [Fact]
    public void Solo_OfABus_KeepsTheStripsThatFeedIt()
    {
        var builder = new AudioGraphBuilder();
        var source = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var bus = builder.AddStrip(new MixerStripState(), new MixerStripSettings(Soloed: true));
        var other = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.RouteStrip(source, bus);
        builder.ConnectToStrip(builder.AddNode(new ToneNode(1000, 0.25)), source);
        builder.ConnectToStrip(builder.AddNode(new ToneNode(3000, 0.25)), other);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));

        var output = new OfflineAudioDriver(engine).Render(Block, Block);

        Assert.Equal(0.25 * Math.Sin(2 * Math.PI * 1000 * 10 / Rate), output[20], 5);
    }

    [Fact]
    public void MuteOfASoloedStrip_StillSilencesIt()
    {
        var (output, _) = RenderThroughStrip(new MixerStripSettings(Muted: true, Soloed: true), frames: Block);

        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void MasterGain_ScalesTheOutput()
    {
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(new ToneNode(1000, 0.5)));
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState(), masterGainDecibels: -120));

        Assert.All(new OfflineAudioDriver(engine).Render(Block, Block), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Builder_RefusesStripsRoutedInALoop()
    {
        var builder = new AudioGraphBuilder();
        var a = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var b = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.RouteStrip(a, b);
        builder.RouteStrip(b, a);

        Assert.Throws<InvalidOperationException>(() => builder.Build(Block, new MixerStripState()));
    }

    [Fact]
    public void Builder_RefusesNodesConnectedInALoop()
    {
        var builder = new AudioGraphBuilder();
        var a = builder.AddNode(new ToneNode());
        var b = builder.AddNode(new ToneNode());
        builder.Connect(a, b);
        builder.Connect(b, a);

        Assert.Throws<InvalidOperationException>(() => builder.Build(Block, new MixerStripState()));
    }

    [Fact]
    public void Engine_RefusesAGraphBuiltForAnotherBlockSize()
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);

        Assert.Throws<ArgumentException>(() => engine.Publish(AudioGraph.Silent(128)));
    }

    [Fact]
    public void MonoDevice_GetsTheAverageOfLeftAndRight_ExtraChannelsGetSilence()
    {
        var builder = new AudioGraphBuilder();
        var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings(Pan: 1));
        builder.ConnectToStrip(builder.AddNode(new ToneNode(1000, 0.5)), strip);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));

        var mono = new OfflineAudioDriver(engine, channels: 1).Render(Block, Block);
        Assert.Equal(0.25 * Math.Sin(2 * Math.PI * 1000 * 10 / Rate), mono[10], 5);

        var quad = new OfflineAudioDriver(engine, channels: 4).Render(Block, Block);
        Assert.Equal(0f, quad[(10 * 4) + 0]);
        Assert.NotEqual(0f, quad[(10 * 4) + 1]);
        Assert.Equal(0f, quad[(10 * 4) + 2]);
        Assert.Equal(0f, quad[(10 * 4) + 3]);
    }

    [Fact]
    public void Render_ThroughStripsAndTone_AllocatesNothing()
    {
        var builder = new AudioGraphBuilder();
        var bus = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -3));
        for (var i = 0; i < 8; i++)
        {
            var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -6, Pan: (i - 4) / 4.0));
            builder.RouteStrip(strip, bus);
            builder.ConnectToStrip(builder.AddNode(new ToneNode(200 * (i + 1), 0.1)), strip);
        }

        using var engine = new AudioEngine(Rate, 128, streamFromDisk: false);
        engine.Publish(builder.Build(128, new MixerStripState()));
        engine.TestTone = new ToneNode();
        engine.Transport.SetLoop(1000, 5000);
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        var buffer = new float[512 * 2];
        for (var i = 0; i < 50; i++)
        {
            driver.Render(buffer, 512);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            driver.Render(buffer, 512);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static AudioGraph StripGraph(MixerStripState state, MixerStripSettings settings)
    {
        var builder = new AudioGraphBuilder();
        var strip = builder.AddStrip(state, settings);
        builder.ConnectToStrip(builder.AddNode(new ToneNode(1000, 0.5)), strip);
        return builder.Build(Block, new MixerStripState());
    }

    private static (float[] Output, MixerStripState Strip) RenderThroughStrip(MixerStripSettings settings, int frames)
    {
        var strip = new MixerStripState();
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(StripGraph(strip, settings));
        return (new OfflineAudioDriver(engine).Render(frames, Block), strip);
    }
}
