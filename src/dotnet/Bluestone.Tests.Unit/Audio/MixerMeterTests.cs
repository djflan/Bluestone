using Bluestone.Audio;

namespace Bluestone.Tests.Unit.Audio;

public sealed class MixerMeterTests
{
    private const int SampleRate = 48_000;
    private const int BlockFrames = 480;
    private const int SegmentFrames = SampleRate / 10;

    [Fact]
    public void MixerMeter_DefaultsToPeakRms_AndKeepsItsPeakAndRmsReadings()
    {
        var (engine, strip, driver) = CreateMeterGraph(MixerMeterSettings.Default, 0.5);
        using (engine)
        {
            Assert.Equal(new MixerMeterSettings(MixerMeterMode.PeakRms, -18), strip.Meter.Settings);

            Render(driver, SampleRate * 3);

            var reading = strip.Meter.ReadLevel();
            Assert.Equal(MixerMeterMode.PeakRms, reading.Mode);
            Assert.True(reading.IsReady);
            Assert.Equal(0.5, reading.PeakRms.PeakLeft, 3);
            Assert.Equal(0.5 / Math.Sqrt(2), reading.PeakRms.RmsLeft, 3);
            Assert.Equal(20 * Math.Log10(0.5), reading.LeftValue, 3);
        }
    }

    [Fact]
    public void Vu_AtTheConfiguredReference_ReadsZeroForAOneKilohertzSine()
    {
        const int referenceDbfs = -12;
        var amplitude = PeakForRmsDbfs(referenceDbfs);
        var (engine, strip, driver) = CreateMeterGraph(new MixerMeterSettings(MixerMeterMode.Vu, referenceDbfs), amplitude);
        using (engine)
        {
            Render(driver, SampleRate * 3);

            var reading = strip.Meter.ReadLevel();
            Assert.Equal(MixerMeterMode.Vu, reading.Mode);
            Assert.InRange(reading.LeftValue, -0.1, 0.1);
            Assert.InRange(reading.RightValue, -0.1, 0.1);
        }
    }

    [Theory]
    [InlineData(MixerMeterMode.K20, -20)]
    [InlineData(MixerMeterMode.K14, -14)]
    [InlineData(MixerMeterMode.K12, -12)]
    public void KScale_AtItsReference_ReadsZero(MixerMeterMode mode, int referenceDbfs)
    {
        var amplitude = PeakForRmsDbfs(referenceDbfs);
        var (engine, strip, driver) = CreateMeterGraph(new MixerMeterSettings(mode, -18), amplitude);
        using (engine)
        {
            Render(driver, SampleRate * 3);

            var reading = strip.Meter.ReadLevel();
            Assert.Equal(mode, reading.Mode);
            Assert.InRange(reading.LeftValue, -0.1, 0.1);
            Assert.InRange(reading.RightValue, -0.1, 0.1);
        }
    }

    [Fact]
    public void LufsS_RemainsNotReadyUntilThirtyCompleteHundredMillisecondSegments()
    {
        var (engine, strip, driver) = CreateMeterGraph(new MixerMeterSettings(MixerMeterMode.LufsS, -18), 0.5);
        using (engine)
        {
            var initial = strip.Meter.ReadLevel();
            Assert.False(initial.IsReady);
            Assert.True(double.IsNaN(initial.LeftValue));

            // 29.9 segments: no partial segment counts toward the three-second window.
            Render(driver, (29 * SegmentFrames) + (9 * BlockFrames));
            var warming = strip.Meter.ReadLevel();
            Assert.False(warming.IsReady);
            Assert.True(double.IsNaN(warming.LeftValue));

            Render(driver, BlockFrames);
            var ready = strip.Meter.ReadLevel();
            Assert.True(ready.IsReady);
            Assert.True(double.IsFinite(ready.LeftValue));
        }
    }

