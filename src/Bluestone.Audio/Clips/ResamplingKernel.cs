using System.Numerics;

namespace Bluestone.Audio.Clips;

/// <summary>
/// A windowed-sinc interpolation kernel for sample-rate conversion at a fixed ratio, as a polyphase table with linear
/// interpolation between phases. Stateless: the output at a fractional source position x is
/// <c>Σ source[k] · h(x − k)</c> over the <see cref="Taps"/> source frames around x, so a reader can compute any output
/// frame directly from its source position, and a jump (seek, loop) needs no filter history.
/// </summary>
/// <remarks>
/// Design (ADR 0032): a Kaiser window (β = 8.6, about 85 dB of stopband attenuation) over 32 zero crossings on each side
/// of the centre. The cutoff is 0.91 of the lower of the two Nyquist frequencies, so 44.1 kHz material keeps a flat
/// passband to about 19 kHz and images or aliases above the output's Nyquist are attenuated by the stopband. When
/// downsampling, the kernel widens by the ratio. 512 phases with linear interpolation keep the table error below the
/// stopband. Each phase is normalised to unity DC gain. The cost is <see cref="Taps"/> multiply-adds per channel and
/// output frame (64 at or below a 1:1 ratio). A C# kernel was chosen over a Rust crate (rubato) because it is small,
/// it runs inside the same allocation-free callback as the rest of the graph, and the measurement in ADR 0032 found
/// no reason to cross the native boundary per clip.
/// </remarks>
public sealed class ResamplingKernel
{
    public const int ZeroCrossings = 32;
    public const int Phases = 512;
    public const double Beta = 8.6;
    public const double Cutoff = 0.91;

    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<double, ResamplingKernel> Cache = [];
    private readonly float[] _table;

    private ResamplingKernel(double ratio)
    {
        Ratio = ratio;
        var stretch = Math.Max(1, ratio);
        var cutoff = Cutoff / stretch;
        HalfTaps = (int)Math.Ceiling(ZeroCrossings * stretch);
        Taps = 2 * HalfTaps;
        _table = new float[(Phases + 1) * Taps];
        var half = (double)HalfTaps;
        for (var p = 0; p <= Phases; p++)
        {
            var fraction = (double)p / Phases;
            var row = _table.AsSpan(p * Taps, Taps);
            var sum = 0.0;
            var values = new double[Taps];
            for (var j = 0; j < Taps; j++)
            {
                // Tap j multiplies source frame floor(x) - HalfTaps + 1 + j, at distance t from x.
                var t = fraction + HalfTaps - 1 - j;
                var value = Math.Abs(t) >= half ? 0 : cutoff * Sinc(cutoff * t) * Kaiser(t / half);
                values[j] = value;
                sum += value;
            }

            for (var j = 0; j < Taps; j++)
            {
                row[j] = (float)(values[j] / sum);
            }
        }
    }

    /// <summary>Source frames per output frame.</summary>
    public double Ratio { get; }

    /// <summary>Source frames each output frame reads.</summary>
    public int Taps { get; }

    /// <summary>Source frames before and after the position (Taps / 2).</summary>
    public int HalfTaps { get; }

    /// <summary>The kernel for converting from <paramref name="sourceRate"/> to <paramref name="outputRate"/>; shared and cached.</summary>
    public static ResamplingKernel For(int sourceRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        var ratio = (double)sourceRate / outputRate;
        lock (CacheGate)
        {
            if (!Cache.TryGetValue(ratio, out var kernel))
            {
                kernel = new ResamplingKernel(ratio);
                Cache[ratio] = kernel;
            }

            return kernel;
        }
    }

    /// <summary>
    /// The value at source position <paramref name="position"/>, from <paramref name="source"/> which holds source frames
    /// starting at <paramref name="sourceStart"/> and must cover floor(position) − HalfTaps + 1 to floor(position) + HalfTaps.
    /// </summary>
    public float Interpolate(ReadOnlySpan<float> source, long sourceStart, double position)
    {
        var whole = (long)Math.Floor(position);
        var phase = (position - whole) * Phases;
        var index = (int)phase;
        var blend = (float)(phase - index);
        var first = (int)(whole - HalfTaps + 1 - sourceStart);
        var window = source.Slice(first, Taps);
        var a = _table.AsSpan(index * Taps, Taps);
        var b = _table.AsSpan((index + 1) * Taps, Taps);
        var vectorA = Vector<float>.Zero;
        var vectorB = Vector<float>.Zero;
        var j = 0;
        for (; j <= window.Length - Vector<float>.Count; j += Vector<float>.Count)
        {
            var samples = new Vector<float>(window[j..]);
            vectorA += samples * new Vector<float>(a[j..]);
            vectorB += samples * new Vector<float>(b[j..]);
        }

        var sumA = Vector.Sum(vectorA);
        var sumB = Vector.Sum(vectorB);
        for (; j < window.Length; j++)
        {
            sumA += window[j] * a[j];
            sumB += window[j] * b[j];
        }

        return sumA + ((sumB - sumA) * blend);
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Kaiser(double x)
    {
        var r = 1 - (x * x);
        return r <= 0 ? 0 : BesselI0(Beta * Math.Sqrt(r)) / BesselI0(Beta);
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++)
        {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
            if (term < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
