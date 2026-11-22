using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>Feeds a junction's <see cref="LedLadderMeter"/> what Settings ▸ Display says its level shows.</summary>
internal static class LadderLevels
{
    /// <summary>
    /// <see cref="JunctionLevelStyle.RmsAndPeak"/>: lit to the average level, with the held peak as one cell above it.
    /// <see cref="JunctionLevelStyle.Peak"/>: lit to the peak, with no held cell.
    /// </summary>
    public static void Show(LedLadderMeter ladder, MeterLevels levels, JunctionLevelStyle style)
    {
        var peakOnly = style == JunctionLevelStyle.Peak;
        ladder.LeftValue = peakOnly ? levels.PeakLeft : levels.RmsLeft;
        ladder.RightValue = peakOnly ? levels.PeakRight : levels.RmsRight;
        ladder.LeftPeakHold = peakOnly ? double.NaN : levels.HoldLeft;
        ladder.RightPeakHold = peakOnly ? double.NaN : levels.HoldRight;
    }
}
