using Bluestone.Engine.Protocol;

namespace Bluestone.Engine;

/// <summary>
/// Live input's latency in the engine (ADR 0035): from a note's input timestamp until its first frame is heard, measured
/// on the audio thread as it is played into a plugin instrument, and when input last went to one (for the live queue).
/// Lock-free.
/// </summary>
internal sealed class LiveMeter
{
    private long _count;
    private long _sumTicks;
    private long _minTicks = long.MaxValue;
    private long _maxTicks;
    private long _lastInput;

    /// <summary>When input last went to a plugin instrument, in clock ticks (0 for never).</summary>
    public long LastInput => Interlocked.Read(ref _lastInput);

    public void MarkInput(TimeSpan now) => Interlocked.Exchange(ref _lastInput, Math.Max(1, now.Ticks));

    /// <summary>A note-on measured on the audio thread.</summary>
    public void Record(TimeSpan latency)
    {
        var ticks = latency.Ticks;
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumTicks, ticks);
        long seen;
        while (ticks < (seen = Interlocked.Read(ref _minTicks)) && Interlocked.CompareExchange(ref _minTicks, ticks, seen) != seen)
        {
        }

        while (ticks > (seen = Interlocked.Read(ref _maxTicks)) && Interlocked.CompareExchange(ref _maxTicks, ticks, seen) != seen)
        {
        }
    }

    public LiveLatencySpec Read(long deadlineMisses)
    {
        var count = Interlocked.Read(ref _count);
        return count == 0
            ? new LiveLatencySpec(0, 0, 0, 0, deadlineMisses)
            : new LiveLatencySpec(count, Interlocked.Read(ref _sumTicks) / count, Interlocked.Read(ref _minTicks), Interlocked.Read(ref _maxTicks), deadlineMisses);
    }
}
