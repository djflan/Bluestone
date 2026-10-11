using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>Each reference plugin, created in a real worker process, produces the output it should.</summary>
[Collection(PluginProcessTests.Name)]
public sealed class ReferencePluginProcessTests
{
    private const int Frames = 64;

    [Fact]
    public async Task Gain_InAWorker_ScalesAudio_AndAMidBlockChangeLandsOnTheExactSample()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var driver = new BlockDriver(gain, Frames);
        Array.Fill(driver.Input, 1.0f);

        Assert.Equal(ProcessOutcome.Priming, driver.Process().Outcome);
        Assert.All(driver.Output, s => Assert.Equal(0.0f, s));

        Assert.True(gain.QueueParameterChange(0, 0.25, sampleOffset: 37));
        Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
        Assert.All(driver.Output, s => Assert.Equal(1.0f, s));

        Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
        var block = driver.Channel(0);
        Assert.All(block[..37], s => Assert.Equal(1.0f, s));
        Assert.All(block[37..], s => Assert.Equal(0.5f, s));
        Assert.Equal(64, gain.ReportedLatencyFrames);
    }

    [Fact]
    public async Task Sine_InAWorker_PlaysTheNoteAtItsPitch_PolyphonicallyAndDeterministically()
    {
        await using var host = new PluginTestHost();
        var request = new PluginInstanceRequest(Sine, 48_000, 256, 0, 2);
        var first = await host.CreateRunningAsync(request);
        var second = await host.CreateRunningAsync(request);

        var a440 = Render(first, [PluginEvent.NoteOn(0, 0, 69, 127)], blocks: 40);
        var again = Render(second, [PluginEvent.NoteOn(0, 0, 69, 127)], blocks: 40);

        Assert.Equal(a440.Left, again.Left);
        Assert.Equal(a440.Left, a440.Right);
        var crossings = 0;
        for (var i = 4800; i < 9600; i++)
        {
            if (a440.Left[i - 1] < 0 && a440.Left[i] >= 0)
            {
                crossings++;
            }
        }

        Assert.InRange(crossings, 43, 45);
        Assert.InRange(a440.Left[4800..].Max(), 0.249f, 0.251f);

        var chord = Render(second, [PluginEvent.NoteOn(0, 1, 60, 127), PluginEvent.NoteOn(0, 1, 64, 127), PluginEvent.NoteOn(0, 1, 67, 127), PluginEvent.NoteOn(0, 1, 72, 127)], blocks: 20);
        Assert.True(chord.Left[2560..].Max() > 0.3f, "Four voices together must exceed one voice's peak.");

        var released = Render(second, [PluginEvent.ControlChange(0, 0, 123, 0), PluginEvent.ControlChange(0, 1, 123, 0)], blocks: 4);
        Assert.All(released.Left[512..], s => Assert.Equal(0.0f, s));
    }

    [Fact]
    public async Task Transpose_InAWorker_TransposesNotes_AndPassesControllersAndSystemExclusiveUnchanged()
    {
        await using var host = new PluginTestHost();
        var transpose = await host.CreateRunningAsync(new PluginInstanceRequest(Transpose, 48_000, Frames, 0, 0));
        await transpose.SetParameterAsync(0, (7 + 24) / 48.0, Ct);
        var driver = new BlockDriver(transpose, Frames);
        Assert.True(PluginEvent.TryCreateSystemExclusive(5, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7], out var sysEx));
        PluginEvent[] input = [PluginEvent.NoteOn(0, 2, 60, 100), PluginEvent.ControlChange(3, 2, 7, 90), sysEx, PluginEvent.NoteOff(10, 2, 60)];

        driver.Process(input);
        driver.Process();

        Assert.Equal(ProcessOutcome.Processed, driver.LastOutcome.Outcome);
        Assert.Equal(
            [PluginEvent.NoteOn(0, 2, 67, 100), PluginEvent.ControlChange(3, 2, 7, 90), sysEx, PluginEvent.NoteOff(10, 2, 67)],
            driver.OutputEvents.ToArray());
    }

    private static (float[] Left, float[] Right) Render(PluginInstance instance, PluginEvent[] firstBlockEvents, int blocks)
    {
        var driver = new BlockDriver(instance, instance.Request.MaxBlockFrames);
        var left = new List<float>();
        var right = new List<float>();
        driver.Process(firstBlockEvents);
        for (var b = 0; b < blocks; b++)
        {
            Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
            left.AddRange(driver.Channel(0));
            right.AddRange(driver.Channel(1));
        }

        return ([.. left], [.. right]);
    }
}

/// <summary>
/// Plugin process tests run one at a time, and not alongside other test classes, so heartbeat and timing checks
/// are not starved by other work and their worker processes do not starve timing-sensitive tests elsewhere.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginProcessTests
{
    public const string Name = "Plugin worker processes";
}
