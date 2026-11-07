namespace Bluestone.Audio;

/// <summary>
/// Its input at a fixed gain: an Audio Receiver's level for what it brings into a chain (ADR 0037). The realizer keeps one
/// per receiver across graphs, so the delay lines on its edges keep their audio; a gain change ramps over one block.
/// </summary>
public sealed class GainNode : AudioNode
{
    private float _gain;
    private float _target;

    public GainNode(string name, float gain)
    {
        Name = name;
        _gain = gain;
        _target = gain;
    }

    public override string Name { get; }

    /// <summary>The linear gain to reach by the end of the next block. Control thread; read on the audio thread.</summary>
    public float Gain
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, value);
    }

    public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
    {
        var from = _gain;
        var to = Volatile.Read(ref _target);
        var frames = output.Left.Length;
        var step = frames > 0 ? (to - from) / frames : 0;
        for (var i = 0; i < frames; i++)
        {
            var gain = from + (step * (i + 1));
            output.Left[i] = input.Left[i] * gain;
            output.Right[i] = input.Right[i] * gain;
        }

        _gain = to;
    }
}
