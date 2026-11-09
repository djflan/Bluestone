using SkiaSharp;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The corona: streamers of light flowing off the shell like a solar wind, in the shell's own glow colours (azure on the
/// left, amber on the right, swaying as the shell's colours do). A GPU shader draws it on the render thread with the
/// rest of the animated mark; where the shader cannot run there is simply no corona.
/// </summary>
internal sealed partial class StoneMark
{
    private static class Corona
    {
        /// <summary>How far the corona reaches past the shell at most, in design units; within the visual's render bounds.</summary>
        private const float Reach = 30;

        private static readonly Lazy<SKRuntimeEffect?> Shader = new(Compile);

        /// <summary>Draws the corona at <paramref name="strength"/> (0–1), coloured by this frame's shell <paramref name="glow"/>.</summary>
        public static void Draw(SKCanvas canvas, double seconds, double strength, SKShader glow)
        {
            if (strength <= 0 || Shader.Value is not { } effect)
            {
                return;
            }

            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["time"] = (float)seconds,
                ["strength"] = (float)strength,
                ["centre"] = new[] { (float)Middle.X, (float)Middle.Y },
                ["apothem"] = (float)(OuterRadius * Math.Sqrt(3) / 2),
                ["reach"] = Reach,
            };
            var children = new SKRuntimeEffectChildren(effect) { ["glow"] = glow };
            using var shader = effect.ToShader(uniforms, children);

            // Added to what is behind it, as light is: it brightens the marble rather than covering it.
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.Plus };
            var extent = (float)OuterRadius + Reach + 4;
            canvas.DrawRect(SKRect.Create((float)Middle.X - extent, (float)Middle.Y - extent, extent * 2, extent * 2), paint);
        }

        /// <summary>Compiles the shader, or returns null (with the reason traced) where it cannot run.</summary>
        internal static SKRuntimeEffect? Compile()
        {
            try
            {
                var effect = SKRuntimeEffect.CreateShader(CoronaSource, out var errors);
                if (effect is null)
                {
                    System.Diagnostics.Trace.TraceWarning($"The logo corona shader did not compile: {errors}");
                }

                return effect;
            }
#pragma warning disable CA1031 // Decoration must never stop start-up; the mark is drawn without its corona instead.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceWarning($"The logo corona shader is unavailable: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// The SkSL source, in design units. Streamers are noise laid out by direction from the centre and distance past the
    /// shell, flowing outward with time: broad, slowly turning rays set how far the light reaches, knots of light stream
    /// out along them, and the streamers bend as they go. The colour is the shell's glow gradient at the same point,
    /// whitening close to the rim.
    /// </summary>
    internal const string CoronaSource = """
        uniform float time;
        uniform float strength;
        uniform float2 centre;
        uniform float apothem;
        uniform float reach;
        uniform shader glow;

        float hash(float3 p) {
            p = fract(p * float3(0.1031, 0.1030, 0.0973));
            p += dot(p, p.yxz + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        float noise(float3 p) {
            float3 i = floor(p);
            float3 f = fract(p);
            float3 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(mix(hash(i), hash(i + float3(1, 0, 0)), u.x),
                           mix(hash(i + float3(0, 1, 0)), hash(i + float3(1, 1, 0)), u.x), u.y),
                       mix(mix(hash(i + float3(0, 0, 1)), hash(i + float3(1, 0, 1)), u.x),
                           mix(hash(i + float3(0, 1, 1)), hash(i + float3(1, 1, 1)), u.x), u.y), u.z);
        }

        float fbm(float3 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 4; i++) {
                v += a * noise(p);
                p = p * 2.03 + float3(3.1, 1.7, 5.3);
                a *= 0.5;
            }
            return v / 0.9375;
        }

        // Signed distance to a hexagon with its points at the top and bottom (after Inigo Quilez's sdHexagon).
        float hexagon(float2 p, float r) {
            const float3 k = float3(-0.866025404, 0.5, 0.577350269);
            p = abs(p.yx);
            p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
            p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
            return length(p) * sign(p.y);
        }

        half4 main(float2 xy) {
            float2 p = xy - centre;
            float d = hexagon(p, apothem);
            if (d < -2.0 || d > reach) {
                return half4(0);
            }

            float past = max(d, 0.0);
            float2 u = p / max(length(p), 0.001);

            // The wind bends the streamers as they leave the shell, a little more the further out they reach.
            float bend = past * 0.018 * (noise(float3(u * 1.5, time * 0.2)) - 0.5);
            u = float2(u.x * cos(bend) - u.y * sin(bend), u.x * sin(bend) + u.y * cos(bend));

            // Broad rays that turn slowly set how far the light reaches; knots of light stream outward along them.
            float rays = fbm(float3(u * 3.4, time * 0.1 - past * 0.012));
            float ray = smoothstep(0.42, 0.78, rays);
            float knots = fbm(float3(u * 10.0, past * 0.085 - time * 1.1));
            float extent = 2.0 + 24.0 * ray * ray + 3.0 * knots;

            float light = exp(-past / extent) * (0.2 + 0.8 * pow(knots, 1.4) * (0.4 + 0.6 * ray));
            light *= smoothstep(-2.0, 1.0, d);
            light *= 1.0 - smoothstep(reach * 0.7, reach, d);
            light *= 0.88 + 0.12 * sin(time * 0.8);

            half4 c = glow.eval(xy);
            float3 col = float3(c.rgb) / max(float(c.a), 0.001);
            col = mix(col, float3(1.0), 0.4 * exp(-past / 2.5));
            float a = clamp(light * strength * 1.1, 0.0, 1.0);
            return half4(half3(col * a), half(a));
        }
        """;
}
