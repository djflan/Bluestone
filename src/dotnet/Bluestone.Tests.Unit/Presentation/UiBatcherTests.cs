using System.Collections.Concurrent;
using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

public sealed class UiBatcherTests
{
    [Fact]
    public void ABurstAddedBeforeTheUiThreadRuns_IsOnePost_AndOneBatchInOrder()
    {
        var dispatcher = new ManualDispatcher();
        var batches = new List<IReadOnlyList<int>>();
        var batcher = new UiBatcher<int>(dispatcher, batches.Add);

        for (var i = 0; i < 3000; i++)
        {
            batcher.Add(i);
        }

        Assert.Equal(1, dispatcher.Pending);
        dispatcher.RunAll();

        Assert.Equal(Enumerable.Range(0, 3000), Assert.Single(batches));
    }

    [Fact]
    public void ItemsAddedWhileABatchIsHandled_AreTakenByTheNextPost()
    {
        var dispatcher = new ManualDispatcher();
        var batches = new List<IReadOnlyList<int>>();
        UiBatcher<int>? batcher = null;
        batcher = new UiBatcher<int>(dispatcher, batch =>
        {
            batches.Add(batch);
            if (batch[0] == 1)
            {
                batcher!.Add(2);
                batcher.Add(3);
            }
        });

        batcher.Add(1);
        dispatcher.RunAll();

        Assert.Equal([[1], [2, 3]], batches);
        Assert.Equal(0, dispatcher.Pending);
    }

    [Fact]
    public void ItemsAddedFromManyThreadsWhileTheUiThreadDrains_AreEachHandledOnce()
    {
        var dispatcher = new ManualDispatcher();
        var handled = new List<int>();
        var batcher = new UiBatcher<int>(dispatcher, batch =>
        {
            Assert.NotEmpty(batch);
            handled.AddRange(batch);
        });

        const int Threads = 4, PerThread = 20_000;
        var producers = Enumerable.Range(0, Threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < PerThread; i++)
            {
                batcher.Add((t * PerThread) + i);
            }
        })).ToArray();

        while (!Task.WhenAll(producers).IsCompleted)
        {
            dispatcher.RunAll();
        }

        dispatcher.RunAll();
        Assert.Equal(Enumerable.Range(0, Threads * PerThread), handled.Order());
    }

    private sealed class ManualDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _posted = new();

        public int Pending => _posted.Count;

        public bool CheckAccess() => true;

        public void Post(Action action) => _posted.Enqueue(action);

        public void RunAll()
        {
            while (_posted.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
