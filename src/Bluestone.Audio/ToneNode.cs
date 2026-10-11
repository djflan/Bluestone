namespace Bluestone.Audio;

/// <summary>
/// A sine test tone on both channels: sample n is <c>amplitude × sin(2π × frequency × n / rate)</c>, counted from the
/// node's first rendered frame. It ignores the transport.
/// </summary>
public sealed class ToneNode : AudioNode
{
    private double _phase;

    /// <param name="frequency">In Hz, above 0.</param>
    /// <param name="amplitude">Linear peak amplitude, 0 to 1.</param>
    public ToneNode(double frequency = 440, double amplitude = 0.125)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        ArgumentOutOfRangeException.ThrowIfNegative(amplitude);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amplitude, 1);
        Frequency = frequency;
        Amplitude = amplitude;
    }

    public double Frequency { get; }

    public double Amplitude { get; }

    public override string Name => "Test tone";

    public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
    {
        var step = Frequency / context.SampleRate;
        var phase = _phase;
        for (var i = 0; i < output.Frames; i++)
        {
            var sample = (float)(Amplitude * Math.Sin(2 * Math.PI * phase));
            output.Left[i] = sample;
            output.Right[i] = sample;
            phase += step;
            if (phase >= 1)
            {
                phase -= 1;
            }
        }

        _phase = phase;
    }
}
