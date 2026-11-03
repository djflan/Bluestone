using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Plugins;

/// <summary>
/// What the audio graph needs of a plugin: its block shape, its latency, and the data plane. A
/// <see cref="PluginInstance"/> in the process that supervises the worker, or an <see cref="AttachedPluginInstance"/> in
/// the engine process (ADR 0044). <see cref="ProcessBlock"/> and <see cref="QueueParameterChange"/> are called by one audio
/// thread and never lock, allocate, do I/O, or throw for a plugin failure.
/// </summary>
public interface IPluginProcessor
{
    int MaxBlockFrames { get; }

    int InputChannels { get; }

    int OutputChannels { get; }

    /// <summary>Events per block in each direction.</summary>
    int EventCapacity { get; }

    /// <summary>Pipelining plus the plugin's own latency, in frames.</summary>
    int ReportedLatencyFrames { get; }

    /// <summary>Raised (on any thread) when the plugin reports something itself, such as a new latency.</summary>
    event EventHandler<NotificationKind>? Changed;

    /// <summary>See <see cref="PluginInstance.ProcessBlock"/>.</summary>
    BlockOutcome ProcessBlock(int frames, ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<PluginEvent> inputEvents, Span<PluginEvent> outputEvents, in TransportState transport);

    /// <summary>See <see cref="PluginInstance.QueueParameterChange"/>.</summary>
    bool QueueParameterChange(uint parameterId, double normalizedValue, int sampleOffset);
}

/// <summary>
/// A running instance's block exchange as another process attaches to it: the file the worker created, its generation,
/// and its shape (<see cref="HostBlockExchange.Open"/>).
/// </summary>
public sealed record PluginDataPlane(string Path, uint Generation, ExchangeOptions Options);
