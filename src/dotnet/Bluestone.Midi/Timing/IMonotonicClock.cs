using System.Diagnostics;

namespace Bluestone.Midi.Timing;

/// <summary>
/// A steadily increasing time source, unaffected by wall-clock changes. All scheduling and MIDI
/// timestamps in Bluestone are expressed in this clock's time base.
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Elapsed time since an arbitrary, fixed origin. Never decreases.</summary>
    TimeSpan Now { get; }
}

/// <summary>The process's high-resolution monotonic clock (<see cref="Stopwatch"/>).</summary>
public sealed class SystemMonotonicClock : IMonotonicClock
{
    public static readonly SystemMonotonicClock Instance = new();

    private SystemMonotonicClock()
    {
    }

    public TimeSpan Now => Stopwatch.GetElapsedTime(0);

    /// <summary>Converts a raw <see cref="Stopwatch.GetTimestamp"/> value into this clock's time base.</summary>
    public static TimeSpan FromStopwatchTimestamp(long timestamp) => Stopwatch.GetElapsedTime(0, timestamp);
}

/// <summary>A clock that only moves when told to, for deterministic tests and offline rendering.</summary>
public sealed class VirtualClock : IMonotonicClock
{
    private long _ticks;

    public VirtualClock(TimeSpan start = default) => _ticks = start.Ticks;

    public TimeSpan Now => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative.</exception>
    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A monotonic clock cannot move backwards.");
        }

        Interlocked.Add(ref _ticks, amount.Ticks);
    }

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> is earlier than <see cref="Now"/>.</exception>
    public void AdvanceTo(TimeSpan time) => Advance(time - Now);
}
