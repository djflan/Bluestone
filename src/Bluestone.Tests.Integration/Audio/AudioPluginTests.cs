using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Tests.Integration.Plugins;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Audio;

/// <summary>Plugins in real worker processes, driven by the audio engine through the offline driver.</summary>
[Collection(PluginProcessTests.Name)]
public sealed class AudioPluginTests
{
    private const int Rate = 48_000;
    private const int Block = 256;

    [Fact]
    public async Task SineInstrument_ThroughAMixerStrip_PlaysItsScheduledNote_AtItsPitch_AfterThePipelineLatency()
    {
        await using var host = new PluginTestHost();
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, Rate, Block, 0, 2));
        var node = new PluginNode(sine, "Sine", Block) { Schedule = Note(69, from: 0, to: 24_000) };
        using var engine = Engine(node, out var strip, gainDecibels: -6.0206);
        engine.Transport.Play(0);

        var output = new OfflineAudioDriver(engine).Render(Rate, Block);

        Assert.Equal(Block, node.LatencyFrames);
        Assert.All(output.AsSpan(0, 2 * Block).ToArray(), s => Assert.Equal(0f, s));
        var left = output.Where((_, i) => i % 2 == 0).ToArray();
        var crossings = 0;
        for (var i = 4800; i < 4800 + Rate / 10; i++)
        {
            crossings += left[i - 1] < 0 && left[i] >= 0 ? 1 : 0;
        }

        Assert.InRange(crossings, 43, 45);
        // The reference sine peaks at 0.25; the strip halves it.
        Assert.InRange(left[4800..20_000].Max(), 0.124f, 0.126f);
        Assert.All(left[(24_000 + Block + 1024)..], s => Assert.Equal(0f, s));
        Assert.InRange(strip.Meter.Read().PeakLeft, 0f, 0.126f);
        Assert.Equal(0, node.FailedBlocks);
    }

    [Fact]
    public async Task GainEffect_ProcessesTheAudioConnectedIntoIt()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, Rate, Block, 2, 2));
        await gain.SetParameterAsync(0, 0.25, Ct); // the reference gain maps 0.25 to ×0.5
        var builder = new AudioGraphBuilder();
        var tone = builder.AddNode(new ToneNode(1000, 0.5));
        var effect = builder.AddNode(new PluginNode(gain, "Gain", Block));
        builder.Connect(tone, effect);
        builder.ConnectToMaster(effect);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));

        var output = new OfflineAudioDriver(engine).Render(Block * 8, Block);

        // One block of pipeline latency: frame n of the output is the tone's frame n - 256, halved.
        for (var n = Block; n < Block * 8; n += 37)
        {
            Assert.Equal(0.25 * Math.Sin(2 * Math.PI * 1000 * (n - Block) / Rate), output[2 * n], 4);
        }
    }

    [Fact]
    public async Task AKilledWorker_LeavesExactSilence_NeverStaleAudio_AndTheEngineKeepsRunning()
    {
        await using var host = new PluginTestHost(o => o with { CollectWait = TimeSpan.FromMilliseconds(5) });
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, Rate, Block, 0, 2));
        var node = new PluginNode(sine, "Sine", Block) { Schedule = Note(69, from: 0, to: long.MaxValue / 2) };
        using var engine = Engine(node, out _);
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine);
        Assert.Contains(driver.Render(Block * 40, Block), s => s != 0f);

        KillProcess(sine.WorkerProcessId!.Value);
        await WaitForStateAsync(sine, PluginInstanceState.Unavailable);
        var after = driver.Render(Block * 20, Block);

        Assert.All(after, s => Assert.Equal(0f, s));
        Assert.True(node.FailedBlocks >= 20);
        Assert.Equal(0, engine.Statistics.Faults);
    }

    [Fact]
    public async Task ALocate_ReleasesTheNotesTheNodeStarted()
    {
        await using var host = new PluginTestHost();
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, Rate, Block, 0, 2));
        var node = new PluginNode(sine, "Sine", Block) { Schedule = Note(60, from: 0, to: Rate * 10) };
        using var engine = Engine(node, out _);
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine);
        Assert.Contains(driver.Render(Block * 20, Block), s => s != 0f);

        // Past the note's end: nothing should sound once the release has gone through the pipeline.
        engine.Transport.Locate(Rate * 20);
        driver.Render(Block * 4, Block);

        Assert.All(driver.Render(Block * 8, Block), s => Assert.Equal(0f, s));
    }

    [Fact]
    public async Task ReplacingTheSchedule_KeepsTheNotesItStillHolds_AndReleasesTheOthers()
    {
        await using var host = new PluginTestHost();
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, Rate, Block, 0, 2));
        ScheduledEvent[] held = [new(0, PluginEvent.NoteOn(0, 0, 60, 127)), new(Rate * 10, PluginEvent.NoteOff(0, 0, 60))];
        ScheduledEvent[] other = [new(0, PluginEvent.NoteOn(0, 0, 64, 64)), new(Rate * 10, PluginEvent.NoteOff(0, 0, 64))];
        var node = new PluginNode(sine, "Sine", Block) { Schedule = new PluginSchedule([.. held, .. other], []) };
        using var engine = Engine(node, out _);
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine);
        Assert.True(Left(driver.Render(Block * 20, Block))[(Block * 10)..].Max() > 0.3f);

        // Note 64 is gone and a later note was added; note 60 (peak 0.25) still holds at the play position.
        node.Schedule = new PluginSchedule([.. held, new(Rate * 20, PluginEvent.NoteOn(0, 0, 67, 127)), new(Rate * 21, PluginEvent.NoteOff(0, 0, 67))], []);
        var kept = Left(driver.Render(Block * 20, Block))[(Block * 4)..];

        Assert.InRange(kept.Max(), 0.24f, 0.26f);
        Assert.All(kept.Chunk(Block), block => Assert.True(block.Max() > 0.2f));

        // A schedule without note 60 at the play position releases it.
        node.Schedule = new PluginSchedule([new(Rate * 20, PluginEvent.NoteOn(0, 0, 67, 127)), new(Rate * 21, PluginEvent.NoteOff(0, 0, 67))], []);
        driver.Render(Block * 4, Block);
        Assert.All(driver.Render(Block * 8, Block), s => Assert.Equal(0f, s));
    }

    [Fact]
    public async Task Rendering_APluginInstrument_AllocatesNothing()
    {
        await using var host = new PluginTestHost();
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, Rate, Block, 0, 2));
        var notes = Enumerable.Range(0, 200).SelectMany(i => new[]
        {
            new ScheduledEvent(i * 1000L, PluginEvent.NoteOn(0, 0, 60 + (i % 12), 100)),
            new ScheduledEvent((i * 1000L) + 900, PluginEvent.NoteOff(0, 0, 60 + (i % 12))),
        });
        var node = new PluginNode(sine, "Sine", Block) { Schedule = new PluginSchedule(notes, [new ScheduledParameterChange(5000, 0, 0.5)]) };
        using var engine = Engine(node, out _);
        engine.Transport.Play(0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false);
        var buffer = new float[Block * 2];
        for (var i = 0; i < 20; i++)
        {
            driver.Render(buffer, Block);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 400; i++)
        {
            driver.Render(buffer, Block);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static float[] Left(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 == 0)];

    private static PluginSchedule Note(int note, long from, long to) =>
        new([new ScheduledEvent(from, PluginEvent.NoteOn(0, 0, note, 127)), new ScheduledEvent(to, PluginEvent.NoteOff(0, 0, note))], []);

    private static AudioEngine Engine(PluginNode node, out MixerStripState strip, double gainDecibels = 0)
    {
        var builder = new AudioGraphBuilder();
        strip = new MixerStripState();
        var index = builder.AddStrip(strip, new MixerStripSettings(GainDecibels: gainDecibels));
        builder.ConnectToStrip(builder.AddNode(node), index);
        var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(builder.Build(Block, new MixerStripState()));
        return engine;
    }
}
