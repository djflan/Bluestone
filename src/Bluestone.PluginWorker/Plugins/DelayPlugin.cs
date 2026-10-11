using Bluestone.Plugins.Protocol;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>
/// <c>reference.delay</c>: an audio effect that puts out its input exactly <see cref="LatencyFrames"/> frames late and
/// reports that as its latency, as a plugin with look-ahead does. Latency (id 0) maps [0, 1] to 0..<see cref="MaxLatency"/>
/// frames. When it is set or restored, the worker tells the host the new latency (a <c>LatencyChanged</c>
/// notification), so delay compensation can be tested sample by sample with a real worker. Needs as many outputs as inputs.
/// </summary>
internal sealed class DelayPlugin : ReferencePlugin
{
    public const string PluginId = "reference.delay";
    public const uint LatencyParameter = 0;
    public const int MaxLatency = 9600;

    private float[][] _rings = [];
    private int _mask;
    private long _written;

    public DelayPlugin()
        : base(
            CreateIdentity(PluginId, "Reference Delay", PluginKind.AudioEffect),
            [new ParameterDescriptor(LatencyParameter, "Latency", 0.0, stepCount: MaxLatency, units: "frames")])
    {
    }

    public override int LatencyFrames => (int)Math.Round(Value(LatencyParameter) * MaxLatency);

    protected override void ValidateChannels(int inputChannels, int outputChannels)
    {
        if (inputChannels < 1 || inputChannels != outputChannels)
        {
            throw new NotSupportedException("Reference Delay needs the same number of input and output channels, at least one.");
        }
    }

    protected override void OnPrepared(int maxFrames)
    {
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(MaxLatency + maxFrames + 1));
        _rings = [.. Enumerable.Range(0, InputChannels).Select(_ => new float[size])];
        _mask = size - 1;
        _written = 0;
    }

    protected override void Render(in ProcessArgs args, int start, int end)
    {
        var latency = LatencyFrames;
        var written = _written;
        for (var c = 0; c < OutputChannels; c++)
        {
            var ring = _rings[c];
            var input = args.Input.Slice(c * args.Frames, args.Frames);
            var output = args.Output.Slice(c * args.Frames, args.Frames);
            for (var i = start; i < end; i++)
            {
                var at = written + (i - start);
                ring[at & _mask] = input[i];
                output[i] = ring[(at - latency) & _mask];
            }
        }

        _written = written + (end - start);
    }
}
