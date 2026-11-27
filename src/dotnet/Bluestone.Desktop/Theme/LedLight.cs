using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Bluestone.Desktop.Theme;

/// <summary>
/// One lamp's light, baked: a small premultiplied bitmap of a real gaussian blur, drawn centred on the lamp, so the
/// frame that shows it is one image draw (no blur, no layers, no allocation). <see cref="Pad"/> is how far it reaches
/// past the lamp on every side.
/// </summary>
internal sealed class LedSprite(Bitmap image, double pad)
{
    public Bitmap Image { get; } = image;

    public double Pad { get; } = pad;

    /// <summary>Draws the light around <paramref name="lamp"/>.</summary>
    public void Draw(DrawingContext context, Rect lamp) => context.DrawImage(Image, lamp.Inflate(Pad));
}

/// <summary>
/// The physical-LED light (direction B): a lit lamp throws a soft spill onto the dark surface around it and a tighter,
/// hotter bloom just past its lens, both real gaussian blurs added together. They are baked once per colour, shape,
/// intensity, and <see cref="Glow.Strength"/> into <see cref="LedSprite"/>s: fixed theme colours (a key's, the clip
/// lamp's) are cached here for the life of the glow setting; gradient colours are cached by their
/// <see cref="MeterGradient"/>, which empties them when it changes. <see cref="Glow.Use"/> empties both. While the glow is
/// off nothing is baked and <see cref="For"/> returns null. UI thread only.
/// </summary>
internal static class LedLight
{
    /// <summary>The bitmaps' pixel density: sharp on a 2× display, and soft light needs no more on a 3× one.</summary>
    private const double Scale = 2;

    /// <summary>Brightness levels a fading lamp's light passes through (a lamp at <c>LedLevels.Steps</c> is level 4).</summary>
    public const int Levels = 4;

    private const double SpillGain = 1.1;
    private const double BloomGain = 1.2;

    private static readonly Dictionary<(uint, double, double, double, bool, double, double), LedSprite?> Fixed = [];

    /// <summary>How many sprites the fixed cache holds (for tests).</summary>
    internal static int CachedCount => Fixed.Count;

    /// <summary>The light level (0 to <see cref="Levels"/>) for a lamp at <paramref name="step"/> of its fade.</summary>
    public static int Level(int step) =>
        step <= 0 ? 0 : Math.Min(Levels, ((step * Levels) + Controls.LedLevels.Steps - 1) / Controls.LedLevels.Steps);

    /// <summary>A fixed theme colour's light, cached until the glow setting changes; null while the glow is off.</summary>
    public static LedSprite? For(Color color, Size size, double radius, bool ellipse, double intensity, double spread = 1)
    {
        var key = (color.ToUInt32(), size.Width, size.Height, radius, ellipse, intensity, spread);
        if (!Fixed.TryGetValue(key, out var sprite))
        {
            sprite = Bake(color, size, radius, ellipse, intensity, spread);
            Fixed[key] = sprite;
        }

        return sprite;
    }

    public static void Clear() => Fixed.Clear();

    /// <summary>
    /// Bakes a lamp's light: <paramref name="intensity"/> scales its alpha (1 a lit lamp, less for one of many close
    /// together, so a column pools to its own colour instead of white), <paramref name="spread"/> its reach. Null while
    /// the glow is off or the light would be invisible.
    /// </summary>
    public static LedSprite? Bake(Color color, Size size, double radius, bool ellipse, double intensity, double spread = 1)
    {
        var s = Glow.Strength;
        if (!Glow.IsOn || intensity <= 0 || size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        // Strength widens the light a little and brightens it a lot: 0.5 is a hint of light, 2 a bright studio lamp. A gain
        // past 1 lays the blurred shape down again (added), the way a brighter lamp floods its surroundings.
        var spillSigma = (1.2 + (1.0 * s)) * spread;
        var bloomSigma = (0.6 + (0.35 * s)) * spread;
        var spillGain = SpillGain * intensity * s;
        // The bloom never outshines the lamp's own edge (it would read as a ring), so it stops at full alpha.
        var bloomGain = Math.Min(1, BloomGain * intensity * s);
        if (spillGain < 0.01 && bloomGain < 0.01)
        {
            return null;
        }

        var pad = Math.Ceiling((spillSigma * 2.8) + 1);
        var width = (int)Math.Ceiling((size.Width + (2 * pad)) * Scale);
        var height = (int)Math.Ceiling((size.Height + (2 * pad)) * Scale);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixels = new SKBitmap(info);
        using (var canvas = new SKCanvas(pixels))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)Scale);
            var lamp = new Rect(pad, pad, size.Width, size.Height);
            using var paint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };

            // The spill: the lamp's own colour, wide and faint, on the surface it sits in.
            using (var spill = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)spillSigma))
            {
                paint.MaskFilter = spill;
                Layers(canvas, paint, color, spillGain, lamp.Inflate(spread), radius + spread, ellipse);
            }

            // The bloom: hotter and tighter, the lens scattering its core.
            using (var bloom = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)bloomSigma))
            {
                paint.MaskFilter = bloom;
                Layers(canvas, paint, Palette.Lighten(color, 0.15), bloomGain, lamp.Inflate(0.3), radius + 0.3, ellipse);
            }

            paint.MaskFilter = null;
        }

        // At 96 DPI the bitmap's size is its pixel size, so drawing it into a rectangle takes every pixel (at a higher DPI
        // Avalonia would read the DIP size as pixels and draw only a corner).
        var image = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixels.GetPixels(), new PixelSize(width, height),
            new Vector(96, 96), pixels.RowBytes);
        return new LedSprite(image, pad);
    }

    // The shape at <paramref name="gain"/>: whole layers at full alpha, then the remainder.
    private static void Layers(SKCanvas canvas, SKPaint paint, Color color, double gain, Rect r, double radius, bool ellipse)
    {
        for (; gain > 0.01; gain -= 1)
        {
            paint.Color = Sk(Palette.Alpha(color, Math.Min(1, gain)));
            Shape(canvas, paint, r, radius, ellipse);
        }
    }

    private static void Shape(SKCanvas canvas, SKPaint paint, Rect r, double radius, bool ellipse)
    {
        var rect = new SKRect((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);
        if (ellipse)
        {
            canvas.DrawOval(rect, paint);
        }
        else
        {
            canvas.DrawRoundRect(rect, (float)Math.Max(0, radius), (float)Math.Max(0, radius), paint);
        }
    }

    private static SKColor Sk(Color c) => new(c.R, c.G, c.B, c.A);
}
