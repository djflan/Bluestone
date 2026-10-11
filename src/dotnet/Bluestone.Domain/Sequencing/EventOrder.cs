namespace Bluestone.Domain.Sequencing;

/// <summary>Canonical ordering rules for simultaneous events.</summary>
public static class EventOrder
{
    /// <summary>
    /// Compares by position, then phase. This is not a total order: ties must be broken by the
    /// caller's stable sequence (track position, then track index).
    /// </summary>
    public static int Compare(TrackEvent left, TrackEvent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var byPosition = left.Position.CompareTo(right.Position);
        return byPosition != 0 ? byPosition : left.Phase.CompareTo(right.Phase);
    }
}
