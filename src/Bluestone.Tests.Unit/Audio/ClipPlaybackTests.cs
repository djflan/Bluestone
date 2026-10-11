using Bluestone.Audio;
using Bluestone.Audio.Clips;

namespace Bluestone.Tests.Unit.Audio;

public sealed class ClipPlaybackTests : IDisposable
{
    private const int Block = 256;
    private readonly string _directory = TestAudioFiles.Directory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AtTheDeviceRate_AClipPlaysItsSamplesExactly_FromItsStartFrame()
    {
        var samples = TestAudioFiles.Sine(4800, 2, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "a.wav", "float32", 48_000, 2, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 1000, 1000 + 4800, 0));

        var output = driver.Render(8000, 300);

        Assert.All(output.AsSpan(0, 2000).ToArray(), s => Assert.Equal(0f, s));
        for (var n = 0; n < 4800; n++)
        {
            Assert.Equal((float)samples[2 * n], output[2 * (1000 + n)]);
            Assert.Equal((float)samples[(2 * n) + 1], output[(2 * (1000 + n)) + 1]);
        }

        Assert.All(output.AsSpan(2 * 5800).ToArray(), s => Assert.Equal(0f, s));
        engine.Dispose();
    }

    [Fact]
    public void TrimmedClip_StartsAtItsSourceOffset_AndEndsAtItsEnd()
    {
        var samples = TestAudioFiles.Sine(48_000, 1, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "b.wav", "pcm24", 48_000, 1, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 2000, 30_000 / 48_000.0));

        var output = driver.Render(3000, 256);

        Assert.Equal(samples[30_000], output[0], 6);
        Assert.Equal(samples[31_999], output[2 * 1999], 6);
        Assert.Equal(samples[31_999], output[(2 * 1999) + 1], 6); // mono plays on both sides
        Assert.Equal(0f, output[2 * 2000]);
        engine.Dispose();
    }

    [Fact]
    public void Resampling44100To48000_KeepsASineWithinMinus90DecibelsOfTheIdeal()
    {
        // A 1 kHz sine at 44.1 kHz played at 48 kHz must come out as the same 1 kHz sine.
        var source = TestAudioFiles.Sine(44_100, 1, 1000, 44_100, amplitude: 0.5);
        var path = TestAudioFiles.Wave(_directory, "c.wav", "float64", 44_100, 1, source);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 48_000, 0));

        var output = driver.Render(48_000, 512);

        // Away from the clip's edges (where the kernel sees silence before the file), compare with the ideal.
        double error = 0, signal = 0;
        for (var n = 2000; n < 46_000; n++)
        {
            var ideal = 0.5 * Math.Sin(2 * Math.PI * 1000 * n / 48_000);
            error += Math.Pow(output[2 * n] - ideal, 2);
            signal += ideal * ideal;
        }

        var decibels = 10 * Math.Log10(error / signal);
        Assert.True(decibels < -90, $"Error {decibels:0.0} dB relative to the signal.");
        engine.Dispose();
    }

    [Fact]
    public void Resampling96000To48000_RemovesATone_AboveTheOutputNyquist()
    {
        // 30 kHz cannot exist at 48 kHz; it must not alias down to 18 kHz.
        var source = TestAudioFiles.Sine(96_000, 1, 30_000, 96_000, amplitude: 0.5);
        var path = TestAudioFiles.Wave(_directory, "d.wav", "float32", 96_000, 1, source);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 48_000, 0));

        var output = driver.Render(48_000, 512);

        var rms = Math.Sqrt(output.Skip(4000).Take(80_000).Average(s => (double)s * s));
        var decibels = 20 * Math.Log10(rms / (0.5 / Math.Sqrt(2)));
        Assert.True(decibels < -80, $"Alias at {decibels:0.0} dB relative to the input tone.");
        engine.Dispose();
    }

    [Fact]
    public void Resampling96000To48000_PassesAToneInTheBand()
    {
        var source = TestAudioFiles.Sine(96_000, 1, 5000, 96_000, amplitude: 0.5);
        var path = TestAudioFiles.Wave(_directory, "e.wav", "float32", 96_000, 1, source);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 48_000, 0));

        var output = driver.Render(48_000, 512);

        var rms = Math.Sqrt(output.Skip(4000).Take(80_000).Average(s => (double)s * s));
        Assert.Equal(0.5 / Math.Sqrt(2), rms, 3);
        engine.Dispose();
    }

    [Fact]
    public void SeekingIntoTheMiddle_PlaysTheRightSourcePosition_OnceTheDiskHasDelivered()
    {
        var samples = TestAudioFiles.Sine(96_000, 2, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "f.wav", "pcm16", 48_000, 2, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 96_000, 0), preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));

        // 1.5 s is outside the preload: the first block after the seek is silent (counted), then the disk catches up.
        engine.Transport.Locate(72_000);
        var output = driver.Render(1024, 256);

        var node = (ClipPlayerNode)engine.Graph.Nodes.Single();
        Assert.True(node.UnderrunFrames > 0);
        var lastFrame = 72_000 + 1023;
        Assert.Equal(samples[2 * lastFrame], output[2 * 1023], 4);
        engine.Dispose();
    }

    [Fact]
    public void ASlowDisk_GivesSilenceForMissingFrames_NeverOtherAudio_AndThenCatchesUp()
    {
        var samples = TestAudioFiles.Sine(48_000, 1, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "g.wav", "float32", 48_000, 1, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 48_000, 0), preload: TimeSpan.FromMilliseconds(10), limitReading: 128);

        // The disk delivers 128 frames per block of 256: it falls behind after the 480-frame preload. Then it recovers.
        var slow = driver.Render(6000, 256);
        var node = (ClipPlayerNode)engine.Graph.Nodes.Single();
        node.LimitReading(int.MaxValue);
        var recovered = driver.Render(6000, 256);

        Assert.True(node.UnderrunFrames > 0);
        var output = slow.Concat(recovered).ToArray();
        for (var n = 0; n < 12_000; n++)
        {
            var got = output[2 * n];
            Assert.True(got == 0f || Math.Abs(got - samples[n]) < 1e-6, $"Frame {n} is neither silence nor the clip's own sample.");
        }

        for (var n = 8000; n < 12_000; n++)
        {
            Assert.Equal(samples[n], output[2 * n], 6);
        }
        engine.Dispose();
    }

    [Fact]
    public void ReplayingFromTheStart_AfterPlayingPastThePreload_HasNoUnderruns()
    {
        var samples = TestAudioFiles.Sine(96_000, 2, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "k.wav", "float32", 48_000, 2, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 96_000, 0), preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));
        driver.Render(24_064, Block);

        engine.Transport.Locate(0);
        var output = driver.Render(24_000, Block);

        Assert.Equal(0, ((ClipPlayerNode)engine.Graph.Nodes.Single()).UnderrunFrames);
        AssertPlays(samples, output, Enumerable.Range(0, 24_000).Select(n => (long)n));
        engine.Dispose();
    }

    [Fact]
    public void ALoopAtTheClipStart_WrapsIntoThePreloadAndBeyond_WithoutUnderruns()
    {
        var samples = TestAudioFiles.Sine(96_000, 2, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "l.wav", "float32", 48_000, 2, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 96_000, 0), preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));

        // 0.3 s loops: each pass plays the 0.1 s preload, then the ring; the loop end falls inside a block.
        engine.Transport.SetLoop(0, 14_400);
        var output = driver.Render(48_000, Block);

        Assert.Equal(0, ((ClipPlayerNode)engine.Graph.Nodes.Single()).UnderrunFrames);
        AssertPlays(samples, output, Enumerable.Range(0, 48_000).Select(n => (long)(n % 14_400)));
        engine.Dispose();
    }

    [Fact]
    public void ALoopStartingInsideALongClip_WrapsWithoutUnderruns_AndALoopMovedLaterToo()
    {
        var samples = TestAudioFiles.Sine(192_000, 2, 1000, 48_000);
        var path = TestAudioFiles.Wave(_directory, "m.wav", "float32", 48_000, 2, samples);
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 192_000, 0), preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));
        var node = (ClipPlayerNode)engine.Graph.Nodes.Single();

        // The seek lands outside every preload (silent until the disk delivers); the wraps after it must not.
        engine.Transport.SetLoop(100_000, 110_000);
        engine.Transport.Locate(95_000);
        driver.Render(2048, Block);
        var underruns = node.UnderrunFrames;
        var output = driver.Render(40_960, Block);

        Assert.Equal(underruns, node.UnderrunFrames);
        AssertPlays(samples, output, Looped(97_048, 100_000, 110_000, 40_960));

        engine.Transport.SetLoop(150_000, 160_000);
        engine.Transport.Locate(146_000);
        driver.Render(2048, Block);
        underruns = node.UnderrunFrames;
        output = driver.Render(40_960, Block);

        Assert.Equal(underruns, node.UnderrunFrames);
        AssertPlays(samples, output, Looped(148_048, 150_000, 160_000, 40_960));
        engine.Dispose();
    }

    [Fact]
    public void AResampledClip_WrappingToALoopStartInsideIt_PlaysWhatItPlaysWithoutTheLoop()
    {
        var path = TestAudioFiles.Wave(_directory, "n.wav", "pcm16", 44_100, 2, TestAudioFiles.Sine(176_400, 2, 1000, 44_100));
        var region = new AudioClipRegion(path, 0, 192_000, 0);
        var (reference, referenceDriver) = Play(48_000, region, preload: TimeSpan.FromSeconds(4));
        var straight = referenceDriver.Render(110_000, Block);
        reference.Dispose();
        var (engine, driver) = Play(48_000, region, preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));
        var node = (ClipPlayerNode)engine.Graph.Nodes.Single();
        engine.Transport.SetLoop(100_000, 110_000);
        engine.Transport.Locate(95_000);
        driver.Render(2048, Block);
        var underruns = node.UnderrunFrames;

        var output = driver.Render(40_000, Block);

        Assert.Equal(underruns, node.UnderrunFrames);
        var i = 0;
        foreach (var frame in Looped(97_048, 100_000, 110_000, 40_000))
        {
            Assert.Equal(straight[2 * frame], output[2 * i], 6);
            Assert.Equal(straight[(2 * frame) + 1], output[(2 * i) + 1], 6);
            i++;
        }

        engine.Dispose();
    }

    [Fact]
    public void AMissingFile_IsReported_AndTheOtherClipsStillPlay()
    {
        var path = TestAudioFiles.Wave(_directory, "h.wav", "float32", 48_000, 1, TestAudioFiles.Sine(1000, 1, 1000, 48_000));

        using var node = ClipPlayerNode.Open(
            "Track",
            [new AudioClipRegion(Path.Combine(_directory, "gone.wav"), 0, 1000, 0), new AudioClipRegion(path, 0, 1000, 0)],
            48_000,
            Block,
            out var problems);

        Assert.Equal("gone.wav: the file is missing", Assert.Single(problems));
        Assert.Single(node.Regions);
    }

    [Fact]
    public void WhileStopped_ClipsAreSilent()
    {
        var path = TestAudioFiles.Wave(_directory, "i.wav", "float32", 48_000, 1, TestAudioFiles.Sine(4800, 1, 1000, 48_000));
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 4800, 0));
        engine.Transport.Stop();

        Assert.All(driver.Render(1024, 256), s => Assert.Equal(0f, s));
        engine.Dispose();
    }

    [Fact]
    public void Rendering_AResampledClip_AllocatesNothing()
    {
        var path = TestAudioFiles.Wave(_directory, "j.wav", "pcm16", 44_100, 2, TestAudioFiles.Sine(88_200, 2, 1000, 44_100));
        var (engine, driver) = Play(48_000, new AudioClipRegion(path, 0, 96_000, 0));
        driver.ServiceStreamsBeforeEachBlock = false;
        var buffer = new float[256 * 2];
        engine.ServiceStreams();

        // Warm-up: first calls and tiered compilation of the render path allocate; steady state must not.
        for (var i = 0; i < 50; i++)
        {
            driver.Render(buffer, 256);
        }

        // Measured in rounds, the last one asserted: an allocation in every block fails every round, while a one-time
        // runtime allocation on a slow machine (seen on a CI runner, not reproducible locally) lands in an earlier one.
        long allocated = 0;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                driver.Render(buffer, 256);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
        engine.Dispose();
    }

    // Song frames heard from `from` on, wrapping from loopEnd to loopStart.
    private static IEnumerable<long> Looped(long from, long loopStart, long loopEnd, int count)
    {
        var frame = from;
        for (var i = 0; i < count; i++)
        {
            yield return frame;
            frame = frame + 1 == loopEnd ? loopStart : frame + 1;
        }
    }

    // Each output frame is exactly the stereo sample of the song (= source) frame listed for it.
    private static void AssertPlays(double[] samples, float[] output, IEnumerable<long> frames)
    {
        var i = 0;
        foreach (var frame in frames)
        {
            Assert.Equal((float)samples[2 * frame], output[2 * i]);
            Assert.Equal((float)samples[(2 * frame) + 1], output[(2 * i) + 1]);
            i++;
        }
    }

    private static (AudioEngine Engine, OfflineAudioDriver Driver) Play(int rate, AudioClipRegion region, TimeSpan? preload = null, int? limitReading = null, TimeSpan? ring = null)
    {
        var engine = new AudioEngine(rate, Block, streamFromDisk: false);
        var node = ClipPlayerNode.Open("Track", [region], rate, Block, out var problems, preload, ring);
        Assert.Empty(problems);
        if (limitReading is { } frames)
        {
            node.LimitReading(frames);
        }

        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(node));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Play(0);
        return (engine, new OfflineAudioDriver(engine));
    }
}
