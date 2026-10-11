using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Signal;

/// <summary>What the chain runner gives an <see cref="IOutOfProcessRenderer"/> for one device and one block.</summary>
/// <param name="Device">The device, as the project stores it (state and parameter values included).</param>
/// <param name="Definition">Its definition.</param>
/// <param name="Block">The stretch of timeline.</param>
/// <param name="Tempo">The sequence's tempo map, for turning positions into time.</param>
/// <param name="Ppqn">The sequence's resolution.</param>
public readonly record struct OutOfProcessBlock(DeviceInstance Device, DeviceDefinition Definition, SignalBlock Block, TempoMap Tempo, Ppqn Ppqn);

/// <summary>
/// What an out-of-process render produced. <see cref="Events"/> is null when the device could not run (its worker is
/// gone, the plugin failed, the request was too large): the chain then passes the input on unchanged. The messages
/// are reported either way.
/// </summary>
public sealed record OutOfProcessResult(ImmutableArray<SignalEvent>? Events, ImmutableArray<string> Messages)
{
    public static OutOfProcessResult Failed(string message) => new(null, [message]);
}

/// <summary>
/// Runs event devices that cannot run in Bluestone's process, such as plugin MIDI effects, at plan-compile time
/// (ADR 0025). The implementation lives outside this project (the plugin bridge); it may block and do I/O, so it is
/// only used when a chain is run for the plan, never by a real-time runner. Each call stands alone: the device starts
/// from its stored state, so plan compilation, which runs the whole timeline as one block, gets a coherent result.
/// </summary>
public interface IOutOfProcessRenderer
{
    /// <summary>Whether this renderer runs <paramref name="device"/>. Devices it does not run pass events through, reported.</summary>
    bool CanRender(DeviceInstance device, DeviceDefinition definition);

    /// <summary>
    /// Runs the device over <paramref name="block"/>: <paramref name="input"/> holds the events it handles (canonical order),
    /// and <paramref name="changes"/> its own parameter changes (position order). Output events keep the key of an input
    /// event (<see cref="SignalEvent.With"/>) so merging stays deterministic. Never throws for a device failure.
    /// </summary>
    OutOfProcessResult Render(in OutOfProcessBlock block, ReadOnlySpan<SignalEvent> input, ReadOnlySpan<ParameterChange> changes);
}
