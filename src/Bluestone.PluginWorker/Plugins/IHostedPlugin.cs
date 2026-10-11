using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>
/// A plugin as the worker hosts it: a built-in reference plugin, or a VST3 or CLAP plugin in the native plugin host
/// (<see cref="Native.NativePlugin"/>). Calls are serialized by the caller: processing and control never overlap.
/// </summary>
internal interface IHostedPlugin
{
    PluginIdentity Identity { get; }

    ImmutableArray<ParameterDescriptor> Parameters { get; }

    /// <summary>Latency the plugin itself adds, in sample frames.</summary>
    int LatencyFrames { get; }

    /// <summary>Configures processing. Throws <see cref="NotSupportedException"/> for a channel layout the plugin cannot run.</summary>
    void Prepare(double sampleRate, int maxFrames, int inputChannels, int outputChannels);

    double GetParameter(uint id);

    /// <summary>Sets a normalized value; false for an unknown id or a value outside [0, 1].</summary>
    bool TrySetParameter(uint id, double value);

    void Process(in ProcessArgs args, OutputEventList output);

    PluginStateData SaveState();

    /// <summary>
    /// Loads state. Corrupt or unknown-version state throws <see cref="InvalidDataException"/> and leaves the plugin
    /// at its default parameter values, still usable.
    /// </summary>
    void LoadState(PluginStateData state);
}

/// <summary>
/// One block. Audio is planar: channel c occupies <c>[c * Frames, (c + 1) * Frames)</c>. Events and parameter changes
/// are sorted by sample offset.
/// </summary>
internal readonly ref struct ProcessArgs
{
    public ProcessArgs(int frames, ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<PluginEvent> events, ReadOnlySpan<ParameterChange> parameterChanges, TransportState transport)
    {
        Frames = frames;
        Input = input;
        Output = output;
        Events = events;
        ParameterChanges = parameterChanges;
        Transport = transport;
    }

    public int Frames { get; }

    public ReadOnlySpan<float> Input { get; }

    public Span<float> Output { get; }

    public ReadOnlySpan<PluginEvent> Events { get; }

    public ReadOnlySpan<ParameterChange> ParameterChanges { get; }

    public TransportState Transport { get; }
}

/// <summary>Preallocated output events of one block. Events beyond capacity are counted, not stored.</summary>
internal sealed class OutputEventList
{
    private readonly PluginEvent[] _events;

    public OutputEventList(int capacity) => _events = new PluginEvent[capacity];

    public int Count { get; private set; }

    public int Capacity => _events.Length;

    public int Dropped { get; private set; }

    public ReadOnlySpan<PluginEvent> Events => _events.AsSpan(0, Count);

    public void Add(in PluginEvent e)
    {
        if (Count < _events.Length)
        {
            _events[Count++] = e;
        }
        else
        {
            Dropped++;
        }
    }

    public void Clear()
    {
        Count = 0;
        Dropped = 0;
    }
}
