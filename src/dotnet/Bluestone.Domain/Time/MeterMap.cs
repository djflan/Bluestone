using System.Collections.Immutable;

namespace Bluestone.Domain.Time;

/// <summary>A time signature change taking effect at a tick.</summary>
public readonly record struct MeterChange(Tick Position, TimeSignature Signature);

/// <summary>
/// An immutable map of time signature changes that converts between ticks and bar/beat positions.
/// </summary>
/// <remarks>
/// Each change starts a new bar. A change that lands mid-bar (common in imported files) ends the
/// previous bar early; that shortened bar still counts as one bar.
/// </remarks>
public sealed class MeterMap
{
    private readonly long[] _firstBars;
    private readonly long[] _ticksPerBar;
    private readonly long[] _ticksPerBeat;

    /// <param name="ppqn">The sequence resolution.</param>
    /// <param name="changes">
    /// Changes in any order; the last one supplied wins at a shared tick. If none is at tick 0,
    /// 4/4 applies until the first change.
    /// </param>
    /// <exception cref="ArgumentException">A beat would not be a whole number of ticks at this resolution.</exception>
    public MeterMap(Ppqn ppqn, IEnumerable<MeterChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Ppqn = ppqn.EnsureValid(nameof(ppqn));

        var byPosition = new SortedDictionary<Tick, TimeSignature>();
        foreach (var change in changes)
        {
            if (!change.Signature.IsValid)
            {
                throw new ArgumentException("Meter changes must have an initialized time signature.", nameof(changes));
            }

            byPosition[change.Position] = change.Signature;
        }

        byPosition.TryAdd(Tick.Zero, TimeSignature.CommonTime);
        Changes = [.. byPosition.Select(pair => new MeterChange(pair.Key, pair.Value))];

        _firstBars = new long[Changes.Length];
        _ticksPerBar = new long[Changes.Length];
        _ticksPerBeat = new long[Changes.Length];
        for (var i = 0; i < Changes.Length; i++)
        {
            var signature = Changes[i].Signature;
            if (!signature.TryGetTicksPerBeat(Ppqn, out var beat))
            {
                throw new ArgumentException($"A {signature} beat is not a whole number of ticks at {Ppqn} PPQN.", nameof(changes));
            }

            _ticksPerBeat[i] = beat.Value;
            _ticksPerBar[i] = beat.Value * signature.Numerator;
            if (i > 0)
            {
                var length = Changes[i].Position.Value - Changes[i - 1].Position.Value;
                _firstBars[i] = _firstBars[i - 1] + CeilingDivide(length, _ticksPerBar[i - 1]);
            }
        }
    }

    public static MeterMap Constant(Ppqn ppqn, TimeSignature signature) => new(ppqn, [new MeterChange(Tick.Zero, signature)]);

    public Ppqn Ppqn { get; }

    /// <summary>Changes in ascending tick order. The first is always at tick 0.</summary>
    public ImmutableArray<MeterChange> Changes { get; }

    public TimeSignature SignatureAt(Tick position) => Changes[SegmentContaining(position)].Signature;

    public BarBeatTick ToBarBeatTick(Tick position)
    {
        var segment = SegmentContaining(position);
        var offset = position.Value - Changes[segment].Position.Value;
        var bar = _firstBars[segment] + (offset / _ticksPerBar[segment]);
        var withinBar = offset % _ticksPerBar[segment];
        return new BarBeatTick(
            bar + 1,
            (int)(withinBar / _ticksPerBeat[segment]) + 1,
            withinBar % _ticksPerBeat[segment]);
    }

    /// <summary>
    /// Converts a bar/beat/tick position to ticks. Fails if the beat or tick does not exist in that
    /// bar, including positions past the end of a bar shortened by a mid-bar meter change.
    /// </summary>
    public bool TryGetTick(BarBeatTick position, out Tick tick)
    {
        tick = Tick.Zero;
        var barIndex = position.Bar - 1;
        var segment = Array.BinarySearch(_firstBars, barIndex);
        if (segment < 0)
        {
            segment = ~segment - 1;
        }

        // Consecutive changes can share a first bar only if the earlier segment is empty; prefer the later one.
        while (segment + 1 < _firstBars.Length && _firstBars[segment + 1] == barIndex)
        {
            segment++;
        }

        if (position.Beat > Changes[segment].Signature.Numerator || position.Tick >= _ticksPerBeat[segment])
        {
            return false;
        }

        var value = Changes[segment].Position.Value
            + ((barIndex - _firstBars[segment]) * _ticksPerBar[segment])
            + ((position.Beat - 1) * _ticksPerBeat[segment])
            + position.Tick;

        if (segment + 1 < Changes.Length && value >= Changes[segment + 1].Position.Value)
        {
            return false;
        }

        tick = new Tick(value);
        return true;
    }

    /// <summary>The tick at which the bar containing <paramref name="position"/> starts.</summary>
    public Tick BarStart(Tick position)
    {
        var segment = SegmentContaining(position);
        var start = Changes[segment].Position.Value;
        var offset = position.Value - start;
        return new Tick(start + (offset - (offset % _ticksPerBar[segment])));
    }

    /// <summary>
    /// The tick at which the bar after the one containing <paramref name="position"/> starts. A meter
    /// change that lands mid-bar starts that next bar early.
    /// </summary>
    public Tick NextBarStart(Tick position)
    {
        var segment = SegmentContaining(position);
        var next = BarStart(position).Value + _ticksPerBar[segment];
        return segment + 1 < Changes.Length && Changes[segment + 1].Position.Value < next
            ? Changes[segment + 1].Position
            : new Tick(next);
    }

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

    private static long CeilingDivide(long value, long divisor) => (value + divisor - 1) / divisor;
}
