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
/// so a gain change between snapshots is ramped over one block instead of clicking. The same for each of its sends.
/// </summary>
public sealed class MixerStripState
{
    private readonly Dictionary<object, GainRamp> _sends = [];
    private readonly GainRamp _gain = new();
    private bool _muteStarted;
    private float _muteLevel;
    private bool _levelStarted;
    private double _lastGainLevel;
    private double _lastPan;

    public MixerMeter Meter { get; } = new();

    /// <summary>Applies the gains, ramping from the previous block's; the first block starts at the target.</summary>
    internal void Apply(StereoBuffer block, float left, float right) => _gain.Apply(block, left, right);

    /// <summary>Applies sample-timed fader and pan automation without allocating or taking a lock.</summary>
    internal void ApplyAutomated(
        StereoBuffer block,
        in AudioRenderContext context,
        double gainDecibels,
        double pan,
        bool unmuted,
        DescribedAutomationPoint[] gainAutomation,
        DescribedAutomationPoint[] panAutomation)
    {
        if (gainAutomation.Length == 0 && panAutomation.Length == 0)
        {
            var faderGain = PanLaw.DecibelsToGain(gainDecibels);
            var gain = unmuted ? faderGain : 0f;
            var (left, right) = PanLaw.Gains(pan);
            _gain.Apply(block, gain * left, gain * right);
            _muteLevel = unmuted ? 1f : 0f;
            _muteStarted = true;
            RememberLevel(faderGain, pan);
            return;
        }

        // The parameter without automation follows the manual control; ramp it across the block from the value last
        // applied (automated or manual) so a fader or pan move doesn't jump while the other parameter is automated.
        var manualGain = (double)PanLaw.DecibelsToGain(gainDecibels);
        var startGain = _levelStarted ? _lastGainLevel : manualGain;
        var startPan = _levelStarted ? _lastPan : pan;
        var lastGain = startGain;
        var lastPan = startPan;
        var targetMute = unmuted ? 1f : 0f;
        var startMute = _muteStarted ? _muteLevel : targetMute;
        var muteStep = (targetMute - startMute) / block.Frames;
        var gainIndex = 0;
        var panIndex = 0;
        var finalLeft = 0f;
        var finalRight = 0f;
        for (var i = 0; i < block.Frames; i++)
        {
            if (context.Wraps && i == context.WrapOffset)
            {
                gainIndex = 0;
                panIndex = 0;
            }

            var frame = context.Wraps && i >= context.WrapOffset
                ? context.WrapFrame + i - context.WrapOffset
                : context.SongFrame + i;
            var progress = (double)(i + 1) / block.Frames;
            lastGain = gainAutomation.Length == 0
                ? startGain + ((manualGain - startGain) * progress)
                : PanLaw.DecibelsToGain(ValueAt(gainAutomation, frame, gainDecibels, ref gainIndex));
            lastPan = panAutomation.Length == 0
                ? startPan + ((pan - startPan) * progress)
                : ValueAt(panAutomation, frame, pan, ref panIndex);
            var level = (float)lastGain * (startMute + ((i + 1) * muteStep));
            var (left, right) = PanLaw.Gains(lastPan);
            finalLeft = level * left;
            finalRight = level * right;
            block.Left[i] *= finalLeft;
            block.Right[i] *= finalRight;
        }

        if (block.Frames > 0)
        {
            _gain.SetCurrent(finalLeft, finalRight);
            RememberLevel(lastGain, lastPan);
        }

        _muteLevel = targetMute;
        _muteStarted = true;
    }

    private void RememberLevel(double gain, double pan)
    {
        _lastGainLevel = gain;
        _lastPan = pan;
        _levelStarted = true;
    }

    /// <summary>The ramp state of this strip's send to <paramref name="target"/>. Control thread (graph building).</summary>
    internal GainRamp SendTo(object target)
    {
        lock (_sends)
        {
            if (!_sends.TryGetValue(target, out var ramp))
            {
                ramp = new GainRamp();
                _sends[target] = ramp;
            }

            return ramp;
        }
    }

    /// <summary>Forgets the ramp state of sends that are gone. Control thread; graphs already built keep theirs.</summary>
    internal void KeepSendsTo(IReadOnlySet<object> targets)
    {
        lock (_sends)
        {
            foreach (var gone in _sends.Keys.Where(t => !targets.Contains(t)).ToList())
            {
                _sends.Remove(gone);
            }
        }
    }

    private static double ValueAt(DescribedAutomationPoint[] points, long frame, double fallback, ref int index)
    {
        if (points.Length == 0)
        {
            return fallback;
        }

        if (frame < points[0].Frame)
        {
            return points[0].Value;
        }

        if (index + 1 < points.Length && points[index + 1].Frame <= frame)
        {
            var lower = index + 1;
            var upper = points.Length - 1;
            while (lower < upper)
            {
                var middle = lower + ((upper - lower + 1) / 2);
                if (points[middle].Frame <= frame)
                {
                    lower = middle;
                }
                else
                {
                    upper = middle - 1;
                }
            }

            index = lower;
        }

        var current = points[index];
        if (!current.Linear || index + 1 == points.Length || points[index + 1].Frame <= current.Frame)
        {
            return current.Value;
        }

        var next = points[index + 1];
        if (frame >= next.Frame)
        {
            return next.Value;
        }

        var amount = (frame - current.Frame) / (double)(next.Frame - current.Frame);
        return current.Value + ((next.Value - current.Value) * amount);
    }
}

/// <summary>A stereo gain that moves from the last block's value to the new one across a block. Audio thread.</summary>
internal sealed class GainRamp
{
    private bool _started;
    private float _left;
    private float _right;

    /// <summary>Multiplies <paramref name="block"/> in place.</summary>
    public void Apply(StereoBuffer block, float left, float right)
    {
        Start(left, right);
        Mix.Ramp(block.Left, _left, left);
        Mix.Ramp(block.Right, _right, right);
        _left = left;
        _right = right;
    }

    /// <summary>Adds <paramref name="source"/> at the gains to <paramref name="destination"/>.</summary>
    public void AddTo(StereoBuffer source, StereoBuffer destination, float left, float right)
    {
        Start(left, right);
        Mix.AddRamped(source.Left, destination.Left, _left, left);
        Mix.AddRamped(source.Right, destination.Right, _right, right);
        _left = left;
        _right = right;
    }

    public void SetCurrent(float left, float right)
    {
        _left = left;
        _right = right;
        _started = true;
    }

    private void Start(float left, float right)
    {
        if (!_started)
        {
            _left = left;
            _right = right;
            _started = true;
        }
    }
}
