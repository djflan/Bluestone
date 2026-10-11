using System.Diagnostics;

namespace Bluestone.Audio;

/// <summary>
/// Callback timing, written by the audio thread with atomic adds and readable from any thread. Durations are
/// measured inside the managed callback (from entry to return), so they do not include the native layer's own
/// work; a deadline miss is a callback that took longer than its block lasts.
/// </summary>
public sealed class AudioEngineStatistics
{
    public const int BucketMicroseconds = 10;
    public const int BucketCount = 2000;

    private readonly long[] _histogram = new long[BucketCount + 1];
    private long _callbacks;
    private long _deadlineMisses;
    private long _discontinuities;
    private long _faults;
    private long _maxTicks;
    private long _totalTicks;

    public long Callbacks => Interlocked.Read(ref _callbacks);

    /// <summary>Callbacks that took longer than the block they rendered lasts.</summary>
    public long DeadlineMisses => Interlocked.Read(ref _deadlineMisses);

    /// <summary>Callbacks the device flagged as coming late (audio was probably dropped before them).</summary>
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    /// <summary>Exceptions caught while rendering; each such block was output as silence.</summary>
    public long Faults => Interlocked.Read(ref _faults);

    public TimeSpan MaxDuration => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _maxTicks));

    public TimeSpan MeanDuration
    {
        get
        {
            var count = Callbacks;
            return count == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _totalTicks) / count);
        }
    }

    /// <summary>The duration below which <paramref name="percent"/> of callbacks finished, to the histogram's 10 µs resolution.</summary>
    public TimeSpan Percentile(double percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        var counts = new long[_histogram.Length];
        long total = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = Interlocked.Read(ref _histogram[i]);
            total += counts[i];
        }

        if (total == 0)
        {
            return TimeSpan.Zero;
        }

        var target = (long)Math.Ceiling(total * percent / 100);
        long seen = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= target)
            {
                return i == BucketCount ? MaxDuration : TimeSpan.FromMicroseconds((i + 1) * BucketMicroseconds);
            }
        }

        return MaxDuration;
    }

    /// <summary>Clears every counter (not synchronised with a running callback; for measurements between runs).</summary>
    public void Reset()
    {
        Array.Clear(_histogram);
        Interlocked.Exchange(ref _callbacks, 0);
        Interlocked.Exchange(ref _deadlineMisses, 0);
        Interlocked.Exchange(ref _discontinuities, 0);
        Interlocked.Exchange(ref _faults, 0);
        Interlocked.Exchange(ref _maxTicks, 0);
        Interlocked.Exchange(ref _totalTicks, 0);
    }

    internal void Record(long elapsedTicks, int frames, int sampleRate, bool discontinuity)
    {
        Interlocked.Increment(ref _callbacks);
        Interlocked.Add(ref _totalTicks, elapsedTicks);
        var micros = elapsedTicks * 1_000_000 / Stopwatch.Frequency;
        Interlocked.Increment(ref _histogram[Math.Min(micros / BucketMicroseconds, BucketCount)]);
        if (elapsedTicks * sampleRate > (long)frames * Stopwatch.Frequency)
        {
            Interlocked.Increment(ref _deadlineMisses);
        }

        if (discontinuity)
        {
            Interlocked.Increment(ref _discontinuities);
        }

        var max = Interlocked.Read(ref _maxTicks);
        while (elapsedTicks > max)
        {
            var seen = Interlocked.CompareExchange(ref _maxTicks, elapsedTicks, max);
            if (seen == max)
            {
                break;
            }

            max = seen;
        }
    }

    internal void Fault() => Interlocked.Increment(ref _faults);
}