    [Fact]
    public void LufsS_AfterAJump_ResetsAndWarmsUpAgain()
    {
        var (engine, strip, driver) = CreateMeterGraph(new MixerMeterSettings(MixerMeterMode.LufsS, -18), 0.5, startPlaying: true);
        using (engine)
        {
            Render(driver, SampleRate * 3);
            Assert.True(strip.Meter.ReadLevel().IsReady);

            engine.Transport.Locate(0);
            Render(driver, BlockFrames);
            Assert.False(strip.Meter.ReadLevel().IsReady);

            Render(driver, (29 * SegmentFrames) - BlockFrames);
            Assert.False(strip.Meter.ReadLevel().IsReady);

            Render(driver, SegmentFrames);
            Assert.True(strip.Meter.ReadLevel().IsReady);
        }
    }

    [Fact]
    public void LufsS_AfterAnInBlockLoopWrap_ResetsAndStartsWithThePostWrapTail()
    {
        var (engine, strip, driver) = CreateMeterGraph(new MixerMeterSettings(MixerMeterMode.LufsS, -18), 0.5, startPlaying: true);
        using (engine)
        {
            Render(driver, SampleRate * 4);
            Assert.True(strip.Meter.ReadLevel().IsReady);

            var loopStart = engine.Transport.Position - (SampleRate * 4);
            var wrapOffset = BlockFrames / 4;
            var loopEnd = engine.Transport.Position + wrapOffset;
            engine.Transport.SetLoop(loopStart, loopEnd);
            Render(driver, BlockFrames);

            Assert.Equal(loopStart + BlockFrames - wrapOffset, engine.Transport.Position);
            var warming = strip.Meter.ReadLevel();
            Assert.False(warming.IsReady);
            Assert.True(double.IsNaN(warming.LeftValue));

            // The wrap block contributed only its post-wrap tail. Counting the whole block would complete
            // the thirtieth 100 ms segment one block earlier, at the checkpoint below.
            Render(driver, (30 * SegmentFrames) - BlockFrames);
            Assert.False(strip.Meter.ReadLevel().IsReady);

            Render(driver, BlockFrames);
            Assert.True(strip.Meter.ReadLevel().IsReady);
        }
    }

    [Fact]
    public void Settings_AcceptTheReferenceBounds_AndRejectOutOfRangeOrUnknownValues()
    {
        var meter = new MixerMeter();
        meter.Settings = new MixerMeterSettings(MixerMeterMode.Vu, -36);
        Assert.Equal(-36, meter.Settings.VuReferenceDbfs);
        meter.Settings = new MixerMeterSettings(MixerMeterMode.Vu, 0);
        Assert.Equal(0, meter.Settings.VuReferenceDbfs);

        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Settings = new MixerMeterSettings(MixerMeterMode.Vu, -37));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Settings = new MixerMeterSettings(MixerMeterMode.Vu, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Settings = new MixerMeterSettings((MixerMeterMode)byte.MaxValue, -18));
    }

    private static (AudioEngine Engine, MixerStripState Strip, OfflineAudioDriver Driver) CreateMeterGraph(
        MixerMeterSettings settings,
        double tonePeak,
        bool startPlaying = false)
    {
        var strip = new MixerStripState();
        strip.Meter.Settings = settings;

        var builder = new AudioGraphBuilder();
        var stripIndex = builder.AddStrip(strip, new MixerStripSettings());
        builder.ConnectToStrip(builder.AddNode(new ToneNode(1000, tonePeak)), stripIndex);

        var engine = new AudioEngine(SampleRate, BlockFrames, streamFromDisk: false);
        engine.Publish(builder.Build(BlockFrames, new MixerStripState()));
        if (startPlaying)
        {
            engine.Transport.Play(0);
        }

        return (engine, strip, new OfflineAudioDriver(engine));
    }

    private static void Render(OfflineAudioDriver driver, int frames)
    {
        var output = new float[BlockFrames * 2];
        for (var rendered = 0; rendered < frames; rendered += BlockFrames)
        {
            driver.Render(output, BlockFrames);
        }
    }

    private static double PeakForRmsDbfs(int referenceDbfs) => Math.Sqrt(2) * Math.Pow(10, referenceDbfs / 20.0);
}
