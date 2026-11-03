using System.Buffers.Binary;
using System.Collections.Immutable;
using Bluestone.Engine.Protocol;

namespace Bluestone.Engine;

/// <summary>
/// MIDI on its way from the engine's real-time threads to Bluestone: what the engine plays to forwarded outputs, and
/// what its inputs receive. Writers (the playback thread, MIDI input callbacks) copy into a fixed buffer under a short
/// lock and allocate nothing; the sender thread drains it into <see cref="MidiBatch"/>es. When the buffer is full a
/// message is dropped and counted.
/// </summary>
internal sealed class OutboundRing : IDisposable
{
    // Each entry: kind (1), handle (4), timestamp (8), length (4), then the bytes. The same as its encoded size in a batch.
    private const int EntryHeader = 17;

    // A batch's frame: its type (2) and count (4), within the frame limit with room to spare.
    private const int BatchBudget = EngineLimits.MaxFrameBytes - 1024;

    private readonly Lock _gate = new();
    private readonly byte[] _buffer;
    private readonly byte[] _scratch;
    private readonly AutoResetEvent _signal = new(false);
    private long _head;
    private long _tail;
    private long _dropped;

    public OutboundRing(int capacity = 1 << 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, EntryHeader + EngineLimits.MaxMidiMessageBytes);
        _buffer = new byte[capacity];
        _scratch = new byte[capacity];
    }

    /// <summary>Signalled when something is written.</summary>
    public WaitHandle Signal => _signal;

    /// <summary>Messages dropped because the buffer was full or the message too long.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Copies one message in. Allocation-free; any thread.</summary>
    public bool TryWrite(MidiEventKind kind, int handle, long timestamp, ReadOnlySpan<byte> bytes)
    {
        var size = EntryHeader + bytes.Length;
        if (bytes.Length is 0 or > EngineLimits.MaxMidiMessageBytes)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        Span<byte> header = stackalloc byte[EntryHeader];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], handle);
        BinaryPrimitives.WriteInt64LittleEndian(header[5..], timestamp);
        BinaryPrimitives.WriteInt32LittleEndian(header[13..], bytes.Length);
        lock (_gate)
        {
            if (_head - _tail + size > _buffer.Length)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            Copy(header, _head);
            Copy(bytes, _head + EntryHeader);
            _head += size;
        }

        _signal.Set();
        return true;
    }

    public void Dispose() => _signal.Dispose();

    /// <summary>The next batch of whole entries, or null when there are none. The sender thread only.</summary>
    public MidiBatch? Drain()
    {
        int count;
        lock (_gate)
        {
            // Take whole entries up to a frame's worth.
            var available = _head - _tail;
            count = 0;
            var events = 0;
            while (count < available && events < EngineLimits.MaxBatchEvents)
            {
                var length = ReadLength(_tail + count);
                if (count + EntryHeader + length > BatchBudget)
                {
                    break;
                }

                count += EntryHeader + length;
                events++;
            }

            CopyOut(_tail, _scratch.AsSpan(0, count));
            _tail += count;
        }

        if (count == 0)
        {
            return null;
        }

        var data = _scratch.AsSpan(0, count).ToArray();
        var batch = ImmutableArray.CreateBuilder<MidiEvent>();
        for (var at = 0; at < count;)
        {
            var kind = (MidiEventKind)data[at];
            var handle = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 1));
            var timestamp = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at + 5));
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 13));
            batch.Add(new MidiEvent(kind, handle, timestamp, data.AsMemory(at + EntryHeader, length)));
            at += EntryHeader + length;
        }

        return new MidiBatch(batch.ToImmutable());
    }

    private int ReadLength(long position)
    {
        Span<byte> length = stackalloc byte[4];
        CopyOut(position + 13, length);
        return BinaryPrimitives.ReadInt32LittleEndian(length);
    }

    private void Copy(ReadOnlySpan<byte> source, long position)
    {
        var start = (int)(position % _buffer.Length);
        var first = Math.Min(source.Length, _buffer.Length - start);
        source[..first].CopyTo(_buffer.AsSpan(start));
        source[first..].CopyTo(_buffer);
    }

    private void CopyOut(long position, Span<byte> destination)
    {
        var start = (int)(position % _buffer.Length);
        var first = Math.Min(destination.Length, _buffer.Length - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }
}
