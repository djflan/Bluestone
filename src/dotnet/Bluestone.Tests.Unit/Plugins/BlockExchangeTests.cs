using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class BlockExchangeTests : IDisposable
{
    private static readonly ExchangeOptions Options = new(maxFrames: 16, inputChannels: 2, outputChannels: 2, slotCount: 2, eventCapacity: 4, parameterChangeCapacity: 2);
    private static readonly TransportState Transport = new(4800, 120, true);

    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ABlock_RoundTripsAudioEventsChangesAndTransport()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        var input = Enumerable.Range(0, 32).Select(i => (float)i).ToArray();
        Assert.True(PluginEvent.TryCreateSystemExclusive(4, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7], out var sysEx));
        PluginEvent[] events = [PluginEvent.NoteOn(0, 3, 60, 100), sysEx];

        Assert.Equal(ExchangeStatus.Ok, host.TrySubmit(0, 16, Transport, input, events, [new ParameterChange(1, 7, 0.25)]));
        Assert.True(worker.TryBeginBlock(out var block));
        Assert.Equal(0, block.BlockIndex);
        Assert.Equal(16, block.Frames);
        Assert.Equal(Transport, block.Transport);
        Assert.Equal(input[16..], block.Input(1).ToArray());
        var received = new PluginEvent[2];
        Assert.True(block.TryReadInputEvent(0, ref received[0]));
        Assert.True(block.TryReadInputEvent(1, ref received[1]));
        Assert.Equal(events, received);
        Assert.True(block.TryReadParameterChange(0, out var change));
        Assert.Equal(new ParameterChange(1, 7, 0.25), change);
        for (var c = 0; c < 2; c++)
        {
            for (var i = 0; i < 16; i++)
            {
                block.Output(c)[i] = block.Input(c)[i] * 2;
            }
        }

        Assert.True(block.TryWriteOutputEvent(PluginEvent.ControlChange(2, 0, 7, 99)));
        worker.CompleteBlock(ref block, BlockStatus.Ok);

        var output = new float[32];
        var outputEvents = new PluginEvent[4];
        Assert.Equal(ExchangeStatus.Ok, host.TryCollect(0, TimeSpan.Zero, output, outputEvents, out var result));
        Assert.Equal(new BlockResult(0, 16, BlockStatus.Ok, 1, 0), result);
        Assert.Equal(input.Select(s => s * 2), output);
        Assert.Equal(PluginEvent.ControlChange(2, 0, 7, 99), outputEvents[0]);
    }

    [Fact]
    public void OwnershipStateMachine_FreeSubmittedProcessingDoneFree()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        var input = new float[32];
        var output = new float[32];

        Assert.Equal(ExchangeStatus.NotSubmitted, host.TryCollect(0, TimeSpan.Zero, output, [], out _));
        Assert.Equal(ExchangeStatus.Ok, host.TrySubmit(0, 16, Transport, input, [], []));
        Assert.Equal(ExchangeStatus.Busy, host.TrySubmit(2, 16, Transport, input, [], []));
        Assert.True(worker.TryBeginBlock(out var block));
        Assert.False(worker.TryBeginBlock(out _));

        // Processing belongs to the worker: the host gives up but cannot take it back.
        Assert.Equal(ExchangeStatus.NotReady, host.TryCollect(0, TimeSpan.Zero, output, [], out _));
        Assert.Equal(0, host.Withdrawn);
        Assert.Equal(ExchangeStatus.Busy, host.TrySubmit(2, 16, Transport, input, [], []));

        worker.CompleteBlock(ref block, BlockStatus.Ok);
        Assert.Equal(ExchangeStatus.Ok, host.TryCollect(0, TimeSpan.Zero, output, [], out _));
        Assert.Equal(ExchangeStatus.Ok, host.TrySubmit(2, 16, Transport, input, [], []));
    }

    [Fact]
    public void ABlockNotYetStarted_IsWithdrawnWhenTheWaitExpires()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        host.TrySubmit(0, 16, Transport, new float[32], [], []);

        Assert.Equal(ExchangeStatus.NotReady, host.TryCollect(0, TimeSpan.FromMilliseconds(1), new float[32], [], out _));

        Assert.Equal(1, host.Withdrawn);
        Assert.False(worker.TryBeginBlock(out _));
    }

    [Fact]
    public void ALateResult_IsDiscarded_NeverDeliveredForALaterBlock()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        host.TrySubmit(0, 16, Transport, Enumerable.Repeat(9f, 32).ToArray(), [], []);
        Assert.True(worker.TryBeginBlock(out var block));
        block.Output(0).Fill(9f);
        Assert.Equal(ExchangeStatus.NotReady, host.TryCollect(0, TimeSpan.Zero, new float[32], [], out _));
        worker.CompleteBlock(ref block, BlockStatus.Ok);
        var output = new float[32];

        // Block 2 uses the same slot; it was never submitted, and block 0's late audio must not appear as block 2's.
        Assert.Equal(ExchangeStatus.NotSubmitted, host.TryCollect(2, TimeSpan.Zero, output, [], out _));

        Assert.Equal(1, host.LateResultsDiscarded);
        Assert.All(output, s => Assert.Equal(0f, s));
        Assert.Equal(ExchangeStatus.Ok, host.TrySubmit(2, 16, Transport, new float[32], [], []));
    }

    [Fact]
    public void ALateResult_FoundBySubmit_IsDiscarded()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        host.TrySubmit(0, 16, Transport, new float[32], [], []);
        Assert.True(worker.TryBeginBlock(out var block));
        host.TryCollect(0, TimeSpan.Zero, new float[32], [], out _);
        worker.CompleteBlock(ref block, BlockStatus.Ok);

        Assert.Equal(ExchangeStatus.Ok, host.TrySubmit(2, 16, Transport, new float[32], [], []));

        Assert.Equal(1, host.LateResultsDiscarded);
        Assert.True(worker.TryBeginBlock(out var next));
        Assert.Equal(2, next.BlockIndex);
    }

    [Fact]
    public void DepthTwoPipelining_SubmitsBlockK_AndCollectsBlockKMinusOne()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        var output = new float[32];
        for (long k = 0; k < 10; k++)
        {
            Assert.Equal(ExchangeStatus.Ok, host.TrySubmit(k, 16, Transport, Enumerable.Repeat((float)k, 32).ToArray(), [], []));
            while (worker.TryBeginBlock(out var block))
            {
                block.Input(0).CopyTo(block.Output(0));
                block.Input(1).CopyTo(block.Output(1));
                worker.CompleteBlock(ref block, BlockStatus.Ok);
            }

            var collect = k - 1;
            if (collect >= 0)
            {
                Assert.Equal(ExchangeStatus.Ok, host.TryCollect(collect, TimeSpan.Zero, output, [], out var result));
                Assert.Equal(collect, result.BlockIndex);
                Assert.All(output, s => Assert.Equal(collect, s));
            }
        }
    }

    [Fact]
    public void MarkFaulted_MakesEveryCallReturnFaulted_AndStopsTheWorkerLoop()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        host.TrySubmit(0, 16, Transport, new float[32], [], []);

        host.MarkFaulted();
        host.MarkFaulted();

        Assert.True(host.IsFaulted);
        Assert.True(worker.IsStopped);
        Assert.Equal(ExchangeStatus.Faulted, host.TrySubmit(1, 16, Transport, new float[32], [], []));
        Assert.Equal(ExchangeStatus.Faulted, host.TryCollect(0, TimeSpan.FromSeconds(1), new float[32], [], out _));
        Assert.Equal(-1, host.WorkerHeartbeat);
    }

    [Fact]
    public void Open_WithTheWrongGenerationOrLayout_Throws()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 3);

        Assert.Throws<ProtocolException>(() => HostBlockExchange.Open(worker.Path, 2, Options));
        Assert.Throws<ProtocolException>(() => HostBlockExchange.Open(worker.Path, 3, new ExchangeOptions(16, 2, 1, 2, 4, 2)));
    }

    [Fact]
    public void Open_AFileThatIsNotAnExchange_Throws()
    {
        File.WriteAllBytes(_directory.File("junk"), new byte[4096]);

        Assert.Throws<ProtocolException>(() => HostBlockExchange.Open(_directory.File("junk"), 1, Options));
    }

    [Fact]
    public void AFileRecreatedUnderANewGeneration_MakesTheOldHandleStale()
    {
        using var first = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(first.Path, 1, Options);

        using var second = WorkerBlockExchange.Create(first.Path, Options, 2, allowExisting: true);

        Assert.Equal(ExchangeStatus.Stale, host.TrySubmit(0, 16, Transport, new float[32], [], []));
        Assert.Equal(ExchangeStatus.Faulted, host.TryCollect(0, TimeSpan.Zero, new float[32], [], out _));
        host.MarkFaulted();
        Assert.False(second.IsStopped);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndLaterCallsReturnFaulted()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        var host = HostBlockExchange.Open(worker.Path, 1, Options);

        host.Dispose();
        host.Dispose();
        host.MarkFaulted();

        Assert.Equal(ExchangeStatus.Faulted, host.TrySubmit(0, 16, Transport, new float[32], [], []));
        Assert.Equal(ExchangeStatus.Faulted, host.TryCollect(0, TimeSpan.Zero, new float[32], [], out _));
    }

    [Fact]
    public void EventsAndChangesThatDoNotFit_AreDroppedAndCounted()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        PluginEvent[] events = [.. Enumerable.Range(0, 5).Select(i => PluginEvent.NoteOn(i, 0, 60 + i, 100)), PluginEvent.NoteOn(16, 0, 1, 1)];
        ParameterChange[] changes = [new(0, 0, 0.5), new(0, 16, 0.5), new(0, 1, 2.0), new(0, 2, 0.1), new(0, 3, 0.2)];

        host.TrySubmit(0, 16, Transport, new float[32], events, changes);

        Assert.True(worker.TryBeginBlock(out var block));
        Assert.Equal(4, block.InputEventCount);
        Assert.Equal(2, block.ParameterChangeCount);
        Assert.Equal(2, host.InputEventsDropped);
        Assert.Equal(3, host.ParameterChangesDropped);
        for (var i = 0; i < 5; i++)
        {
            block.TryWriteOutputEvent(PluginEvent.NoteOff(i, 0, 60));
        }

        worker.CompleteBlock(ref block, BlockStatus.Ok);
        var outputEvents = new PluginEvent[2];
        host.TryCollect(0, TimeSpan.Zero, new float[32], outputEvents, out var result);
        Assert.Equal(2, result.OutputEventCount);
        Assert.Equal(3, result.OutputEventsDropped);
    }

    [Fact]
    public void APluginError_IsReportedWithTheBlock()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        host.TrySubmit(0, 16, Transport, new float[32], [], []);
        Assert.True(worker.TryBeginBlock(out var block));

        worker.CompleteBlock(ref block, BlockStatus.PluginError);

        Assert.Equal(ExchangeStatus.Ok, host.TryCollect(0, TimeSpan.Zero, new float[32], [], out var result));
        Assert.Equal(BlockStatus.PluginError, result.Status);
    }

    [Fact]
    public void Heartbeat_IsVisibleToTheHost()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);

        worker.Heartbeat();
        worker.Heartbeat();

        Assert.Equal(2, host.WorkerHeartbeat);
    }

    [Fact]
    public void HostSubmitAndCollect_AllocateNothing()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        using var stop = new CancellationTokenSource();
        var workerThread = new Thread(() => Echo(worker, stop.Token));
        workerThread.Start();
        var input = new float[32];
        var output = new float[32];
        var events = new[] { PluginEvent.NoteOn(1, 0, 60, 100) };
        var changes = new[] { new ParameterChange(0, 3, 0.5) };
        var outputEvents = new PluginEvent[4];
        long allocated = 0;
        try
        {
            for (var round = 0; round < 2; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (long k = round * 1000; k < (round + 1) * 1000; k++)
                {
                    host.TrySubmit(k, 16, Transport, input, events, changes);
                    host.TryCollect(k - 1, TimeSpan.FromSeconds(1), output, outputEvents, out _);
                }

                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }
        finally
        {
            stop.Cancel();
            workerThread.Join();
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TwoThreads_UnderStress_NeverDeliverAnotherBlocksAudio()
    {
        using var worker = WorkerBlockExchange.Create(_directory.File("x"), Options, 1);
        using var host = HostBlockExchange.Open(worker.Path, 1, Options);
        using var stop = new CancellationTokenSource();
        var workerThread = new Thread(() => Echo(worker, stop.Token, jitter: true));
        workerThread.Start();
        var input = new float[32];
        var output = new float[32];
        var outputEvents = new PluginEvent[4];
        var delivered = 0;
        var mismatches = 0;
        var undelivered = 0;
        var submittedOk = new bool[20_000];
        try
        {
            for (long k = 0; k < 20_000; k++)
            {
                for (var i = 0; i < input.Length; i++)
                {
                    input[i] = Pattern(k, i);
                }

                submittedOk[k] = host.TrySubmit(k, 16, Transport, input, [PluginEvent.NoteOn(0, 0, (int)(k % 128), 1)], []) == ExchangeStatus.Ok;

                // Most blocks get a tiny wait, so many are late and abandoned; every eighth gets a long one and must arrive.
                var patient = k % 8 == 0;
                var wait = patient ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(0.02);
                if (host.TryCollect(k - 1, wait, output, outputEvents, out var result) != ExchangeStatus.Ok)
                {
                    if (patient && k > 0 && submittedOk[k - 1])
                    {
                        undelivered++;
                    }

                    continue;
                }

                delivered++;
                for (var i = 0; i < output.Length; i++)
                {
                    if (output[i] != Pattern(k - 1, i))
                    {
                        mismatches++;
                    }
                }

                if (result.OutputEventCount != 1 || outputEvents[0].Data1 != (k - 1) % 128)
                {
                    mismatches++;
                }
            }
        }
        finally
        {
            stop.Cancel();
            workerThread.Join();
        }

        Assert.Equal(0, mismatches);
        Assert.Equal(0, undelivered);
        Assert.True(delivered > 0);
    }

    private static float Pattern(long block, int index) => (block % 8192) + (index / 64f);

    // A minimal worker loop: copies input to output and echoes the events.
    private static void Echo(WorkerBlockExchange worker, CancellationToken stop, bool jitter = false)
    {
        var random = new Random(42);
        var e = default(PluginEvent);
        while (!stop.IsCancellationRequested)
        {
            if (!worker.TryBeginBlock(out var block))
            {
                Thread.SpinWait(10);
                continue;
            }

            if (jitter)
            {
                Thread.SpinWait(random.Next(0, 3000));
            }

            for (var c = 0; c < 2; c++)
            {
                block.Input(c).CopyTo(block.Output(c));
            }

            for (var i = 0; i < block.InputEventCount; i++)
            {
                if (block.TryReadInputEvent(i, ref e))
                {
                    block.TryWriteOutputEvent(e);
                }
            }

            worker.CompleteBlock(ref block, BlockStatus.Ok);
        }
    }
}
