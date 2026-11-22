using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>Feeds a junction's <see cref="LedLadderMeter"/> what Settings ▸ Display says its level shows.</summary>
internal static class LadderLevels
{
    /// <summary>
    /// <see cref="JunctionLevelStyle.RmsAndPeak"/>: lit to the average level, with the held peak as one cell above it.
    /// <see cref="JunctionLevelStyle.Peak"/>: lit to the peak, with no held cell.
    /// Fed through <see cref="LedLadderMeter.Show"/>, not its properties, so a meter tick allocates nothing (ADR 0043).
    /// </summary>
    public static void Show(LedLadderMeter ladder, MeterLevels levels, JunctionLevelStyle style)
    {
        var peakOnly = style == JunctionLevelStyle.Peak;
        ladder.Show(
            peakOnly ? levels.PeakLeft : levels.RmsLeft,
            peakOnly ? levels.PeakRight : levels.RmsRight,
            peakOnly ? double.NaN : levels.HoldLeft,
            peakOnly ? double.NaN : levels.HoldRight);
    }
}
