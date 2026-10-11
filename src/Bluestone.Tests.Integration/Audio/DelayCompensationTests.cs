using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Audio;

/// <summary>
/// Plugin delay compensation with real worker processes (ADR 0035): the reference Delay plugin reports its latency
/// and delays by exactly that much, and the pipeline adds one block, so alignment can be checked sample by sample.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class DelayCompensationTests
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private static readonly PluginIdentity Delay = Reference("reference.delay", "Reference Delay", PluginKind.AudioEffect);

    [Fact]
    public async Task TwoPluginPathsWithDifferentLatencies_AreImpulseAlignedAtTheMaster_AndRealignWhenALatencyChanges()
    {
        await using var host = new PluginTestHost();
        var shorter = await DelayAsync(host, 300);
        var longer = await DelayAsync(host, 1000);
        var compensation = new LatencyCompensation();
        var master = new MixerStripState();
        var a = new PluginNode(shorter, "Short", Block);
        var b = new PluginNode(longer, "Long", Block);
        AudioGraph Build()
        {
            var builder = new AudioGraphBuilder();
            foreach (var node in new[] { a, b })
            {
                var impulse = builder.AddNode(new ImpulseNode(every: Rate / 2));
                var effect = builder.AddNode(node);
                builder.Connect(impulse, effect);
                builder.ConnectToMaster(effect);
            }

            return builder.Build(Block, master, compensation: compensation);
        }

        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(Build());
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        var first = Left(driver.Render(Rate / 2, Block));

        Assert.Equal(1000 + Block, engine.Graph.LatencyFrames);
        Assert.Equal([1000 + Block], Impulses(first));
        Assert.Equal(2f, first[1000 + Block]);

        // The shorter plugin now reports 2000 frames (its worker sends a LatencyChanged notification).
        await shorter.SetParameterAsync(0, 2000.0 / 9600, Ct);
        await WaitUntilAsync(() => shorter.PluginLatencyFrames == 2000, StatusTimeout, "the new latency to be reported");
        Assert.True(engine.Graph.IsLatencyStale);
        engine.Publish(Build());
        var second = Left(driver.Render(Rate, Block));

        // From the next impulse on (song frame 48000), everything lines up at the new longest latency.
        Assert.Equal(2000 + Block, engine.Graph.LatencyFrames);
        var aligned = Rate + 2000 + Block - (Rate / 2);
        Assert.Equal(2f, second[aligned]);
        Assert.All(Impulses(second).Where(i => i > aligned - Block), i => Assert.Equal(aligned, i));
        Assert.Equal(0, a.FailedBlocks + b.FailedBlocks);
    }

    [Fact]
    public async Task ASendToAReturnWithALatentPlugin_IsAlignedWithTheDryChannel()
    {
        await using var host = new PluginTestHost();
        var onReturn = await DelayAsync(host, 500);
        var builder = new AudioGraphBuilder();
        var dry = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        var ret = builder.AddStrip(new MixerStripState(), new MixerStripSettings());
        builder.ConnectToStrip(builder.AddNode(new ImpulseNode()), dry);
        builder.SendStrip(dry, ret, 0, preFader: true);

        // The return's plugin is fed by the return's own input bus: a strip feeding a node is modelled as a node
        // input here, so the plugin takes the dry impulse again and outputs into the return.
        var wet = builder.AddNode(new PluginNode(onReturn, "Return delay", Block));
        builder.Connect(builder.AddNode(new ImpulseNode()), wet);
        builder.ConnectToStrip(wet, ret);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Play(0);

        var left = Left(new OfflineAudioDriver(engine, serviceStreams: false).Render(4096, Block));

        Assert.Equal(500 + Block, engine.Graph.LatencyFrames);
        Assert.Equal([500 + Block], Impulses(left));
        Assert.Equal(3f, left[500 + Block]);
    }

    private static async Task<PluginInstance> DelayAsync(PluginTestHost host, int latency)
    {
        var instance = await host.CreateRunningAsync(new PluginInstanceRequest(Delay, Rate, Block, 2, 2));
        await instance.SetParameterAsync(0, latency / 9600.0, Ct);
        await WaitUntilAsync(() => instance.PluginLatencyFrames == latency, StatusTimeout, "the plugin's latency");
        return instance;
    }

    private static float[] Left(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 == 0)];

    private static int[] Impulses(float[] samples) => [.. Enumerable.Range(0, samples.Length).Where(i => Math.Abs(samples[i]) > 1e-6)];

    /// <summary>1.0 at song frame 0, and every <c>every</c> frames when given.</summary>
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
                if (frame == 0 || (every > 0 && frame % every == 0))
                {
                    output.Left[i] = 1;
                    output.Right[i] = 1;
                }
            }
        }
    }
}
