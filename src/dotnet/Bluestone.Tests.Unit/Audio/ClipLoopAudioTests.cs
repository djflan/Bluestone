using Bluestone.Audio;
using Bluestone.Audio.Clips;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>
/// Audio clip loops in the engine: the played stream continues at the loop's start past its end, sample for sample,
/// across blocks of any size, with any number of wraps in one block, without waiting for the disk, with fades only at
/// the clip's edges, and without allocating.
/// </summary>
public sealed class ClipLoopAudioTests : IDisposable
{
    private const int Block = 256;
    private readonly string _directory = TestAudioFiles.Directory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void APickupThenALongLoop_PlaysEveryFrameExactly_WithoutUnderruns()
    {
        var samples = TestAudioFiles.Sine(48_000, 2, 997, 48_000);
        var path = TestAudioFiles.Wave(_directory, "a.wav", "float32", 48_000, 2, samples);

        // 10,000 frames of pickup, then a 7,000-frame loop: longer than the 0.1 s preload, so each pass also uses the ring.
        var region = Looping(path, 0, 60_000, 0, 10_000, 7_000, 48_000);
        var (engine, driver) = Play(48_000, region, preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));
        var output = driver.Render(60_000, Block);

        Assert.Equal(0, Node(engine).UnderrunFrames);
        AssertPlays(samples, output, Enumerable.Range(0, 60_000).Select(n => Map(n, 10_000, 7_000)));
        engine.Dispose();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1)]
    public void ALoopShorterThanABlock_WrapsManyTimesInOneBlock(int loopFrames)
    {
        var samples = TestAudioFiles.Sine(4800, 2, 440, 48_000);
        var path = TestAudioFiles.Wave(_directory, "b.wav", "float32", 48_000, 2, samples);
        var region = Looping(path, 0, 9_600, 0, 2_000, loopFrames, 48_000);
        var (engine, driver) = Play(48_000, region);

        var output = driver.Render(9_600, Block);

        Assert.Equal(0, Node(engine).UnderrunFrames);
        AssertPlays(samples, output, Enumerable.Range(0, 9_600).Select(n => Map(n, 2_000, loopFrames)));
        engine.Dispose();
    }

    [Fact]
    public void AClipStartingPastItsLoopsEnd_StartsAtTheWrappedPosition()
    {
        var samples = TestAudioFiles.Sine(48_000, 2, 997, 48_000);
        var path = TestAudioFiles.Wave(_directory, "c.wav", "float32", 48_000, 2, samples);

        // Trimmed 12,000 frames in; the loop is 2,000..5,000, so the clip starts at 2,000 + (12,000 − 5,000) mod 3,000.
        var region = Looping(path, 500, 20_000, 12_000, 2_000, 3_000, 48_000);
        var (engine, driver) = Play(48_000, region);
        var output = driver.Render(20_000, Block);

        AssertPlays(samples, output[(2 * 500)..], Enumerable.Range(0, 19_500).Select(n => Map(12_000 + n, 2_000, 3_000)));
        engine.Dispose();
    }

    [Fact]
    public void Resampling_ReadsTheLoopsStart_AcrossTheWrap_NotWhatFollowsTheLoopInTheFile()
    {
        // The reference is the played stream written out as a file of its own and played without a loop: interpolation
        // across each wrap must see exactly that stream.
        var source = TestAudioFiles.Sine(44_100, 2, 1000, 44_100);
        var path = TestAudioFiles.Wave(_directory, "d.wav", "float32", 44_100, 2, source);
        const int loopStart = 8_000, loopLength = 5_513, played = 40_000;
        var unrolled = new double[played * 2];
        for (var n = 0; n < played; n++)
        {
            var frame = Map(n, loopStart, loopLength);
            unrolled[2 * n] = source[2 * frame];
            unrolled[(2 * n) + 1] = source[(2 * frame) + 1];
        }

        var reference = TestAudioFiles.Wave(_directory, "d-unrolled.wav", "float32", 44_100, 2, unrolled);
        var (straight, straightDriver) = Play(48_000, new AudioClipRegion(reference, 0, 40_000, 0), preload: TimeSpan.FromSeconds(4));
        var expected = straightDriver.Render(40_000, Block);
        straight.Dispose();

        var (engine, driver) = Play(48_000, Looping(path, 0, 40_000, 0, loopStart, loopLength, 44_100), preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));
        var output = driver.Render(40_000, Block);

        Assert.Equal(0, Node(engine).UnderrunFrames);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], output[i], 5);
        }

        engine.Dispose();
    }

    [Fact]
    public void TheOutput_IsTheSame_WhateverTheBlockSize()
    {
        var path = TestAudioFiles.Wave(_directory, "e.wav", "pcm16", 44_100, 2, TestAudioFiles.Sine(44_100, 2, 700, 44_100));
        var region = Looping(path, 300, 30_000, 1_000, 3_000, 1_234, 44_100);
        var (a, da) = Play(48_000, region);
        var big = da.Render(30_000, 256);
        a.Dispose();
        var (b, db) = Play(48_000, region);
        var small = db.Render(30_000, 37);
        b.Dispose();

        Assert.Equal(big, small);
    }

    [Fact]
    public void Fades_StayAtTheClipsEdges_AndDoNotRepeatWithTheLoop()
    {
        var samples = Enumerable.Repeat(0.5, 2 * 10_000).ToArray();
        var path = TestAudioFiles.Wave(_directory, "f.wav", "float32", 48_000, 2, samples);
        var region = Looping(path, 0, 12_000, 0, 1_000, 500, 48_000) with { Envelope = new AudioClipEnvelope(1f, 400, 600, false) };
        var (engine, driver) = Play(48_000, region);

        var output = driver.Render(12_000, Block);

        Assert.Equal(0f, output[0]);
        Assert.Equal(0.25f, output[2 * 200], 4);
        for (var n = 400; n < 12_000 - 600; n += 7)
        {
            Assert.Equal(0.5f, output[2 * n]);
        }

        Assert.Equal(0f, output[2 * 11_999]);
        engine.Dispose();
    }

    [Fact]
    public void AnArrangerLoopWrap_IntoALoopingClip_LandsOnTheRightPass_WithoutUnderruns()
    {
        var samples = TestAudioFiles.Sine(96_000, 2, 997, 48_000);
        var path = TestAudioFiles.Wave(_directory, "g.wav", "float32", 48_000, 2, samples);
        var region = Looping(path, 0, 96_000, 0, 4_000, 9_000, 48_000);
        var (engine, driver) = Play(48_000, region, preload: TimeSpan.FromSeconds(0.1), ring: TimeSpan.FromSeconds(0.25));
        engine.Transport.SetLoop(30_000, 50_000);
        engine.Transport.Locate(26_000);
        driver.Render(2_048, Block);
        var underruns = Node(engine).UnderrunFrames;

        var where = new List<string>();
        var parts = new List<float>();
        for (var b = 0; b < 40_000 / 250; b++)
        {
            var before = Node(engine).UnderrunFrames;
            parts.AddRange(driver.Render(250, 250));
            if (Node(engine).UnderrunFrames != before)
            {
                where.Add($"block {b} song {28_048 + (b * 250)}: +{Node(engine).UnderrunFrames - before}");
            }
        }

        var output = parts.ToArray();
        Assert.True(underruns == Node(engine).UnderrunFrames, string.Join("; ", where));
        var frame = 28_048L;
        for (var i = 0; i < 40_000; i++)
        {
            var file = Map(frame, 4_000, 9_000);
            Assert.Equal((float)samples[2 * file], output[2 * i]);
            frame = frame + 1 == 50_000 ? 30_000 : frame + 1;
        }

        engine.Dispose();
    }

    [Fact]
    public void Rendering_AResampledLoopingClip_AllocatesNothing()
    {
        var path = TestAudioFiles.Wave(_directory, "h.wav", "pcm16", 44_100, 2, TestAudioFiles.Sine(88_200, 2, 1000, 44_100));
        var (engine, driver) = Play(48_000, Looping(path, 0, 96_000, 0, 1_000, 97, 44_100));
        driver.ServiceStreamsBeforeEachBlock = false;
        var buffer = new float[256 * 2];
        engine.ServiceStreams();
        for (var i = 0; i < 50; i++)
        {
            driver.Render(buffer, 256);
        }

        Assert.Equal(0, Allocations.InSteadyState(() => driver.Render(buffer, 256), 100));
        engine.Dispose();
    }

    // The file frame played at frame `n` of a clip whose loop is [start, start + length).
    private static long Map(long n, long start, long length) => n < start + length ? n : start + ((n - start - length) % length);

    private static AudioClipRegion Looping(string path, long songStart, long songEnd, long offsetFrames, long loopStart, long loopLength, int rate) =>
        new(path, songStart, songEnd, offsetFrames / (double)rate) { LoopStartSeconds = loopStart / (double)rate, LoopLengthSeconds = loopLength / (double)rate };

    private static ClipPlayerNode Node(AudioEngine engine) => (ClipPlayerNode)engine.Graph.Nodes.Single();

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

    private static (AudioEngine Engine, OfflineAudioDriver Driver) Play(int rate, AudioClipRegion region, TimeSpan? preload = null, TimeSpan? ring = null)
    {
        var engine = new AudioEngine(rate, Block, streamFromDisk: false);
        var node = ClipPlayerNode.Open("Track", [region], rate, Block, out var problems, preload, ring);
        Assert.Empty(problems);
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(node));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Play(0);
        return (engine, new OfflineAudioDriver(engine));
    }
}
