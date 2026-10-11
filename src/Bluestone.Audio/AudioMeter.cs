namespace Bluestone.Audio;

/// <summary>One meter reading: linear sample values (1 is full scale).</summary>
public readonly record struct MeterReading(float PeakLeft, float PeakRight, float RmsLeft, float RmsRight)
{
    public static double ToDecibels(float linear) => linear <= 0 ? double.NegativeInfinity : 20 * Math.Log10(linear);
}

/// <summary>
/// A stereo level meter, written by the audio thread and readable from any thread without locks.
/// Peak: the largest absolute sample, held and falling at <see cref="PeakFallDecibelsPerSecond"/>.
/// RMS: the root of an exponential average of squared samples with time constant <see cref="RmsTimeConstantSeconds"/>.
/// </summary>
public sealed class AudioMeter
{
    public const double PeakFallDecibelsPerSecond = 20;
    public const double RmsTimeConstantSeconds = 0.3;

    private int _peakLeft;
    private int _peakRight;
    private int _rmsLeft;
    private int _rmsRight;
    private float _heldLeft;
    private float _heldRight;
    private double _meanSquareLeft;
    private double _meanSquareRight;

    public MeterReading Read() => new(
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakLeft)),
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakRight)),
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _rmsLeft)),
        BitConverter.Int32BitsToSingle(Volatile.Read(ref _rmsRight)));

    /// <summary>Audio thread only.</summary>
    internal void Update(StereoBuffer block, int sampleRate)
    {
        var frames = block.Frames;
        if (frames == 0)
        {
            return;
        }

        var fall = (float)Math.Pow(10, -PeakFallDecibelsPerSecond * frames / sampleRate / 20);
        var alpha = 1 - Math.Exp(-1 / (RmsTimeConstantSeconds * sampleRate));
        _heldLeft = Math.Max(Peak(block.Left), _heldLeft * fall);
        _heldRight = Math.Max(Peak(block.Right), _heldRight * fall);
        _meanSquareLeft = MeanSquare(block.Left, _meanSquareLeft, alpha);
        _meanSquareRight = MeanSquare(block.Right, _meanSquareRight, alpha);
        Volatile.Write(ref _peakLeft, BitConverter.SingleToInt32Bits(_heldLeft));
        Volatile.Write(ref _peakRight, BitConverter.SingleToInt32Bits(_heldRight));
        Volatile.Write(ref _rmsLeft, BitConverter.SingleToInt32Bits((float)Math.Sqrt(_meanSquareLeft)));
        Volatile.Write(ref _rmsRight, BitConverter.SingleToInt32Bits((float)Math.Sqrt(_meanSquareRight)));
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak;
    }

    private static double MeanSquare(ReadOnlySpan<float> samples, double meanSquare, double alpha)
    {
        foreach (var sample in samples)
        {
            meanSquare += ((double)sample * sample - meanSquare) * alpha;
        }

        // Flush denormals so a silent channel settles at exactly zero.
        return meanSquare < 1e-20 ? 0 : meanSquare;
    }
}
