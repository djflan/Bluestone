using System.Globalization;
using Avalonia.Media;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A colour as the picker edits it: hue in degrees [0, 360), saturation and brightness (HSV value) in [0, 1]. Kept as the
/// picker's own state, so the hue survives a trip through grey or black, where RGB has none.
/// </summary>
public readonly record struct ColourHsv(double Hue, double Saturation, double Brightness)
{
    /// <summary>The opaque RGB colour, each channel rounded to the nearest byte.</summary>
    public Color ToColor()
    {
        var h = ((Hue % 360) + 360) % 360;
        var s = Math.Clamp(Saturation, 0, 1);
        var v = Math.Clamp(Brightness, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));

        static byte Byte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// <paramref name="colour"/> as hue, saturation, and brightness. A grey (no saturation) or black has no hue of its own:
    /// it keeps <paramref name="hue"/> (and black keeps <paramref name="saturation"/>), so the wheel's thumb does not jump.
    /// </summary>
    public static ColourHsv From(Color colour, double hue = 0, double saturation = 0)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (max <= 0)
        {
            return new(hue, saturation, 0);
        }

        if (d <= 0)
        {
            return new(hue, 0, max);
        }

        var h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
        return new((h + 360) % 360, d / max, max);
    }

    /// <summary>"#RRGGBB", upper case.</summary>
    public static string Hex(Color colour) => string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");

    /// <summary>Six hex digits, with or without the '#', as a colour; anything else (a half-typed value) is null.</summary>
    public static Color? ParseHex(string? text)
    {
        var digits = text?.Trim() ?? string.Empty;
        if (digits.StartsWith('#'))
        {
            digits = digits[1..];
        }

        return digits.Length == 6 && uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
    }

    /// <summary>What the picker reads out: "H 292°  S 60%  B 96%".</summary>
    public string Readout => string.Create(CultureInfo.InvariantCulture, $"H {Math.Round(Hue) % 360,3:0}°  S {Saturation * 100,3:0}%  B {Brightness * 100,3:0}%");
}
