using Bluestone.Domain.Devices;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;
using Bluestone.Plugins;

namespace Bluestone.Application.Live;

/// <summary>
/// Where live routes run when they do not run in Bluestone's process: the engine process (ADR 0044, migration steps 5
/// and 7) runs routes through built-in live devices, forced channels, and plugin MIDI effects, to external instruments it
/// reaches or into plugin instruments of its audio graph.
/// </summary>
internal interface ILiveRemote
{
    /// <summary>Whether the engine can run <paramref name="plan"/>, playing to <paramref name="output"/> (null for a plugin instrument).</summary>
    bool CanRun(LiveRoutePlan plan, IMidiOutput? output);

    /// <summary>Starts running <paramref name="plan"/> in the engine, with the plugin instances of its effects; returns its route id.</summary>
    int Add(LiveRoutePlan plan, IMidiOutput? output, Ppqn ppqn, Func<DeviceId, PluginInstance?> plugins);

    /// <summary>Ends a route started by <see cref="Add"/>, releasing its notes.</summary>
    void Remove(int route);

    /// <summary>Plays input received by Bluestone on a route the engine runs.</summary>
    void Post(int route, ChannelMessage message, TimeSpan timestamp);

    /// <summary>Whether input from <paramref name="input"/> reaches Bluestone through the engine, which has already played it on the thru route.</summary>
    bool IsEngineInput(EndpointDescriptor input);
}
