using System.Diagnostics;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Tests.Unit.Plugins;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>
/// Plugin collects in the engine's device callback share one budget (ADR 0044 step 7): with workers that never answer,
/// a callback with several depth-1 plugins waits no longer than the budget in all, at 64 and 128 frames, although each
/// plugin's own collect wait is longer than the whole block.
/// </summary>
public sealed class CollectBudgetTests : IDisposable
{
    private const int Rate = 48_000;
    private const double Share = 0.6;
    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    public void ACallbackWithSeveralPluginsThatNeverAnswer_WaitsNoLongerThanTheBudgetInAll(int frames)
    {
        var shape = new ExchangeOptions(frames, 0, 2);
        var workers = new List<WorkerBlockExchange>();
        var instances = new List<AttachedPluginInstance>();
        var builder = new AudioGraphBuilder();
        for (var i = 0; i < 3; i++)
        {
            var path = _directory.File($"plugin-{i}");
            workers.Add(WorkerBlockExchange.Create(path, shape, 1));
            var instance = new AttachedPluginInstance(shape, 1, TimeSpan.FromMilliseconds(2), bypassOnFailure: false);
            instance.Attach(new PluginDataPlane(path, 1, shape));
            instances.Add(instance);
            builder.ConnectToMaster(builder.AddNode(new PluginNode(instance, $"Plugin {i}", frames)));
        }

        try
        {
            using var engine = new AudioEngine(Rate, frames, streamFromDisk: false) { CollectBudgetShare = Share };
            engine.Publish(builder.Build(frames, new MixerStripState()));
            var buffer = new float[2 * frames];
            var budget = TimeSpan.FromSeconds(Share * frames / Rate);
            var waits = new List<TimeSpan>();
            for (var callback = 0; callback < 20; callback++)
            {
                var started = Stopwatch.GetTimestamp();
                engine.Render(buffer, frames, 2, new AudioDeviceBlock(callback * frames, started, TimeSpan.Zero, Rate, false));
                waits.Add(Stopwatch.GetElapsedTime(started));
            }

            // Unbounded, each callback would wait 3 × 2 ms; the budget is 0.8 ms at 64 frames and 1.6 ms at 128. The median
            // allows for the test thread being descheduled now and then; the budget itself is never passed by more than
            // the time to give up the last block.
            waits.Sort();
            Assert.True(waits[waits.Count / 2] <= budget + TimeSpan.FromMilliseconds(0.25), $"Median callback {waits[waits.Count / 2].TotalMilliseconds:0.000} ms; budget {budget.TotalMilliseconds:0.000} ms.");
            Assert.True(waits[0] <= budget + TimeSpan.FromMilliseconds(0.25), $"Shortest callback {waits[0].TotalMilliseconds:0.000} ms.");
            Assert.All(buffer, s => Assert.Equal(0f, s));
            Assert.All(instances, i => Assert.True(i.LiveDeadlineMisses > 0));
        }
        finally
        {
            instances.ForEach(i => i.Dispose());
            workers.ForEach(w => w.Dispose());
        }
    }

    [Fact]
    public void WithoutABudget_ACollectWaitsItsOwnCollectWait()
    {
        var shape = new ExchangeOptions(64, 0, 2);
        var path = _directory.File("plugin");
        using var worker = WorkerBlockExchange.Create(path, shape, 1);
        using var instance = new AttachedPluginInstance(shape, 1, TimeSpan.FromMilliseconds(20), bypassOnFailure: false);
        instance.Attach(new PluginDataPlane(path, 1, shape));

        var started = Stopwatch.GetTimestamp();
        instance.ProcessBlock(64, [], new float[128], [], new PluginEvent[shape.EventCapacity], new TransportState(0, 120, false));

        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(19));
    }

    [Fact]
    public void ABudget_CutsAWaitToWhatIsLeft_AndToNothingOnceItHasPassed()
    {
        try
        {
            CollectBudget.Begin(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            Assert.Equal(TimeSpan.FromMilliseconds(2), CollectBudget.Bound(TimeSpan.FromMilliseconds(2)));
            Assert.InRange(CollectBudget.Bound(TimeSpan.FromSeconds(10)), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
            CollectBudget.Begin(Stopwatch.GetTimestamp() - 1);
            Assert.Equal(TimeSpan.Zero, CollectBudget.Bound(TimeSpan.FromMilliseconds(2)));
        }
        finally
        {
            CollectBudget.End();
        }

        Assert.Equal(TimeSpan.FromMilliseconds(2), CollectBudget.Bound(TimeSpan.FromMilliseconds(2)));
    }
}
