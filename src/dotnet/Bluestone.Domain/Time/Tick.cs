using System.Globalization;

namespace Bluestone.Domain.Time;

/// <summary>A non-negative musical position measured in ticks from the start of a sequence.</summary>
/// <remarks>Ticks are only meaningful relative to a <see cref="Ppqn"/> resolution.</remarks>
public readonly record struct Tick : IComparable<Tick>
{
    public static readonly Tick Zero;

    public Tick(long value) => Value = Guard.NonNegative(value, nameof(value));

    public long Value { get; }

    public static Tick Min(Tick left, Tick right) => left.Value <= right.Value ? left : right;

    public static Tick Max(Tick left, Tick right) => left.Value >= right.Value ? left : right;

    public int CompareTo(Tick other) => Value.CompareTo(other.Value);

    public static Tick operator +(Tick position, TickSpan span) => new(checked(position.Value + span.Value));

    /// <exception cref="ArgumentOutOfRangeException">The result would be before the sequence start.</exception>
    public static Tick operator -(Tick position, TickSpan span) => new(position.Value - span.Value);

    /// <summary>The distance from <paramref name="earlier"/> to <paramref name="later"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="later"/> precedes <paramref name="earlier"/>.</exception>
    public static TickSpan operator -(Tick later, Tick earlier) => new(later.Value - earlier.Value);

    public static bool operator <(Tick left, Tick right) => left.Value < right.Value;

    public static bool operator >(Tick left, Tick right) => left.Value > right.Value;

    public static bool operator <=(Tick left, Tick right) => left.Value <= right.Value;

    public static bool operator >=(Tick left, Tick right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
