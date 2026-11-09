using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The splash mark on screen, drawn with Skia on the render thread so start-up work on the UI thread cannot make it
/// stutter. Its first frame is the still mark; then the light comes alive. The points of the light field drift, so the
/// colour moves through the stone while every face keeps its crisp edge; the shell's colours sway with the same light;
/// and a corona of streamers flows off the shell (StoneMark.Corona.cs).
/// </summary>
internal sealed partial class StoneMark
{
    /// <summary>How long the light takes to come alive after the mark first appears, in seconds.</summary>
    internal const double WakeSeconds = 0.8;

    /// <summary>
    /// How far the whole light field sways (the light moving through the stone) and how far each point wanders on its
    /// own besides (the shimmer), in gem radii.
    /// </summary>
    private const double SwayReach = 0.16;
    private const double DriftReach = 0.09;

    /// <summary>How far the shell's colours turn either way as the light sways, in degrees.</summary>
    private const double ShellTurnDegrees = 35;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);

    /// <summary>
    /// Draws the animated mark <paramref name="seconds"/> after it first appeared, centred in <paramref name="width"/> by
    /// <paramref name="height"/>, as the still splash mark is.
    /// </summary>
    internal static void DrawAnimated(SKCanvas canvas, double width, double height, double seconds)
    {
        var size = Math.Min(width, height);
        if (size <= 0)
        {
            return;
        }

        var scale = (float)(size / 100);
        canvas.Save();
        canvas.Translate((float)((width - size) / 2), (float)((height - size) / 2));
        canvas.Scale(scale);

        var wake = Ease(Math.Clamp(seconds / WakeSeconds, 0, 1));
        Span<Vector> drift = stackalloc Vector[LightField.Length];
        var sway = Drift(seconds, wake, drift);
        using var shell = new ShellPaints(ShellLight(seconds, wake, sway));

        Skia.DrawBloom(canvas, shell, LayerKind.Aura, AuraBlur, AuraOpacity, scale);
        Skia.DrawBloom(canvas, shell, LayerKind.Aura, NearAuraBlur, NearAuraOpacity, scale);
        Skia.DrawBloom(canvas, shell, LayerKind.Halo, HaloBlur, 1, scale);
        Corona.Draw(canvas, seconds, wake, shell.Glow);
        Skia.DrawArtwork(canvas, shell, drift);
        Skia.DrawBloom(canvas, shell, LayerKind.Flare, FlareBlur, FlareOpacity, scale);
        canvas.Restore();
    }

    /// <summary>
    /// Sways the whole light field on a slow figure and moves each point round its own smaller ellipse, eased in by
    /// <paramref name="wake"/>. Returns the sway, in gem radii, which the shell's colours follow.
    /// </summary>
    private static Vector Drift(double seconds, double wake, Span<Vector> drift)
    {
        var t = seconds;
        var sway = new Vector(Math.Sin(2 * Math.PI * t / 13), Math.Sin((2 * Math.PI * t / 17) + 1)) * (SwayReach * wake);
        for (var i = 0; i < drift.Length; i++)
        {
            // Fixed, unrelated periods (9–19 s) and phases per point, so the shimmer never visibly repeats or moves in step.
            var periodX = 9 + ((i * 7) % 11);
            var periodY = 11 + ((i * 5) % 9);
            var phase = i * 2.399963;
            drift[i] = sway + (new Vector(
                Math.Sin((2 * Math.PI * t / periodX) + phase),
                Math.Sin((2 * Math.PI * t / periodY) + (phase * 1.618))) * (DriftReach * wake));
        }

        return sway;
    }

    /// <summary>
    /// Where the shell's colours lie: the cyan-to-amber gradient moves with the light inside the gem and turns slowly
    /// about the centre, so the colours travel round the hexagon. Identity (the still mark) until the light wakes.
    /// </summary>
    private static SKMatrix ShellLight(double seconds, double wake, Vector sway)
    {
        var degrees = ShellTurnDegrees * wake * Math.Sin((2 * Math.PI * seconds / 23) + 0.5);
        var turn = SKMatrix.CreateRotationDegrees((float)degrees, (float)Middle.X, (float)Middle.Y);
        var move = SKMatrix.CreateTranslation((float)(sway.X * GemRadius), (float)(sway.Y * GemRadius));
        return turn.PostConcat(move);
    }

    /// <summary>Cubic ease-out: quick to start, gentle to land.</summary>
    private static double Ease(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>This frame's shell gradients, placed by <see cref="ShellLight"/>.</summary>
    private sealed class ShellPaints(SKMatrix light) : IDisposable
    {
        public SKShader Fill { get; } = Place(Skia.ShellFillShader, light);

        public SKShader Rim { get; } = Place(Skia.ShellRimShader, light);

        public SKShader Glow { get; } = Place(Skia.ShellGlowShader, light);

        public SKShader GemEdge { get; } = Place(Skia.GemEdgeShader, light);

        public void Dispose()
        {
            // Only shaders made for this frame are disposed; the shared ones are used unplaced while the light is still.
            foreach (var (placed, shared) in new[] { (Fill, Skia.ShellFillShader), (Rim, Skia.ShellRimShader), (Glow, Skia.ShellGlowShader), (GemEdge, Skia.GemEdgeShader) })
            {
                if (!ReferenceEquals(placed, shared))
                {
                    placed.Dispose();
                }
            }
        }

        // Skia hands back the same shader for an identity matrix, which SkiaSharp wraps as the same object, so the shared
        // shader is used as it is rather than risk disposing it.
        private static SKShader Place(SKShader shader, SKMatrix light) => light.IsIdentity ? shader : shader.WithLocalMatrix(light);
    }

    /// <summary>The mark's geometry and paints in Skia. Built on first use, on the render thread.</summary>
    private static class Skia
    {
        public static readonly SKShader ShellFillShader = Across(ShellFillStops);
        public static readonly SKShader ShellRimShader = Across(ShellRimStops);
        public static readonly SKShader ShellGlowShader = Across(ShellGlowStops);
        public static readonly SKShader GemEdgeShader = Across(GemEdgeStops);

        private static readonly SKPath OuterHex = Hexagon(OuterRadius);
        private static readonly SKPath ShellInner = Hexagon(ShellInnerRadius);
        private static readonly SKPath ShellBand = Band(OuterRadius, ShellInnerRadius);
        private static readonly SKPath Gem = Hexagon(GemRadius);
        private static readonly SKPath Rim = Hexagon(OuterRadius - 0.75);

        private static readonly SKShader RecessShader = Recess();
        private static readonly SKColor RidgeSk = Sk(Color.Parse(RidgeColor));
        private static readonly SKColor OutlineSk = Sk(Color.Parse(RecessOutlineColor));

        private static readonly LitFacet[] Cut = [.. Facets.Select(facet => new LitFacet(facet))];

        /// <summary>One bloom pass (as DrawAura, DrawHalo, and DrawFlare), blurred as Avalonia's BlurEffect would blur it.</summary>
        public static void DrawBloom(SKCanvas canvas, ShellPaints shell, LayerKind kind, double blur, double opacity, float scale)
        {
            // Avalonia's blur radius is in device-independent pixels (the still layers scale it with the mark) and its
            // sigma is radius × 0.288675 + 0.5; the canvas here is in design units, so divide back by the scale.
            var sigma = (float)(((0.288675 * blur * scale) + 0.5) / scale);
            using var filter = SKImageFilter.CreateBlur(sigma, sigma);
            using var layer = new SKPaint { ImageFilter = filter, Color = SKColors.White.WithAlpha(Alpha(opacity)) };
            canvas.SaveLayer(layer);
            using var paint = new SKPaint { IsAntialias = true };
            switch (kind)
            {
                case LayerKind.Aura:
                    Fill(canvas, paint, shell.Glow, OuterHex);
                    Stroke(canvas, paint, shell.Glow, 4, SKStrokeJoin.Round, OuterHex);
                    break;
                case LayerKind.Halo:
                    Fill(canvas, paint, shell.Glow, ShellBand);
                    Stroke(canvas, paint, shell.Glow, 6, SKStrokeJoin.Round, ShellBand);
                    break;
                default:
                    Stroke(canvas, paint, shell.Glow, 1.4, SKStrokeJoin.Round, ShellInner);
                    Stroke(canvas, paint, shell.GemEdge, 0.8, SKStrokeJoin.Miter, Gem);
                    break;
            }

            canvas.Restore();
        }

        /// <summary>As the still mark's DrawArtwork (full cut), with the shell's colours and the light field moved.</summary>
        public static void DrawArtwork(SKCanvas canvas, ShellPaints shell, ReadOnlySpan<Vector> drift)
        {
            using var paint = new SKPaint { IsAntialias = true };

            Fill(canvas, paint, shell.Fill, ShellBand);
            Fill(canvas, paint, RecessShader, ShellInner);

            foreach (var facet in Cut)
            {
                facet.Draw(canvas, paint, drift);
            }

            paint.Shader = null;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.Color = RidgeSk;
            for (var k = 0; k < 6; k++)
            {
                // The odd spokes are the cube's edges (to the upper right, bottom and upper left), so they catch more light.
                paint.StrokeWidth = k % 2 == 1 ? 0.5f : 0.28f;
                canvas.DrawLine(Sk(Middle), Sk(Corner(GemRadius, k)), paint);
                paint.StrokeWidth = 0.28f;
                canvas.DrawLine(Sk(Corner(GemRadius, k)), Sk(Corner(GemRadius, k + 2)), paint);
            }

            paint.StrokeCap = SKStrokeCap.Butt;
            paint.Color = SKColors.White;
            Stroke(canvas, paint, shell.GemEdge, 0.6, SKStrokeJoin.Miter, Gem);
            paint.Shader = null;
            paint.Color = OutlineSk;
            paint.StrokeWidth = 0.7f;
            canvas.DrawPath(ShellInner, paint);
            paint.Color = SKColors.White;
            Stroke(canvas, paint, shell.Rim, 1.5, SKStrokeJoin.Miter, Rim);
        }

        private static void Fill(SKCanvas canvas, SKPaint paint, SKShader shader, SKPath path)
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Shader = shader;
            paint.Color = SKColors.White;
            canvas.DrawPath(path, paint);
        }

        private static void Stroke(SKCanvas canvas, SKPaint paint, SKShader shader, double width, SKStrokeJoin join, SKPath path)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.Shader = shader;
            paint.StrokeWidth = (float)width;
            paint.StrokeJoin = join;
            paint.StrokeMiter = 10;
            canvas.DrawPath(path, paint);
        }

        private static SKShader Across((string Color, double Offset)[] stops) => SKShader.CreateLinearGradient(
            new SKPoint((float)(Middle.X - (OuterRadius * 0.93)), (float)(Middle.Y - (OuterRadius * 0.37))),
            new SKPoint((float)(Middle.X + (OuterRadius * 0.93)), (float)(Middle.Y + (OuterRadius * 0.37))),
            [.. stops.Select(stop => Sk(Color.Parse(stop.Color)))],
            [.. stops.Select(stop => (float)stop.Offset)],
            SKShaderTileMode.Clamp);

        /// <summary>The recess: Avalonia's default radial brush, an ellipse filling the inner hexagon's bounds.</summary>
        private static SKShader Recess()
        {
            var bounds = ShellInner.TightBounds;
            var centre = new SKPoint(bounds.MidX, bounds.MidY);
            var radiusX = bounds.Width / 2;
            var radiusY = bounds.Height / 2;
            return SKShader.CreateRadialGradient(
                centre,
                radiusX,
                [.. RecessStops.Select(stop => Sk(Color.Parse(stop.Color)))],
                [.. RecessStops.Select(stop => (float)stop.Offset)],
                SKShaderTileMode.Clamp,
                SKMatrix.CreateScale(1, radiusY / radiusX, centre.X, centre.Y));
        }

        private static SKPath Hexagon(double radius) => Polygon([.. Enumerable.Range(0, 6).Select(k => Corner(radius, k))]);

        private static SKPath Band(double outer, double inner)
        {
            var band = new SKPath { FillType = SKPathFillType.EvenOdd };
            band.AddPath(Hexagon(outer));
            band.AddPath(Hexagon(inner));
            return band;
        }

        public static SKPath Polygon(Point[] corners)
        {
            var path = new SKPath();
            path.MoveTo(Sk(corners[0]));
            foreach (var corner in corners.Skip(1))
            {
                path.LineTo(Sk(corner));
            }

            path.Close();
            return path;
        }

        public static SKPoint Sk(Point point) => new((float)point.X, (float)point.Y);

        public static SKColor Sk(Color color) => new(color.R, color.G, color.B, color.A);

        private static byte Alpha(double opacity) => (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
    }

    /// <summary>One face of the cut, shaded each frame from the moving light field.</summary>
    private sealed class LitFacet
    {
        private readonly Facet _facet;
        private readonly SKPath _path;
        private readonly Rect _bounds;

        public LitFacet(Facet facet)
        {
            _facet = facet;
            _path = Skia.Polygon(facet.Corners);

            // The still mark's geometry bounds, worked out here because Avalonia geometry belongs to the UI thread.
            var xs = facet.Corners.Select(corner => corner.X).ToArray();
            var ys = facet.Corners.Select(corner => corner.Y).ToArray();
            _bounds = new Rect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
        }

        public void Draw(SKCanvas canvas, SKPaint paint, ReadOnlySpan<Vector> drift)
        {
            var (from, to, colors) = Shading(_facet.Corners, _facet.Light, drift);
            SKColor[] stops = [Skia.Sk(colors[0]), Skia.Sk(colors[1]), Skia.Sk(colors[2])];
            paint.Color = SKColors.White;

            // The still mark fills each face and strokes it in its own gradient, so neighbours meet without a seam;
            // Avalonia maps the stroke's gradient onto the bounds grown by half the pen, so do the same here.
            using (var fill = Gradient(from, to, _bounds, _bounds, stops))
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Shader = fill;
                canvas.DrawPath(_path, paint);
            }

            using (var stroke = Gradient(from, to, _bounds, _bounds.Inflate(0.15), stops))
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 0.3f;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Shader = stroke;
                canvas.DrawPath(_path, paint);
            }

            paint.Shader = null;
        }

        /// <summary>The still mark's gradient: from and to relative to <paramref name="shape"/>, mapped onto <paramref name="target"/>.</summary>
        private static SKShader Gradient(Point from, Point to, Rect shape, Rect target, SKColor[] colors)
        {
            return SKShader.CreateLinearGradient(Map(from), Map(to), colors, [0, 0.5f, 1], SKShaderTileMode.Clamp);

            SKPoint Map(Point point) => new(
                (float)(target.X + ((point.X - shape.X) / shape.Width * target.Width)),
                (float)(target.Y + ((point.Y - shape.Y) / shape.Height * target.Height)));
        }
    }

    internal sealed class AnimatedMark : CompositionCustomVisualHandler
    {
        private TimeSpan? _start;
        private TimeSpan _lastFrame;
        private bool _running;

        public override void OnMessage(object message)
        {
            switch (message)
            {
                case Start:
                    _start = null;
                    _running = true;
                    Invalidate();
                    RegisterForNextAnimationFrameUpdate();
                    break;
                case Stop:
                    _running = false;
                    break;
            }
        }

        // The light moves slowly, so 30 frames a second is plenty.
        public override void OnAnimationFrameUpdate()
        {
            if (!_running)
            {
                return;
            }

            if (CompositionNow - _lastFrame >= FrameInterval)
            {
                _lastFrame = CompositionNow;
                Invalidate();
            }

            RegisterForNextAnimationFrameUpdate();
        }

        // The bloom and corona spill well past the mark's box.
        public override Rect GetRenderBounds()
        {
            var size = EffectiveSize;
            return new Rect(0, 0, size.X, size.Y).Inflate(Math.Min(size.X, size.Y) * 0.4);
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            var size = EffectiveSize;
            if (size.X < 1 || size.Y < 1 || drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            _start ??= CompositionNow;
            using var lease = feature.Lease();
            DrawAnimated(lease.SkCanvas, size.X, size.Y, (CompositionNow - _start.Value).TotalSeconds);
        }

        public sealed class Start
        {
            public static readonly Start Instance = new();
        }

        public sealed class Stop
        {
            public static readonly Stop Instance = new();
        }
    }
}
