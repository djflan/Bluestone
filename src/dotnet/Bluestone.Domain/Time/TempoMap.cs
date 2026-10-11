using System.Collections.Immutable;

namespace Bluestone.Domain.Time;

/// <summary>A tempo change taking effect at a tick.</summary>
public readonly record struct TempoChange(Tick Position, Tempo Tempo);

/// <summary>
/// An immutable map of tempo changes that converts between ticks and elapsed time from the
/// sequence start.
/// </summary>
/// <remarks>
/// Conversion uses exact integer arithmetic. Elapsed time at tick <c>t</c> is
/// <c>Σ(segmentTicks × µs/quarter) / PPQN</c>, accumulated as an exact numerator and floored once to
/// <see cref="TimeSpan"/> resolution (100 ns), so results never drift with the number of tempo changes.
/// </remarks>
public sealed class TempoMap
{
    private const int HundredNanosecondsPerMicrosecond = 10;

    // Exact elapsed time at the start of each change, in microsecond-ticks (µs × PPQN).
    private readonly Int128[] _startNumerators;

    /// <param name="ppqn">The sequence resolution.</param>
    /// <param name="changes">
    /// Tempo changes in any order. When several share a tick, the last one supplied wins.
    /// If none is at tick 0, <see cref="Tempo.Default"/> applies until the first change.
    /// </param>
    public TempoMap(Ppqn ppqn, IEnumerable<TempoChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Ppqn = ppqn.EnsureValid(nameof(ppqn));

        var byPosition = new SortedDictionary<Tick, Tempo>();
        foreach (var change in changes)
        {
            if (!change.Tempo.IsValid)
            {
                throw new ArgumentException("Tempo changes must have an initialized tempo.", nameof(changes));
            }

            byPosition[change.Position] = change.Tempo;
        }

        byPosition.TryAdd(Tick.Zero, Tempo.Default);
        Changes = [.. byPosition.Select(pair => new TempoChange(pair.Key, pair.Value))];

        _startNumerators = new Int128[Changes.Length];
        for (var i = 1; i < Changes.Length; i++)
        {
            var previous = Changes[i - 1];
            var segmentTicks = Changes[i].Position.Value - previous.Position.Value;
            _startNumerators[i] = _startNumerators[i - 1] + ((Int128)segmentTicks * previous.Tempo.MicrosecondsPerQuarterNote);
        }
    }

    /// <summary>Creates a map with a single constant tempo.</summary>
    public static TempoMap Constant(Ppqn ppqn, Tempo tempo) => new(ppqn, [new TempoChange(Tick.Zero, tempo)]);

    public Ppqn Ppqn { get; }

    /// <summary>Tempo changes in ascending tick order. The first is always at tick 0.</summary>
    public ImmutableArray<TempoChange> Changes { get; }

    public Tempo TempoAt(Tick position) => Changes[SegmentContaining(position)].Tempo;

    /// <summary>Elapsed time from the sequence start to <paramref name="position"/>, floored to 100 ns.</summary>
    /// <exception cref="OverflowException">The position is too far away to express as a <see cref="TimeSpan"/>.</exception>
    public TimeSpan TimeAt(Tick position)
    {
        var segment = SegmentContaining(position);
        var change = Changes[segment];
        var numerator = _startNumerators[segment] + ((Int128)(position.Value - change.Position.Value) * change.Tempo.MicrosecondsPerQuarterNote);
        return ToTimeSpan(numerator);
    }

    /// <summary>
    /// The last tick whose <see cref="TimeAt"/> is at or before <paramref name="elapsed"/>.
    /// For any tick <c>t</c>, <c>TickAt(TimeAt(t)) ≥ t</c>, with equality whenever a tick lasts at least 100 ns.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsed"/> is negative.</exception>
    public Tick TickAt(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must not be negative.");
        }

        var target = (Int128)elapsed.Ticks;
        var ppqn = Ppqn.TicksPerQuarterNote;

        // Last segment whose start time (floored) is at or before the target.
        int low = 0, high = Changes.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_startNumerators[mid] * HundredNanosecondsPerMicrosecond / ppqn <= target)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        // Largest k with floor((S + k·u)·10 / p) ≤ T, i.e. k·u·10 ≤ (T + 1)·p − 10·S − 1.
        var change = Changes[low];
        var headroom = ((target + 1) * ppqn) - (_startNumerators[low] * HundredNanosecondsPerMicrosecond) - 1;
        var ticksIntoSegment = headroom / ((Int128)change.Tempo.MicrosecondsPerQuarterNote * HundredNanosecondsPerMicrosecond);
        var result = change.Position.Value + ticksIntoSegment;
        return new Tick(result > long.MaxValue ? long.MaxValue : (long)result);
    }

    /// <summary>Returns a new map with <paramref name="change"/> added, replacing any change at the same tick.</summary>
    public TempoMap With(TempoChange change) => new(Ppqn, Changes.Append(change));

    private int SegmentContaining(Tick position)
    {
        int low = 0, high = Changes.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Changes[mid].Position <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private TimeSpan ToTimeSpan(Int128 microsecondTicks)
    {
        var hundredNanoseconds = microsecondTicks * HundredNanosecondsPerMicrosecond / Ppqn.TicksPerQuarterNote;
        if (hundredNanoseconds > long.MaxValue)
        {
            throw new OverflowException("The position is too far from the sequence start to express as a TimeSpan.");
        }

        return TimeSpan.FromTicks((long)hundredNanoseconds);
    }
}
