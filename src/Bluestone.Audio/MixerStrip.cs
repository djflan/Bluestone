namespace Bluestone.Audio;

/// <summary>
/// The mixer's pan law: stereo balance with a sine taper. At centre both sides pass at unity (0 dB). Panning
/// towards one side leaves that side at unity and fades the other with <c>cos(|pan| × π/2)</c>: −3 dB at
/// half way, silent at the edge. A mono source (equal on both sides) is therefore 3 dB louder at centre than
/// hard-panned, as with a balance control; the strip is stereo throughout, so this keeps a stereo source
/// unchanged at centre.
/// </summary>
public static class PanLaw
{
    /// <param name="pan">-1 hard left, 0 centre, 1 hard right.</param>
    public static (float Left, float Right) Gains(double pan)
    {
        pan = Math.Clamp(pan, -1, 1);
        var left = pan > 0 ? Math.Cos(pan * Math.PI / 2) : 1;
        var right = pan < 0 ? Math.Cos(-pan * Math.PI / 2) : 1;

        // cos(π/2) is 6e-17, not 0; an edge must be exact silence.
        return ((float)(Math.Abs(left) < 1e-12 ? 0 : left), (float)(Math.Abs(right) < 1e-12 ? 0 : right));
    }

    public static float DecibelsToGain(double decibels) => decibels <= -120 ? 0f : (float)Math.Pow(10, decibels / 20);
}

/// <summary>A mixer strip's settings in one graph snapshot.</summary>
/// <param name="GainDecibels">Fader gain; −120 dB or less is silence.</param>
/// <param name="Pan">-1 to 1; see <see cref="PanLaw"/>.</param>
/// <param name="Muted">Silences the strip's output.</param>
/// <param name="Soloed">While any strip is soloed, only soloed strips and the strips on their paths sound.</param>
public readonly record struct MixerStripSettings(double GainDecibels = 0, double Pan = 0, bool Muted = false, bool Soloed = false);

/// <summary>
/// The part of a mixer strip that lives across graph snapshots: its meter (post-fader) and the gains last applied,
/// so a gain change between snapshots is ramped over one block instead of clicking.
/// </summary>
public sealed class MixerStripState
{
    private bool _started;
    private float _left;
    private float _right;

    public AudioMeter Meter { get; } = new();

    /// <summary>Applies the gains, ramping from the previous block's; the first block starts at the target.</summary>
    internal void Apply(StereoBuffer block, float left, float right)
    {
        if (!_started)
        {
            _left = left;
            _right = right;
            _started = true;
        }

        Mix.Ramp(block.Left, _left, left);
        Mix.Ramp(block.Right, _right, right);
        _left = left;
        _right = right;
    }
}
