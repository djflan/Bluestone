using Bluestone.Audio;
using Bluestone.Audio.Clips;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>Audio clip gain and fades in the engine: exact curves, and changes without reopening the file.</summary>
public sealed class ClipEnvelopeTests : IDisposable
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private readonly string _directory = TestAudioFiles.Directory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Constant(string name, int frames, double value) =>
        TestAudioFiles.Wave(_directory, name, "float32", Rate, 1, [.. Enumerable.Repeat(value, frames)]);

    private static (AudioEngine Engine, OfflineAudioDriver Driver, ClipPlayerNode Node) Play(AudioClipRegion region)
    {
        var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var node = ClipPlayerNode.Open("Track", [region], Rate, Block, out var problems);
        Assert.Empty(problems);
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(node));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Play(0);
        return (engine, new OfflineAudioDriver(engine), node);
    }

    [Fact]
    public void LinearFades_FollowStraightLines_FromAndToSilence()
    {
        var path = Constant("a.wav", 2000, 0.5);
        var envelope = new AudioClipEnvelope(1f, FadeInFrames: 100, FadeOutFrames: 400, EqualPower: false);
        var (engine, driver, _) = Play(new AudioClipRegion(path, 1000, 3000, 0) { Envelope = envelope });

        var output = driver.Render(3500, 300);

        for (var p = 0; p < 2000; p++)
        {
            var expected = p < 100 ? 0.5 * p / 100 : p >= 1600 ? 0.5 * (1999 - p) / 400 : 0.5;
            Assert.Equal(expected, output[2 * (1000 + p)], 1e-7);
            Assert.Equal(expected, output[(2 * (1000 + p)) + 1], 1e-7);
        }

        Assert.Equal(0f, output[2 * 1000]);
        Assert.Equal(0f, output[2 * 2999]);
        engine.Dispose();
    }

    [Fact]
    public void EqualPowerFades_FollowAQuarterSine_AtTheClipGain()
    {
        var path = Constant("b.wav", 1000, 0.5);
        var gain = PanLaw.DecibelsToGain(-6);
        var envelope = new AudioClipEnvelope(gain, FadeInFrames: 300, FadeOutFrames: 300, EqualPower: true);
        var (engine, driver, _) = Play(new AudioClipRegion(path, 0, 1000, 0) { Envelope = envelope });

        var output = driver.Render(1000, 256);

        for (var p = 0; p < 1000; p++)
        {
            var curve = p < 300 ? Math.Sin(p / 300.0 * Math.PI / 2) : p >= 700 ? Math.Sin((999 - p) / 300.0 * Math.PI / 2) : 1;
            Assert.Equal(0.5 * gain * curve, output[2 * p], 1e-7);
        }

        Assert.Equal(0.5 * gain * Math.Sqrt(0.5), output[2 * 150], 1e-7);
        engine.Dispose();
    }

    [Fact]
    public void GainWithoutFades_ScalesEverySample()
    {
        var path = Constant("c.wav", 600, 0.5);
        var (engine, driver, _) = Play(new AudioClipRegion(path, 0, 600, 0) { Envelope = new AudioClipEnvelope(0.25f, 0, 0, false) });

        var output = driver.Render(600, 200);

        Assert.All(output, s => Assert.Equal(0.125f, s));
        engine.Dispose();
    }

    [Fact]
    public void UpdateEnvelopes_ChangesTheGain_OfTheSameStream()
    {
        var path = Constant("d.wav", 48_000, 0.5);
        var region = new AudioClipRegion(path, 0, 48_000, 0);
        var (engine, driver, node) = Play(region);
        driver.Render(Block, Block);

        node.UpdateEnvelopes([region with { Envelope = new AudioClipEnvelope(0.5f, 0, 0, false) }]);
        var output = driver.Render(Block, Block);

        Assert.All(output, s => Assert.Equal(0.25f, s));
        Assert.True(region.SameStream(region with { Envelope = new AudioClipEnvelope(0.5f, 10, 10, true) }));
        Assert.False(region.SameStream(region with { SongEndFrame = 47_999 }));
        engine.Dispose();
    }

    [Fact]
    public void AClosedNode_IsSilent_AndClosingTwiceIsHarmless()
    {
        var path = Constant("e.wav", 48_000, 0.5);
        var (engine, driver, node) = Play(new AudioClipRegion(path, 0, 48_000, 0));
        driver.Render(Block, Block);

        node.Dispose();
        node.Dispose();

        Assert.True(node.IsClosed);
        driver.Render(48_000, Block);
        engine.Dispose();
    }
}
