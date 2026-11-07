using System.Collections.Concurrent;

namespace Bluestone.Presentation;

/// <summary>
/// Collects items added on any thread and hands them to the UI thread in batches: one post for everything added
/// before the batch is taken, however many items that is. Nothing added is ever left without a post to take it.
/// </summary>
internal sealed class UiBatcher<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<IReadOnlyList<T>> _handle;
    private int _posted;

    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    /// <param name="handle">Runs on the UI thread with the items in the order they were added; never with none.</param>
    public UiBatcher(IUiDispatcher dispatcher, Action<IReadOnlyList<T>> handle)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    /// <summary>Adds an item. Any thread.</summary>
    public void Add(T item)
    {
        _items.Enqueue(item);
        if (Interlocked.Exchange(ref _posted, 1) == 0)
        {
            _dispatcher.Post(Drain);
        }
    }

    private void Drain()
    {
        // A full fence, so an item added while this drains is either taken below or posts a drain of its own.
        Interlocked.Exchange(ref _posted, 0);
        var batch = new List<T>();
        while (_items.TryDequeue(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count > 0)
        {
            _handle(batch);
        }
    }
}
