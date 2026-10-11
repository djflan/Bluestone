using System.Collections.Immutable;

namespace Bluestone.Playback;

/// <summary>
/// Dispatch timing counters, written by the playback thread without locks or allocation and read
/// from any thread. Lateness is the time between when a message was due to be handed to an endpoint
/// and when it actually was.
/// </summary>
public sealed class TimingStatistics
{
    /// <summary>Upper bounds of the lateness histogram buckets; the last bucket is unbounded.</summary>
    public static readonly ImmutableArray<TimeSpan> BucketBounds =
    [
        TimeSpan.FromMilliseconds(0.25),
        TimeSpan.FromMilliseconds(0.5),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(2),
        TimeSpan.FromMilliseconds(5),
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(50),
    ];

    private readonly long[] _histogram = new long[BucketBounds.Length + 1];
    private readonly long _lateThresholdTicks;
    private long _dispatched;
    private long _late;
    private long _skippedLateNotes;
    private long _dropped;
    private long _noteOverflow;
    private long _maxLatenessTicks;

    public TimingStatistics(TimeSpan lateThreshold) => _lateThresholdTicks = lateThreshold.Ticks;

    internal void RecordDispatch(TimeSpan lateness)
    {
        var ticks = Math.Max(0, lateness.Ticks);
        Interlocked.Increment(ref _dispatched);
        Interlocked.Increment(ref _histogram[BucketOf(ticks)]);
        if (ticks > _lateThresholdTicks)
        {
            Interlocked.Increment(ref _late);
        }

        long current;
        while (ticks > (current = Volatile.Read(ref _maxLatenessTicks))
            && Interlocked.CompareExchange(ref _maxLatenessTicks, ticks, current) != current)
        {
        }
    }

    internal void RecordSkippedLateNote() => Interlocked.Increment(ref _skippedLateNotes);

    internal void RecordDropped() => Interlocked.Increment(ref _dropped);

    internal void RecordNoteOverflow() => Interlocked.Increment(ref _noteOverflow);

    public TimingSnapshot Snapshot()
    {
        var histogram = new long[_histogram.Length];
        for (var i = 0; i < histogram.Length; i++)
        {
            histogram[i] = Volatile.Read(ref _histogram[i]);
        }

        return new TimingSnapshot(
            Volatile.Read(ref _dispatched),
            Volatile.Read(ref _late),
            Volatile.Read(ref _skippedLateNotes),
            Volatile.Read(ref _dropped),
            Volatile.Read(ref _noteOverflow),
            TimeSpan.FromTicks(Volatile.Read(ref _maxLatenessTicks)),
            [.. histogram]);
    }

    private static int BucketOf(long ticks)
    {
        for (var i = 0; i < BucketBounds.Length; i++)
        {
            if (ticks <= BucketBounds[i].Ticks)
            {
                return i;
            }
        }

        return BucketBounds.Length;
    }
}

/// <summary>A point-in-time copy of <see cref="TimingStatistics"/>.</summary>
public sealed record TimingSnapshot(
    long Dispatched,
    long Late,
    long SkippedLateNotes,
    long Dropped,
    long NoteOverflow,
    TimeSpan MaxLateness,
    ImmutableArray<long> Histogram)
{
    /// <summary>
    /// An upper bound on the given lateness percentile (0-100), resolved to histogram bucket
    /// boundaries; <see cref="MaxLateness"/> for the unbounded bucket. Zero when nothing was dispatched.
    /// </summary>
    public TimeSpan Percentile(double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);
        var total = Histogram.Sum();
        if (total == 0)
        {
            return TimeSpan.Zero;
        }

        var target = Math.Max(1, (long)Math.Ceiling(total * percentile / 100));
        long cumulative = 0;
        for (var i = 0; i < Histogram.Length; i++)
        {
            cumulative += Histogram[i];
            if (cumulative >= target)
            {
                return i < TimingStatistics.BucketBounds.Length ? TimingStatistics.BucketBounds[i] : MaxLateness;
            }
        }

        return MaxLateness;
    }
}
