namespace Bluestone.Audio.Clips;

/// <summary>
/// A single-producer, single-consumer ring of stereo source frames addressed by absolute frame number. The audio
/// thread (reader) asks for a start frame with <see cref="Request"/> and consumes frames; the disk thread (writer)
/// fills from the requested frame onwards. Each request starts a new generation: the reader only reads data of its
/// latest generation, so after a jump it can never get frames read for an earlier position. The writer never
/// overwrites a frame the reader has not consumed. Lock-free; the reader side allocates nothing.
/// </summary>
internal sealed class SourceRing
{
    private readonly float[] _left;
    private readonly float[] _right;

    // Written by the reader.
    private long _requestFrame;
    private int _requestGeneration;
    private long _readFrame;

    // Written by the writer.
    private int _dataGeneration = -1;
    private long _startFrame;
    private long _writeFrame;
    private int _writerGeneration = -1;
    private long _writerFrame;

    public SourceRing(int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        Capacity = capacityFrames;
        _left = new float[capacityFrames];
        _right = new float[capacityFrames];
    }

    public int Capacity { get; }

    /// <summary>The earliest frame the reader still needs (reader's view).</summary>
    public long ReadFrame => Volatile.Read(ref _readFrame);

    /// <summary>Reader: the end of the frames available for the latest request, or null while the writer has not started it.</summary>
    public long? AvailableEnd => Volatile.Read(ref _dataGeneration) == _requestGeneration ? Volatile.Read(ref _writeFrame) : null;

    /// <summary>Reader: drop everything and fill from <paramref name="frame"/>.</summary>
    public void Request(long frame)
    {
        Volatile.Write(ref _readFrame, frame);
        Volatile.Write(ref _requestFrame, frame);
        Interlocked.Increment(ref _requestGeneration);
    }

    /// <summary>Reader: frames before <paramref name="frame"/> are no longer needed.</summary>
    public void Consume(long frame)
    {
        if (frame > _readFrame)
        {
            Volatile.Write(ref _readFrame, frame);
        }
    }

    /// <summary>Reader: copies frames [frame, frame + left.Length) when the latest request's data holds all of them.</summary>
    public bool TryRead(long frame, Span<float> left, Span<float> right)
    {
        var count = left.Length;
        if (Volatile.Read(ref _dataGeneration) != _requestGeneration || frame < _readFrame)
        {
            return false;
        }

        var start = Volatile.Read(ref _startFrame);
        var end = Volatile.Read(ref _writeFrame);
        if (frame < start || frame + count > end)
        {
            return false;
        }

        var index = (int)(frame % Capacity);
        var first = Math.Min(count, Capacity - index);
        _left.AsSpan(index, first).CopyTo(left);
        _right.AsSpan(index, first).CopyTo(right);
        if (first < count)
        {
            _left.AsSpan(0, count - first).CopyTo(left[first..]);
            _right.AsSpan(0, count - first).CopyTo(right[first..]);
        }

        return true;
    }

    /// <summary>
    /// Writer: where to write next, and how many frames fit before the writer would overwrite unconsumed frames.
    /// Starts a new generation first when the reader asked for one.
    /// </summary>
    public (long Frame, int Space) BeginWrite()
    {
        var generation = Volatile.Read(ref _requestGeneration);
        if (generation != _writerGeneration)
        {
            _writerGeneration = generation;
            _writerFrame = Volatile.Read(ref _requestFrame);
            Volatile.Write(ref _startFrame, _writerFrame);
            Volatile.Write(ref _writeFrame, _writerFrame);
            Volatile.Write(ref _dataGeneration, generation);
        }

        var limit = Volatile.Read(ref _readFrame) + Capacity;
        return (_writerFrame, (int)Math.Clamp(limit - _writerFrame, 0, Capacity));
    }

    /// <summary>Writer: stores frames at the position <see cref="BeginWrite"/> returned and publishes them.</summary>
    /// <returns>False when the reader asked for a new position meanwhile (the frames were not published).</returns>
    public bool EndWrite(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        var count = left.Length;
        var index = (int)(_writerFrame % Capacity);
        var first = Math.Min(count, Capacity - index);
        left[..first].CopyTo(_left.AsSpan(index));
        right[..first].CopyTo(_right.AsSpan(index));
        if (first < count)
        {
            left[first..].CopyTo(_left);
            right[first..].CopyTo(_right);
        }

        if (Volatile.Read(ref _requestGeneration) != _writerGeneration)
        {
            return false;
        }

        _writerFrame += count;
        Volatile.Write(ref _writeFrame, _writerFrame);
        return true;
    }
}
