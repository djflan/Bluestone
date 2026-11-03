using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>
/// An instance's pipeline depth changing while it plays (ADR 0043 decision 6, ADR 0044 step 7): at depth 1 each block
/// comes back in the same call, going down drops the block in flight, going up gives one block of silence, and an
/// instance that keeps missing its depth-1 deadline falls back to depth 2 until depth 1 is asked for again. A stand-in
/// worker thread answers each block with its block index.
/// </summary>
public sealed class PipelineDepthTests : IDisposable
{
    private const int Frames = 16;
    private static readonly ExchangeOptions Shape = new(Frames, 0, 1, slotCount: 2, eventCapacity: 4, parameterChangeCapacity: 2);
    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Depth1_DeliversEachBlockInItsOwnCall_AndChangingDepthMovesTheOutputByOneBlock()
    {
        using var worker = new StandInWorker(_directory.File("x"));
        using var instance = Attached(worker, depth: 2, TimeSpan.FromSeconds(5));
        instance.PluginLatencyFrames = 10;

        Assert.Equal((ProcessOutcome.Priming, 0f), Process(instance));
        Assert.Equal((ProcessOutcome.Processed, 0f), Process(instance));
        Assert.Equal((ProcessOutcome.Processed, 1f), Process(instance));
        Assert.Equal(10 + Frames, instance.ReportedLatencyFrames);

        // Down: block 3 is submitted and collected at once; block 2, still in flight, is never delivered.
        instance.RequestPipelineDepth(1);
        Assert.Equal(10, instance.ReportedLatencyFrames);
        Assert.Equal((ProcessOutcome.Processed, 3f), Process(instance));
        Assert.Equal((ProcessOutcome.Processed, 4f), Process(instance));

        // Up: block 4 was delivered already, so block 5's call is silent; then the pipeline lags by one block again.
        instance.RequestPipelineDepth(2);
        Assert.Equal(10 + Frames, instance.ReportedLatencyFrames);
        Assert.Equal((ProcessOutcome.Priming, 0f), Process(instance));
        Assert.Equal((ProcessOutcome.Processed, 5f), Process(instance));
        Assert.Equal(0, instance.LiveDeadlineMisses);
    }

    [Fact]
    public void ThreeMissedDepth1Deadlines_FallBackToDepth2_UntilDepth1IsAskedForAgain()
    {
        using var worker = new StandInWorker(_directory.File("x"));
        using var instance = Attached(worker, depth: 1, TimeSpan.FromMilliseconds(500));
        Assert.Equal((ProcessOutcome.Processed, 0f), Process(instance));

        worker.Pause();
        Assert.Equal((ProcessOutcome.Late, 0f), Process(instance));
        Assert.Equal((ProcessOutcome.Late, 0f), Process(instance));
        Assert.False(instance.FellBack);
        Assert.Equal((ProcessOutcome.Late, 0f), Process(instance));
        worker.Resume();

        Assert.True(instance.FellBack);
        Assert.Equal(2, instance.PipelineDepth);
        Assert.Equal(1, instance.Fallbacks);
        Assert.Equal(3, instance.LiveDeadlineMisses);
        Assert.Equal(Frames, instance.ReportedLatencyFrames);

        // Asking for depth 1 again changes nothing; asking for another depth first does.
        instance.RequestPipelineDepth(1);
        Assert.Equal(2, instance.PipelineDepth);
        instance.RequestPipelineDepth(2);
        instance.RequestPipelineDepth(1);
        Assert.False(instance.FellBack);
        Assert.Equal(1, instance.PipelineDepth);
    }

    [Fact]
    public void MissesFurtherApartThanTheWindow_DoNotFallBack()
    {
        using var worker = new StandInWorker(_directory.File("x"));
        using var instance = Attached(worker, depth: 1, TimeSpan.FromMilliseconds(500));
        for (var round = 0; round < 3; round++)
        {
            worker.Pause();
            Assert.Equal(ProcessOutcome.Late, Process(instance).Outcome);
            worker.Resume();
            for (var i = 0; i < AttachedPluginInstance.MissWindowBlocks / 2; i++)
            {
                Process(instance);
            }
        }

        Assert.False(instance.FellBack);
        Assert.Equal(1, instance.PipelineDepth);
        Assert.Equal(3, instance.LiveDeadlineMisses);
    }

    private static AttachedPluginInstance Attached(StandInWorker worker, int depth, TimeSpan wait)
    {
        var instance = new AttachedPluginInstance(Shape, depth, wait, bypassOnFailure: false);
        instance.Attach(new PluginDataPlane(worker.Path, 1, Shape));
        return instance;
    }

    private static (ProcessOutcome Outcome, float Value) Process(AttachedPluginInstance instance)
    {
        var output = new float[Frames];
        var outcome = instance.ProcessBlock(Frames, [], output, [], new PluginEvent[4], new TransportState(0, 120, false));
        Assert.All(output, s => Assert.Equal(output[0], s));
        return (outcome.Outcome, output[0]);
    }

    /// <summary>Answers every block with its index, on its own thread, unless paused.</summary>
    private sealed class StandInWorker : IDisposable
    {
        private readonly WorkerBlockExchange _exchange;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _pausedSeen = new();
        private volatile bool _paused;
        private volatile bool _running = true;

        public StandInWorker(string path)
        {
            _exchange = WorkerBlockExchange.Create(path, Shape, 1);
            Path = path;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public string Path { get; }

        /// <summary>Stops answering; returns once the worker will not start another block.</summary>
        public void Pause()
        {
            _pausedSeen.Reset();
            _paused = true;
            _pausedSeen.Wait();
        }

        public void Resume() => _paused = false;

        public void Dispose()
        {
            _running = false;
            _thread.Join();
            _exchange.Dispose();
            _pausedSeen.Dispose();
        }

        private void Run()
        {
            while (_running)
            {
                if (_paused)
                {
                    _pausedSeen.Set();
                    Thread.Yield();
                    continue;
                }

                if (_exchange.TryBeginBlock(out var block))
                {
                    block.Output(0)[..block.Frames].Fill(block.BlockIndex);
                    _exchange.CompleteBlock(ref block, BlockStatus.Ok);
                }
                else
                {
                    Thread.Yield();
                }
            }
        }
    }
}
