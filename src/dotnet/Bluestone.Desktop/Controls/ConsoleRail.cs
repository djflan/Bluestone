using Bluestone.Audio;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The console's shared vertical geometry and dB taper: the gain-LED gutter, the fader travel, the meter, and its scale
/// all place a dB value with the same curve, so 0 dB on the scale sits level with the fader cap at unity and with the
/// gain LEDs lit to unity. The curve gives the working range (−24 to +6 dB) most of the travel, as on a console fader.
/// </summary>
internal static class ConsoleRail
{
    /// <summary>The fader travel starts this far below the top of the fader zone (room for the cap's top half).</summary>
    public const double TravelTop = 12;

    /// <summary>The fader travel ends this far above the bottom of the fader zone.</summary>
    public const double TravelBottomInset = 6;

    /// <summary>The meter sits this far inside the fader zone, top and bottom.</summary>
    public const double MeterInset = 4;

    private static readonly (double Db, double Fraction)[] Taper =
    [
        (-60, 0), (-48, 0.12), (-36, 0.25), (-24, 0.40), (-12, 0.60), (-6, 0.72), (0, 0.86), (6, 1),
    ];

    /// <summary>Where <paramref name="db"/> sits on the Peak scale (−60 to +6 dB), from 0 at the floor to 1 at the top.</summary>
    public static double Position(double db)
    {
        if (double.IsNaN(db) || db <= Taper[0].Db)
        {
            return 0;
        }

        for (var i = 1; i < Taper.Length; i++)
        {
            if (db <= Taper[i].Db)
            {
                var (d0, f0) = Taper[i - 1];
                var (d1, f1) = Taper[i];
                return f0 + ((db - d0) / (d1 - d0) * (f1 - f0));
            }
        }

        return 1;
    }

    /// <summary>The dB value at <paramref name="fraction"/> of the Peak scale; the inverse of <see cref="Position"/>.</summary>
    public static double Db(double fraction)
    {
        if (fraction <= 0)
        {
            return Taper[0].Db;
        }

        for (var i = 1; i < Taper.Length; i++)
        {
            if (fraction <= Taper[i].Fraction)
            {
                var (d0, f0) = Taper[i - 1];
                var (d1, f1) = Taper[i];
                return d0 + ((fraction - f0) / (f1 - f0) * (d1 - d0));
            }
        }

        return Taper[^1].Db;
    }

    /// <summary>
    /// Where a meter reading in a display mode's unit sits on that mode's scale: the Peak modes use the console taper, the
    /// others (VU, K, LUFS) are linear over their own range.
    /// </summary>
    public static double Fraction(MixerMeterMode mode, double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (mode == MixerMeterMode.PeakRms)
        {
            return Position(value);
        }

        var (floor, ceiling) = MixerRail.Range(mode);
        return MixerRail.Fraction(value, floor, ceiling);
    }

    /// <summary>The y of <paramref name="fraction"/> of a travel from <paramref name="bottom"/> up to <paramref name="top"/>.</summary>
    public static double Y(double top, double bottom, double fraction) => bottom - (Math.Clamp(fraction, 0, 1) * (bottom - top));
}
