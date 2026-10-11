using System.Diagnostics;
using Bluestone.Audio;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>
/// Plugin delay compensation in the render graph (ADR 0035), sample by sample: an impulse at song frame 0 through paths
/// of different latency arrives at every summing point at one frame.
/// </summary>
public sealed class LatencyCompensationTests
{
    private const int Rate = 48_000;
    private const int Block = 64;

    [Fact]
    public void TwoPathsWithDifferentLatencies_ArriveAlignedAtTheMaster()
    {
        var builder = new AudioGraphBuilder();
        var slow = Path(builder, latency: 100);
        var fast = Path(builder, latency: 37);
        builder.ConnectToMaster(slow);
        builder.ConnectToMaster(fast);
        var graph = builder.Build(Block, new MixerStripState());

        var left = Left(Render(graph, 512));

        Assert.Equal(100, graph.LatencyFrames);
        Assert.Equal([100], Impulses(left));
        Assert.Equal(2f, left[100]);
    }

    [Fact]
    public void PathsThroughMixerStripsAndABus_ArriveAligned()
    {
        var builder = new AudioGraphBuilder();
        var bus = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var first = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var second = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var direct = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.ConnectToStrip(Path(builder, latency: 150), first);
        builder.ConnectToStrip(Path(builder, latency: 10), second);
        builder.ConnectToStrip(Path(builder, latency: 0), direct);
        builder.RouteStrip(first, bus);
        builder.RouteStrip(second, bus);
        var graph = builder.Build(Block, new MixerStripState());

        var left = Left(Render(graph, 512));

        Assert.Equal(150, graph.LatencyFrames);
        Assert.Equal([150], Impulses(left));
        Assert.Equal(3f, left[150]);
    }

    [Fact]
    public void ASendToAReturnWithAPluginOnIt_ArrivesAlignedWithTheDryPaths()
    {
        // Dry: impulse → strip "dry" → master, and a post-fader send to the return. Wet: a 120-frame plugin into the
        // return. Another channel has an 80-frame plugin. Everything must line up at 120.
        var builder = new AudioGraphBuilder();
        var dry = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var other = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var ret = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.ConnectToStrip(Path(builder, latency: 0), dry);
        builder.ConnectToStrip(Path(builder, latency: 80), other);
        builder.ConnectToStrip(Path(builder, latency: 120), ret);
        builder.SendStrip(dry, ret, 0, preFader: false);
        var graph = builder.Build(Block, new MixerStripState());

        var left = Left(Render(graph, 512));

        Assert.Equal(120, graph.LatencyFrames);
        Assert.Equal([120], Impulses(left));
        Assert.Equal(4f, left[120]);
    }

    [Fact]
    public void AnEffectSummingAnInstrumentAndAClip_GetsThemAligned()
    {
        var builder = new AudioGraphBuilder();
        var instrument = Path(builder, latency: 64);
        var clip = builder.AddNode(new ImpulseNode());
        var effect = builder.AddNode(new LatentNode(30));
        builder.Connect(instrument, effect);
        builder.Connect(clip, effect);
        builder.ConnectToMaster(effect);
        var graph = builder.Build(Block, new MixerStripState());

        var left = Left(Render(graph, 512));

        Assert.Equal(94, graph.LatencyFrames);
        Assert.Equal([94], Impulses(left));
        Assert.Equal(2f, left[94]);
    }

    [Fact]
    public void ALatencyChange_MakesTheGraphStale_AndARebuildAlignsAtTheNewLatency()
    {
        var compensation = new LatencyCompensation();
        var master = new MixerStripState();
        var latent = new LatentNode(40);
        AudioGraph Build()
        {
            var builder = new AudioGraphBuilder();
            var impulse = builder.AddNode(new ImpulseNode(every: 1000));
            var path = builder.AddNode(latent);
            builder.Connect(impulse, path);
            builder.ConnectToMaster(path);
            builder.ConnectToMaster(builder.AddNode(new ImpulseNode(every: 1000)));
            return builder.Build(Block, master, compensation: compensation);
        }

        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(Build());
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine);
        var before = Left(driver.Render(1024, Block));
        Assert.False(engine.Graph.IsLatencyStale);

        // The plugin reports a new latency (as through a protocol notification); the graph says so and is rebuilt.
        latent.Latency = 300;
        Assert.True(engine.Graph.IsLatencyStale);
        engine.Publish(Build());
        var after = Left(driver.Render(2048, Block));

