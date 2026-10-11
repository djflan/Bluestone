using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>The data plane across a real process boundary: ordering under concurrency, and no allocation on the audio path.</summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginDataPlaneProcessTests
{
    private const int Frames = 128;

    [Fact]
    public async Task PatternCheckedAudio_AcrossAWorker_WhileAControlThreadIsBusy_NeverDeliversAnotherBlocksAudio()
    {
        await using var host = new PluginTestHost(o => o with { CollectWait = TimeSpan.FromSeconds(1) });
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 2, 2));
        var driver = new BlockDriver(gain, Frames);
        using var stop = new CancellationTokenSource();
        var control = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await gain.CaptureStateAsync(Ct);
                    await gain.GetParametersAsync(Ct);
                }
            },
            Ct);

        var processed = 0;
        var mismatches = 0;
        var audio = new Thread(() =>
        {
            for (var block = 0; block < 3000; block++)
            {
                for (var i = 0; i < driver.Input.Length; i++)
                {
                    driver.Input[i] = Pattern(block, i);
                }

                if (driver.Process().Outcome != ProcessOutcome.Processed)
                {
                    mismatches += driver.Output.Count(s => s != 0.0f);
                    continue;
                }

                processed++;
                for (var i = 0; i < driver.Output.Length; i++)
                {
                    if (driver.Output[i] != Pattern(block - 1, i))
                    {
                        mismatches++;
                    }
                }
            }
        });
        audio.Start();
        Assert.True(audio.Join(TimeSpan.FromSeconds(60)), "The audio thread did not finish.");
        await stop.CancelAsync();
        await control;

        Assert.Equal(0, mismatches);
        Assert.True(processed > 2900, $"Only {processed} of 3000 blocks were processed in time.");
    }

    [Fact]
    public async Task ProcessBlock_AcrossAWorker_AllocatesNothing()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 2, 2));
        var input = new float[2 * Frames];
        var output = new float[2 * Frames];
        var outputEvents = new PluginEvent[16];
        PluginEvent[] events = [PluginEvent.NoteOn(3, 0, 60, 100)];
        var transport = new TransportState(0, 120, true);
        long allocated = -1;
        var outcomes = new ProcessOutcome[200];
        var audio = new Thread(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                gain.QueueParameterChange(0, 0.5, i % Frames);
                gain.ProcessBlock(Frames, input, output, events, outputEvents, transport);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < outcomes.Length; i++)
            {
                gain.QueueParameterChange(0, 0.5, i % Frames);
                outcomes[i] = gain.ProcessBlock(Frames, input, output, events, outputEvents, transport).Outcome;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });
        audio.Start();
        Assert.True(audio.Join(TimeSpan.FromSeconds(60)));

        Assert.Equal(0, allocated);
        Assert.All(outcomes, o => Assert.Equal(ProcessOutcome.Processed, o));
    }

    private static float Pattern(int block, int index) => (block % 4096) + (index / 1024f);
}
