using System.Diagnostics;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// The data plane driven by a handle other than the supervising instance's, as the engine process drives it (ADR 0044):
/// it processes the worker's blocks, sees at once when the supervisor marks the exchange after the worker dies, and plays
/// again once attached to the restarted worker's exchange.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class AttachedPluginInstanceTests
{
    private const int Frames = 128;

    [Fact]
    public async Task AnAttachedInstance_ProcessesTheWorkersBlocks_IsUnavailableAtOnceWhenTheWorkerDies_AndPlaysAgainWhenReattached()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 2, 2));
        await gain.SetParameterAsync(0, 0.25, Ct); // the reference gain maps 0.25 to ×0.5
        using var attached = new AttachedPluginInstance(gain.ExchangeShape, gain.PipelineDepth, gain.CollectWait, bypassOnFailure: false);
        attached.Attach(gain.DataPlane);
        Assert.True(attached.IsAttached);

        Assert.Equal(ProcessOutcome.Priming, Process(attached, 1f).Outcome);
        Assert.Equal(ProcessOutcome.Processed, Process(attached, 1f).Outcome);
        Assert.All(Output(attached, 1f, out var outcome), s => Assert.Equal(0.5f, s));
        Assert.Equal(ProcessOutcome.Processed, outcome);

        // The supervisor marks the dead worker's exchange; the attached handle sees the mark instead of waiting out its
        // (here five-second) collect wait for every block.
        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        var watch = Stopwatch.StartNew();
        Assert.All(Output(attached, 1f, out outcome), s => Assert.Equal(0f, s));
        Assert.Equal(ProcessOutcome.Unavailable, outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"The block took {watch.Elapsed.TotalMilliseconds:0} ms.");
        Assert.False(attached.IsAttached);

        await gain.RestartAsync(Ct);
        host.Track(gain);
        await WaitForStateAsync(gain, PluginInstanceState.Running);
        attached.Attach(gain.DataPlane);
        for (var i = 0; i < gain.PipelineDepth; i++)
        {
            Process(attached, 1f);
        }

        Assert.All(Output(attached, 1f, out outcome), s => Assert.Equal(0.5f, s));
        Assert.Equal(ProcessOutcome.Processed, outcome);
    }

    [Fact]
    public async Task AnExchangeOfAnotherShape_IsRefused()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 2, 2));
        using var attached = new AttachedPluginInstance(new ExchangeOptions(Frames * 2, 2, 2), gain.PipelineDepth, gain.CollectWait, bypassOnFailure: false);

        Assert.Throws<Bluestone.Plugins.Protocol.ProtocolException>(() => attached.Attach(gain.DataPlane));

        // A sidechain of the wrong size is refused as PluginInstance refuses it, not replaced with silence.
        Assert.Throws<ArgumentException>(() => attached.ProcessBlock(Frames, new float[2 * Frames], new float[2 * Frames], [], new PluginEvent[attached.EventCapacity], new TransportState(0, 120, false), new float[3]));
        Assert.Throws<Bluestone.Plugins.Protocol.ProtocolException>(() => attached.Attach(gain.DataPlane! with { Generation = gain.Generation + 1, Options = new ExchangeOptions(Frames * 2, 2, 2) }));
        Assert.False(attached.IsAttached);
    }

    private static BlockOutcome Process(AttachedPluginInstance instance, float level)
    {
        var output = new float[2 * Frames];
        return instance.ProcessBlock(Frames, Enumerable.Repeat(level, 2 * Frames).ToArray(), output, [], new PluginEvent[instance.EventCapacity], new TransportState(0, 120, false));
    }

    private static float[] Output(AttachedPluginInstance instance, float level, out ProcessOutcome outcome)
    {
        var output = new float[2 * Frames];
        outcome = instance.ProcessBlock(Frames, Enumerable.Repeat(level, 2 * Frames).ToArray(), output, [], new PluginEvent[instance.EventCapacity], new TransportState(0, 120, false)).Outcome;
        return output;
    }
}