        // Around the change one impulse is lost to the new delay (as when any host's latency changes); after it, aligned.
        Assert.Equal([40], Impulses(before));
        Assert.Equal(300, engine.Graph.LatencyFrames);
        Assert.Equal(2000 + 300 - 1024, Impulses(after)[^1]);
        Assert.Equal(2f, after[2000 + 300 - 1024]);
    }

    [Fact]
    public void Rebuilding_KeepsTheAudioInsideTheDelayLines()
    {
        var compensation = new LatencyCompensation();
        var master = new MixerStripState();
        var slow = new LatentNode(200);
        var fast = new LatentNode(20);
        var impulse = new ImpulseNode();
        AudioGraph Build()
        {
            var builder = new AudioGraphBuilder();
            var source = builder.AddNode(impulse);
            var a = builder.AddNode(slow);
            var b = builder.AddNode(fast);
            builder.Connect(source, a);
            builder.Connect(source, b);
            builder.ConnectToMaster(a);
            builder.ConnectToMaster(b);
            return builder.Build(Block, master, compensation: compensation);
        }

        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(Build());
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine);
        var first = Left(driver.Render(128, Block));

        // An edit rebuilds the graph while the fast path's impulse is still inside its compensation delay.
        engine.Publish(Build());
        var rest = Left(driver.Render(256, Block));

        Assert.Empty(Impulses(first));
        Assert.Equal([200 - 128], Impulses(rest));
        Assert.Equal(2f, rest[200 - 128]);
    }

    [Fact]
    public void AMonitoredNode_IsNotDelayed_AndDoesNotHoldBackTheOthers()
    {
        var builder = new AudioGraphBuilder();
        var monitored = Path(builder, latency: 70);
        builder.Monitor(monitored);
        var clip = builder.AddNode(new ImpulseNode());
        var other = Path(builder, latency: 30);
        builder.ConnectToMaster(monitored);
        builder.ConnectToMaster(clip);
        builder.ConnectToMaster(other);
        var graph = builder.Build(Block, new MixerStripState());

        var left = Left(Render(graph, 512));

        Assert.Equal(30, graph.LatencyFrames);
        Assert.Equal([30, 70], Impulses(left));
        Assert.Equal(2f, left[30]);
        Assert.Equal(0, graph.DelayToMasterOf(graph.Nodes.OfType<LatentNode>().Single(n => n.Latency == 70)));
    }

    [Fact]
    public void AMonitoredInstrument_ThroughItsChannelABusAndAReturn_IsHeardAfterItsOwnLatency_AndTheRestStaysAligned()
    {
        // Channel T: the monitored instrument (128) and a clip; T → bus B, and a send to return R. Channel V: a clip,
        // into B. Channel U: a 480-frame plugin and a clip, to the master, and a send to R.
        var builder = new AudioGraphBuilder();
        var t = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var v = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var u = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var bus = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var ret = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var instrument = Path(builder, latency: 128);
        builder.Monitor(instrument);
        builder.ConnectToStrip(instrument, t);
        builder.ConnectToStrip(builder.AddNode(new ImpulseNode()), t);
        builder.ConnectToStrip(builder.AddNode(new ImpulseNode()), v);
        builder.ConnectToStrip(Path(builder, latency: 480), u);
        builder.ConnectToStrip(builder.AddNode(new ImpulseNode()), u);
        builder.RouteStrip(t, bus);
        builder.RouteStrip(v, bus);
        builder.SendStrip(t, ret, 0, preFader: true);
        builder.SendStrip(u, ret, 0, preFader: true);
        var graph = builder.Build(Block, new MixerStripState());

        var left = Left(Render(graph, 1024));

        Assert.Equal(480, graph.LatencyFrames);
        Assert.Equal([128, 480], Impulses(left));

        // The instrument through B and R; T's clip through B and R, V's clip, and U's plugin and clip, direct and through R.
        Assert.Equal(2f, left[128]);
        Assert.Equal(7f, left[480]);
    }

    [Theory]
    [InlineData(false, 236)]
    [InlineData(true, 50)]
    public void DelayToMasterOf_AnInstrument_IsEverythingBetweenItsOutputAndTheMaster(bool monitor, int expected)
    {
        // Instrument (64) → effect (50) → channel → master, and a slower path (300) through another channel. Unmonitored,
        // the instrument is heard 300 frames after its input: 64 its own, 50 the effect's, 186 compensation.
        var builder = new AudioGraphBuilder();
        var channel = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var other = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var instrument = Path(builder, latency: 64);
        var effect = builder.AddNode(new LatentNode(50));
        builder.Connect(instrument, effect);
        builder.ConnectToStrip(effect, channel);
        builder.ConnectToStrip(Path(builder, latency: 300), other);
        if (monitor)
        {
            builder.Monitor(instrument);
        }

        var graph = builder.Build(Block, new MixerStripState());
        var node = graph.Nodes.OfType<LatentNode>().Single(n => n.Latency == 64);

        Assert.Equal(expected, graph.DelayToMasterOf(node));
        Assert.Equal(64 + expected, Impulses(Left(Render(graph, 1024)))[0]);
    }

    [Fact]
    public void FollowingATimeline_RendersAheadByTheGraphsLatency_AndRelocatesWhenItChanges()
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var recorder = new RecordingNode();
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(recorder));
        builder.ConnectToMaster(Path(builder, latency: 480));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Follow(new SteadyTimeline(), toleranceFrames: 48);
        var driver = new OfflineAudioDriver(engine) { StartTimestamp = Stopwatch.Frequency };

        driver.Render(Block, Block);

        // Heard one second after the timeline's origin: song frame 48000, so the block renders 480 frames ahead.
        Assert.Equal(Rate + 480, recorder.Contexts[0].SongFrame);
        Assert.True(recorder.Contexts[0].Jumped);

        var rebuilt = new AudioGraphBuilder();
        rebuilt.ConnectToMaster(rebuilt.AddNode(recorder));
        rebuilt.ConnectToMaster(Path(rebuilt, latency: 10));
        engine.Publish(rebuilt.Build(Block, new MixerStripState()));
        driver.Render(Block, Block);

        Assert.Equal(Rate + Block + 10, recorder.Contexts[1].SongFrame);
        Assert.True(recorder.Contexts[1].Jumped);
    }

    [Fact]
    public void RenderingWithCompensation_AllocatesNothing()
    {
        var builder = new AudioGraphBuilder();
        var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var ret = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.ConnectToStrip(Path(builder, latency: 300), strip);
        builder.ConnectToStrip(Path(builder, latency: 5), ret);
        builder.SendStrip(strip, ret, -6, preFader: true);
        builder.ConnectToMaster(Path(builder, latency: 0));
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        var buffer = new float[2 * 512];
        for (var i = 0; i < 100; i++)
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

    // An impulse source feeding a node with the given latency; returns the latent node.
    private static int Path(AudioGraphBuilder builder, int latency)
    {
        var impulse = builder.AddNode(new ImpulseNode());
        var node = builder.AddNode(new LatentNode(latency));
        builder.Connect(impulse, node);
        return node;
    }

    private static float[] Render(AudioGraph graph, int frames)
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(graph);
        engine.Transport.Play(0);
        return new OfflineAudioDriver(engine).Render(frames, Block);
    }

    private static float[] Left(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 == 0)];

    private static int[] Impulses(float[] samples) => [.. Enumerable.Range(0, samples.Length).Where(i => samples[i] != 0)];

    /// <summary>1.0 at song frame 0 (and every <c>every</c> frames, when given), silence elsewhere.</summary>
    private sealed class ImpulseNode(int every = 0) : AudioNode
    {
        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            if (!context.Playing)
            {
                return;
            }

            for (var i = 0; i < context.Frames; i++)
            {
                var frame = context.SongFrame + i;
                if (frame == 0 || (every > 0 && frame > 0 && frame % every == 0))
                {
                    output.Left[i] = 1;
                    output.Right[i] = 1;
                }
            }
        }
    }

    /// <summary>Puts out its input exactly <see cref="Latency"/> frames late, and says so, as a plugin with latency does.</summary>
    private sealed class LatentNode(int latency) : AudioNode
    {
        private readonly float[] _ring = new float[4096];
        private long _written;

        public int Latency { get; set; } = latency;

        public override int LatencyFrames => Latency;

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            for (var i = 0; i < context.Frames; i++)
            {
                _ring[_written % _ring.Length] = input.Left[i];
                var delayed = _written - Latency;
                var value = delayed >= 0 ? _ring[delayed % _ring.Length] : 0;
                output.Left[i] = value;
                output.Right[i] = value;
                _written++;
            }
        }
    }

    private sealed class RecordingNode : AudioNode
    {
        public List<AudioRenderContext> Contexts { get; } = [];

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output) => Contexts.Add(context);
    }

    private sealed class SteadyTimeline : IAudioTimeline
    {
        public bool TryGetPosition(long timestamp, out TimelinePosition position)
        {
            position = new TimelinePosition((double)timestamp / Stopwatch.Frequency);
            return true;
        }
    }
}
