using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// Polished black marble with glowing azure veins and a fleck of gold beside them, drawn by a GPU shader, with light
/// travelling along the veins while it is shown. Each slab shows a different part of the marble (<see cref="Seed"/>).
/// Where Skia or the shader is unavailable it draws a plain dark rounded card instead, so it can never stop start-up.
/// The marble is drawn and animated on the render thread (a composition visual), so work on the UI thread, such as
/// start-up behind the splash, cannot make it stutter.
/// </summary>
internal sealed class MarbleSlab : Control
{
    public const double CornerRadius = 18;

    /// <summary>Frames are drawn at most this often; the marble moves slowly, so more would only cost power.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);

    private static readonly IBrush FallbackFill = new SolidColorBrush(Color.Parse("#0B0D12"));
    private static readonly Lazy<SKRuntimeEffect?> Shader = new(Compile);

    /// <summary>
    /// Which part of the marble is shown. Each slab picks one at random, so every launch (and every About) shows a
    /// different stone; set it to show a particular one.
    /// </summary>
#pragma warning disable CA5394 // Decorative variety, not security.
    public Vector Seed { get; set; } = new(Random.Shared.NextDouble() * 40, Random.Shared.NextDouble() * 40);
#pragma warning restore CA5394
    private CompositionCustomVisual? _visual;

    /// <summary>The SkSL source: marble base, blue veins with a travelling glow, gold fleck, a calm area for the title, rounded edge.</summary>
    internal const string Source = """
        uniform float2 size;
        uniform float time;
        uniform float radius;
        uniform float2 seed;

        float hash(float2 p) {
            p = fract(p * float2(123.34, 456.21));
            p += dot(p, p + 45.32);
            return fract(p.x * p.y);
        }

        float noise(float2 p) {
            float2 i = floor(p);
            float2 f = fract(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash(i), hash(i + float2(1, 0)), u.x),
                       mix(hash(i + float2(0, 1)), hash(i + float2(1, 1)), u.x), u.y);
        }

        float fbm(float2 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 5; i++) {
                v += a * noise(p);
                p = p * 2.03 + float2(17.1, 9.2);
                a *= 0.5;
            }
            return v;
        }

        // Fewer octaves: smooth, flowing lines for the veins.
        float fbm3(float2 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 3; i++) {
                v += a * noise(p);
                p = p * 2.07 + float2(5.3, 1.7);
                a *= 0.5;
            }
            return v / 0.875;
        }

        // Thin bright ridges where warped noise crosses its middle: the shape of marble veins.
        float ridge(float2 p, float width) {
            float n = fbm(p);
            return 1.0 - smoothstep(0.0, width, abs(n - 0.5));
        }

        float roundedBox(float2 p, float2 extent, float r) {
            float2 q = abs(p) - extent + r;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        half4 main(float2 xy) {
            float2 uv = xy / size.y;

            // The seed moves the pattern to another part of the same endless marble.
            float2 at = uv + seed;

            // Domain warp: the marble's flowing, folded look.
            float2 w = float2(fbm(at * 1.6 + float2(3.1, 7.4)), fbm(at * 1.6 + float2(8.3, 2.8)));
            float2 p = at * 2.2 + w * 1.6;

            // Black stone with faint charcoal clouding and fine grey-white hairlines.
            float cloud = fbm(p * 1.3);
            float3 col = mix(float3(0.010, 0.012, 0.017), float3(0.085, 0.092, 0.112), smoothstep(0.30, 0.90, cloud));
            float grain = noise(xy * 0.9) * 0.5 + noise(xy * 0.35) * 0.5;
            col *= 0.85 + 0.30 * grain;
            float hair = 1.0 - smoothstep(0.0, 0.020, abs(fbm3(p * 1.7 + 11.0) - 0.5));
            col += float3(0.36, 0.38, 0.42) * hair * 0.22;
            float hair2 = 1.0 - smoothstep(0.0, 0.012, abs(fbm3(p * 3.4 + 3.0) - 0.5));
            col += float3(0.28, 0.30, 0.34) * hair2 * 0.12;

            // Blue veins: a sharp core with a wide glow, brightness travelling along them.
            float2 vp = at * 1.1 + w * 0.8 + float2(5.0, 1.0);
            float d = abs(fbm3(vp) - 0.5);
            float core = 1.0 - smoothstep(0.0, 0.012, d);
            float glow = exp(-d * 38.0);
            float2 vp2 = at * 1.7 + w * 1.1 + float2(1.0, 6.0);
            float d2 = abs(fbm3(vp2) - 0.5);
            float core2 = 1.0 - smoothstep(0.0, 0.008, d2);
            float glow2 = exp(-d2 * 60.0);

            float along = (w.x * 9.0 + uv.x * 3.0 + uv.y * 1.5);
            float flow = 0.45 + 0.55 * pow(0.5 + 0.5 * sin(along - time * 1.4), 6.0);
            float flow2 = 0.40 + 0.60 * pow(0.5 + 0.5 * sin(along * 1.3 + 2.0 - time * 1.1), 6.0);

            float3 azure = float3(0.16, 0.48, 1.0);
            float3 hot = float3(0.80, 0.92, 1.0);
            col += azure * glow * 0.9 * flow + mix(azure, hot, flow) * core * 1.1 * flow;
            col += azure * glow2 * 0.70 * flow2 + mix(azure, hot, flow2) * core2 * 0.7 * flow2;

            // Gold fleck: sparse sparkles only just beside the blue veins.
            // The seed changes which cells sparkle (cell), not where a cell is on the card (grid).
            float2 grid = floor(xy / 5.0);
            float2 cell = grid + floor(seed * 97.0);
            float2 centre = (grid + 0.2 + 0.6 * float2(hash(cell + 3.1), hash(cell + 8.7))) * 5.0;
            float fleck = 1.0 - smoothstep(0.4, 1.3 * (0.6 + hash(cell + 1.9)), length(xy - centre));
            float sparkle = step(0.80, hash(cell + 7.0)) * fleck;
            float near = smoothstep(0.05, 0.02, d) * (1.0 - core);
            col += float3(0.95, 0.74, 0.34) * sparkle * near * (0.55 + 0.45 * hash(cell));

            // A polished sheen from the top left.
            float sheen = exp(-length((uv - float2(0.25, -0.15)) * float2(0.9, 1.6)) * 2.2);
            col += float3(0.10, 0.12, 0.16) * sheen;

            // Calm the stone behind the title so it reads clearly.
            float2 t = (xy / size - float2(0.30, 0.42)) * float2(1.6, 2.4);
            col *= 1.0 - 0.55 * exp(-dot(t, t) * 2.0);

            // Rounded edge, anti-aliased, with a thin lit rim along the top.
            float sd = roundedBox(xy - size * 0.5, size * 0.5 - 1.0, radius);
            float alpha = 1.0 - smoothstep(-0.75, 0.75, sd);
            float rim = (1.0 - smoothstep(0.0, 1.6, abs(sd + 1.0))) * smoothstep(0.6, 0.0, xy.y / size.y);
            col += float3(0.30, 0.36, 0.48) * rim * 0.5;

            return half4(half3(col) * half(alpha), half(alpha));
        }
        """;

