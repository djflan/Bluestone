using System.Diagnostics;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>
/// A render-ahead thread may let collects wait past their collect wait while its queue covers the device (ADR 0049): a
/// plugin that answers in 8 ms is dropped with a 2 ms collect wait, and delivered with an allowance, which never shortens
/// a wait and is ignored once it has passed or for collects bounded by a device callback.
/// </summary>
public sealed class CollectAllowanceTests : IDisposable
{
    private const int Frames = 16;
    private static readonly ExchangeOptions Shape = new(Frames, 0, 1, slotCount: 2, eventCapacity: 4, parameterChangeCapacity: 2);
    private static readonly TimeSpan CollectWait = TimeSpan.FromMilliseconds(2);
    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AnAllowance_LengthensAWait_ButNeverShortensIt_AndIsIgnoredOnceItHasPassed()
    {
        try
        {
            Assert.Equal(CollectWait, CollectBudget.Extend(CollectWait));
            CollectBudget.Allow(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            Assert.InRange(CollectBudget.Extend(CollectWait), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(5), CollectBudget.Extend(TimeSpan.FromSeconds(5)));
            CollectBudget.Allow(Stopwatch.GetTimestamp() - 1);
            Assert.Equal(CollectWait, CollectBudget.Extend(CollectWait));
        }
        finally
        {
            CollectBudget.Allow(0);
        }
    }

    [Theory]
    [InlineData(false, ProcessOutcome.Late)]
    [InlineData(true, ProcessOutcome.Processed)]
    public void ABlockThatTakesLongerThanTheCollectWait_IsDeliveredWithinAnAllowance(bool allow, ProcessOutcome expected)
    {
        var path = _directory.File("plugin");
        using var worker = WorkerBlockExchange.Create(path, Shape, 1);
        using var host = HostBlockExchange.Open(path, 1, Shape);
        var pipeline = new PluginBlockPipeline(Frames, 0, Shape.ParameterChangeCapacity, pipelineDepth: 1, CollectWait, bypassOnFailure: false);
        using var answering = new CancellationTokenSource();
        var slow = new Thread(() =>
        {
            while (!answering.IsCancellationRequested)
            {
                if (worker.TryBeginBlock(out var block))
                {
                    Thread.Sleep(8);
                    block.Output(0)[..block.Frames].Fill(1f);
                    worker.CompleteBlock(ref block, BlockStatus.Ok);
                }
                else
                {
                    Thread.Yield();
                }
            }
        })
        { IsBackground = true };
        slow.Start();
        try
        {
            if (allow)
            {
                CollectBudget.Allow(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            }

            var audio = new float[Frames];
            var outcome = pipeline.Process(host, Frames, [], audio, [], new PluginEvent[Shape.EventCapacity], new TransportState(0, 120, false));

            Assert.Equal(expected, outcome.Outcome);
            Assert.Equal(allow ? 1f : 0f, audio[0]);
        }
        finally
        {
            CollectBudget.Allow(0);
            answering.Cancel();
            slow.Join();
        }
    }

    [Fact]
    public void ACollectBoundedByTheDeviceCallback_IgnoresTheAllowance()
    {
        var path = _directory.File("plugin");
        using var worker = WorkerBlockExchange.Create(path, Shape, 1);
        using var host = HostBlockExchange.Open(path, 1, Shape);
        var pipeline = new PluginBlockPipeline(Frames, 0, Shape.ParameterChangeCapacity, pipelineDepth: 1, CollectWait, bypassOnFailure: false) { BoundedByCallback = true };
        try
        {
            CollectBudget.Allow(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
            var started = Stopwatch.GetTimestamp();
            var outcome = pipeline.Process(host, Frames, [], new float[Frames], [], new PluginEvent[Shape.EventCapacity], new TransportState(0, 120, false));

            Assert.Equal(ProcessOutcome.Late, outcome.Outcome);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            CollectBudget.Allow(0);
        }
    }
}
