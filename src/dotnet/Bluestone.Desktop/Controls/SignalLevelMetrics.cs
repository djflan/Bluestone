using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The shape of a level: one column's segment height and the gap between them, how wide each of its two columns is and the
/// gap between them, and the height the segments are fitted into. The segment count follows from those.
/// </summary>
internal readonly record struct SignalLevelMetrics(double LevelHeight, double BarWidth, double BarGap, double SegmentHeight, double SegmentGap)
{
    public double Segment => Math.Max(1, SegmentHeight);

    public double Gap => Math.Max(0, SegmentGap);

    /// <summary>How many segments fit in <see cref="LevelHeight"/>.</summary>
    public int Count => Math.Max(1, (int)Math.Floor((LevelHeight + Gap) / (Segment + Gap)));

    /// <summary>The height the segments take: the top one is the level's top edge.</summary>
    public double Height => (Count * Segment) + ((Count - 1) * Gap);

    /// <summary>Both columns, side by side.</summary>
    public double Width => (2 * BarWidth) + BarGap;

    /// <summary>The decibels one segment spans.</summary>
    public double SegmentDecibels => (MeterLevels.CeilingDecibels - MeterLevels.FloorDecibels) / Count;

    /// <summary>The top of segment <paramref name="index"/> (0 at the bottom), for a level whose bottom edge is <paramref name="bottom"/>.</summary>
    public double SegmentTop(double bottom, int index) => bottom - ((index + 1) * Segment) - (index * Gap);

    /// <summary>Whether <paramref name="decibels"/> falls on the boundary between two segments (or the level's top or bottom).</summary>
    public bool LandsOnEdge(double decibels)
    {
        var edge = MeterLevels.Fraction(decibels) * Count;
        return Math.Abs(edge - Math.Round(edge)) < 1e-9;
    }

    /// <summary>The middle of the gap at <paramref name="decibels"/>, which should be on a segment boundary.</summary>
    public double EdgeY(double bottom, double decibels)
    {
        var edge = (int)Math.Round(MeterLevels.Fraction(decibels) * Count);
        return edge <= 0 ? bottom : SegmentTop(bottom, edge - 1) - (Gap / 2);
    }
}
