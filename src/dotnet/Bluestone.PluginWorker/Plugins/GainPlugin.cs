using Bluestone.Plugins.Protocol;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>
/// <c>reference.gain</c>: an audio effect. Gain (id 0) maps [0, 1] linearly to x0..x2, so the default 0.5 is unity
/// and 0.25 is exactly x0.5. Invert (id 1) flips polarity at 0.5 and above. Needs as many outputs as inputs.
/// </summary>
internal sealed class GainPlugin : ReferencePlugin
{
    public const string PluginId = "reference.gain";
    public const uint GainParameter = 0;
    public const uint InvertParameter = 1;

    public GainPlugin()
        : base(
            CreateIdentity(PluginId, "Reference Gain", PluginKind.AudioEffect),
            [new ParameterDescriptor(GainParameter, "Gain", 0.5), new ParameterDescriptor(InvertParameter, "Invert", 0.0, stepCount: 1)])
    {
    }

    protected override void ValidateChannels(int inputChannels, int outputChannels)
    {
        if (inputChannels < 1 || inputChannels != outputChannels)
        {
            throw new NotSupportedException("Reference Gain needs the same number of input and output channels, at least one.");
        }
    }

    protected override void Render(in ProcessArgs args, int start, int end)
    {
        var gain = (float)(Value(GainParameter) * 2.0);
        if (Value(InvertParameter) >= 0.5)
        {
            gain = -gain;
        }

        for (var c = 0; c < OutputChannels; c++)
        {
            var input = args.Input.Slice((c * args.Frames) + start, end - start);
            var output = args.Output.Slice((c * args.Frames) + start, end - start);
            for (var i = 0; i < input.Length; i++)
            {
                output[i] = input[i] * gain;
            }
        }
    }
}
