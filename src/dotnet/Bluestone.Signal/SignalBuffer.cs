namespace Bluestone.Signal;

/// <summary>
/// A reusable, growable list of <see cref="SignalEvent"/>s. It allocates only when it has to grow, so a
/// runner that keeps its buffers allocates nothing once they have reached the size its signal needs.
/// Not thread-safe; one owner at a time.
/// </summary>
public sealed class SignalBuffer
{
    public const int DefaultCapacity = 64;

    private SignalEvent[] _items;

    public SignalBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _items = capacity == 0 ? [] : new SignalEvent[capacity];
    }

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public ReadOnlySpan<SignalEvent> Events => _items.AsSpan(0, Count);

    public SignalEvent this[int index] => (uint)index < (uint)Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Writable view of the events, for in-place sorting by the runner.</summary>
    internal Span<SignalEvent> Writable => _items.AsSpan(0, Count);

    public void Add(in SignalEvent e)
    {
        if (Count == _items.Length)
        {
            EnsureCapacity(Count + 1);
        }

        _items[Count++] = e;
    }

    public void AddRange(ReadOnlySpan<SignalEvent> events)
    {
        EnsureCapacity(Count + events.Length);
        events.CopyTo(_items.AsSpan(Count));
        Count += events.Length;
    }

    /// <summary>Empties the buffer and keeps its storage. References are cleared so released events can be collected.</summary>
    public void Clear()
    {
        Array.Clear(_items, 0, Count);
        Count = 0;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= _items.Length)
        {
            return;
        }

        var grown = Math.Max(capacity, Math.Max(DefaultCapacity, _items.Length * 2));
        Array.Resize(ref _items, grown);
    }

    /// <summary>Exchanges the contents of two buffers without copying.</summary>
    internal static void Swap(SignalBuffer left, SignalBuffer right)
    {
        (left._items, right._items) = (right._items, left._items);
        (left.Count, right.Count) = (right.Count, left.Count);
    }
}
