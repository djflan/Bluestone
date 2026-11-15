using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The shared vertical geometry of a mixer strip's rails: the gain bar, the dB scale, and the meter all fit the same
/// segment grid (the Signal.Large.* LED metrics) inside the same top and bottom inset, so a mark on the scale lands on a
/// segment boundary of the meter beside it. Values come from the theme tokens, with the shipped values as a fallback for
/// code that draws before the application's resources load.
/// </summary>
internal static class MixerRail
{
    /// <summary>The gain bar's displayed range: the Peak scale (−60 to +6 dB), so unity sits where 0 dBFS does in Peak + RMS.</summary>
    public static double GainFloor => Number("MixerMeter.Scale.PeakFloorDb", -60);

    public static double GainCeiling => Number("MixerMeter.Scale.PeakCeilingDb", 6);

    /// <summary>The space above and below the segments, so a scale label at either end is not clipped.</summary>
    public static double Inset => Number("Space.S", 6);

    public static double TickLength => Number("Signal.Large.TickLength", 3);

    public static SignalLevelMetrics Metrics(double height, double barWidth) => new(
        Math.Max(1, height - (2 * Inset)),
        barWidth,
        Number("Signal.Large.BarGap", 2),
        Number("Signal.Large.SegmentHeight", 2),
        Number("Signal.Large.SegmentGap", 1));

    /// <summary>The bottom edge of the segments laid out by <paramref name="metrics"/>.</summary>
    public static double Bottom(SignalLevelMetrics metrics) => Inset + metrics.Height;

    /// <summary>The meter scale's range for a display mode, from the MixerMeter.Scale.* tokens.</summary>
    public static (double Floor, double Ceiling) Range(MixerMeterMode mode) => mode switch
    {
        MixerMeterMode.Vu => (Number("MixerMeter.Scale.VuFloorDb", -30), Number("MixerMeter.Scale.VuCeilingDb", 6)),
        MixerMeterMode.K20 or MixerMeterMode.K14 or MixerMeterMode.K12 => (Number("MixerMeter.Scale.KFloorDb", -20), Number("MixerMeter.Scale.KCeilingDb", 20)),
        MixerMeterMode.LufsS => (Number("MixerMeter.Scale.LufsFloorDb", -60), Number("MixerMeter.Scale.LufsCeilingDb", -5)),
        _ => (GainFloor, GainCeiling),
    };

    /// <summary>The marks the scale labels for a mode, top to bottom, in the mode's own unit.</summary>
    public static double[] Marks(MixerMeterMode mode) => mode switch
    {
        MixerMeterMode.Vu => VuMarks,
        MixerMeterMode.K20 or MixerMeterMode.K14 or MixerMeterMode.K12 => KMarks,
        MixerMeterMode.LufsS => LufsMarks,
        _ => PeakMarks,
    };

    /// <summary>Where a K-system scale puts 0 dBFS, so a held dBFS peak can be drawn on it.</summary>
    public static double KOffset(MixerMeterMode mode) => mode switch
    {
        MixerMeterMode.K20 => 20,
        MixerMeterMode.K14 => 14,
        MixerMeterMode.K12 => 12,
        _ => 0,
    };

    public static double Fraction(double value, double floor, double ceiling) =>
        double.IsNaN(value) || ceiling <= floor ? 0 : Math.Clamp((value - floor) / (ceiling - floor), 0, 1);

    /// <summary>The middle of the segment gap nearest <paramref name="fraction"/> of the rail, or its bottom edge.</summary>
    public static double EdgeY(SignalLevelMetrics metrics, double fraction)
    {
        var bottom = Bottom(metrics);
        var edge = (int)Math.Round(Math.Clamp(fraction, 0, 1) * metrics.Count);
        return edge <= 0 ? bottom : metrics.SegmentTop(bottom, edge - 1) - (metrics.Gap / 2);
    }

    public static string Label(double mark) => mark > 0
        ? string.Create(CultureInfo.InvariantCulture, $"+{mark:0}")
        : mark.ToString("0", CultureInfo.InvariantCulture);

    public static FormattedText Text(string value, IBrush brush) => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        MonoTypeface, Number("FontSize.Micro", 9), brush);

    public static IImmutableSolidColorBrush Brush(Color color) => new ImmutableSolidColorBrush(color);

    public static double Number(string key, double fallback) => ThemeResources.Find(key) is double value ? value : fallback;

    public static double Radius(string key, double fallback) => ThemeResources.Find(key) is CornerRadius radius ? radius.TopLeft : fallback;

    private static Typeface MonoTypeface => ThemeResources.Find("Font.Mono") is FontFamily family ? new Typeface(family) : Typeface.Default;

    private static readonly double[] PeakMarks = [6, 0, -6, -12, -24, -36, -48, -60];
    private static readonly double[] VuMarks = [6, 3, 0, -5, -10, -20, -30];
    private static readonly double[] KMarks = [20, 12, 8, 4, 0, -4, -10, -20];
    private static readonly double[] LufsMarks = [-5, -14, -23, -30, -40, -50, -60];
}
