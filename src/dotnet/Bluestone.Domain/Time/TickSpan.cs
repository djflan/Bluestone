using System.Globalization;

namespace Bluestone.Domain.Time;

/// <summary>A non-negative musical duration measured in ticks.</summary>
public readonly record struct TickSpan : IComparable<TickSpan>
{
    public static readonly TickSpan Zero;

    public TickSpan(long value) => Value = Guard.NonNegative(value, nameof(value));

    public long Value { get; }

    public int CompareTo(TickSpan other) => Value.CompareTo(other.Value);

    public static TickSpan operator +(TickSpan left, TickSpan right) => new(checked(left.Value + right.Value));

    /// <exception cref="ArgumentOutOfRangeException">The result would be negative.</exception>
    public static TickSpan operator -(TickSpan left, TickSpan right) => new(left.Value - right.Value);

    public static bool operator <(TickSpan left, TickSpan right) => left.Value < right.Value;

    public static bool operator >(TickSpan left, TickSpan right) => left.Value > right.Value;

    public static bool operator <=(TickSpan left, TickSpan right) => left.Value <= right.Value;

    public static bool operator >=(TickSpan left, TickSpan right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