    /// <summary>Compiles the shader, or returns null (with the reason traced) where it cannot run.</summary>
    internal static SKRuntimeEffect? Compile()
    {
        try
        {
            var effect = SKRuntimeEffect.CreateShader(Source, out var errors);
            if (effect is null)
            {
                System.Diagnostics.Trace.TraceWarning($"The splash marble shader did not compile: {errors}");
            }

            return effect;
        }
#pragma warning disable CA1031 // Decoration must never stop start-up; the plain card is drawn instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning($"The splash marble shader is unavailable: {ex.Message}");
            return null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Shader.Value is not { } shader || ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor)
        {
            return;
        }

        _visual = compositor.CreateCustomVisual(new MarbleVisual(shader));
        _visual.Size = new Vector(Bounds.Width, Bounds.Height);
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.SendHandlerMessage(new Start(Seed));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_visual is not null)
        {
            _visual.SendHandlerMessage(Stop.Instance);
            ElementComposition.SetElementChildVisual(this, null);
            _visual = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_visual is not null)
        {
            _visual.Size = new Vector(e.NewSize.Width, e.NewSize.Height);
        }
    }

    /// <summary>The plain card, under the marble, so it shows wherever the shader cannot be drawn.</summary>
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width >= 1 && Bounds.Height >= 1)
        {
            context.DrawRectangle(FallbackFill, null, new RoundedRect(new Rect(Bounds.Size), CornerRadius));
        }
    }

    private sealed record Start(Vector Seed);

    private sealed class Stop
    {
        public static readonly Stop Instance = new();
    }

    /// <summary>Draws the shader on the render thread and asks for the next frame there, at most every <see cref="FrameInterval"/>.</summary>
    private sealed class MarbleVisual(SKRuntimeEffect effect) : CompositionCustomVisualHandler
    {
        private Vector _seed;
        private TimeSpan? _start;
        private TimeSpan _lastFrame;
        private bool _running;

        public override void OnMessage(object message)
        {
            switch (message)
            {
                case Start start:
                    _seed = start.Seed;
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

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            var size = EffectiveSize;
            if (size.X < 1 || size.Y < 1 || drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            _start ??= CompositionNow;
            using var lease = feature.Lease();
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["size"] = new[] { (float)size.X, (float)size.Y },
                ["time"] = (float)(CompositionNow - _start.Value).TotalSeconds,
                ["radius"] = (float)CornerRadius,
                ["seed"] = new[] { (float)_seed.X, (float)_seed.Y },
            };
            using var shader = effect.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            lease.SkCanvas.DrawRect(SKRect.Create((float)size.X, (float)size.Y), paint);
        }
    }
}
