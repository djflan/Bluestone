using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Wire;

namespace Bluestone.Application.Live;

/// <summary>
/// Where live routes run when they do not run in Bluestone's process: the engine process (ADR 0044, migration step 5)
/// runs routes to external instruments whose stages are all built-in live devices or forced channels.
/// </summary>
internal interface ILiveRemote
{
    /// <summary>Whether the engine can run <paramref name="plan"/>, playing to <paramref name="output"/>.</summary>
    bool CanRun(LiveRoutePlan plan, IMidiOutput output);

    /// <summary>Starts running <paramref name="plan"/> in the engine; returns its route id.</summary>
    int Add(LiveRoutePlan plan, IMidiOutput output, Ppqn ppqn);

    /// <summary>Ends a route started by <see cref="Add"/>, releasing its notes.</summary>
    void Remove(int route);

    /// <summary>Plays input received by Bluestone on a route the engine runs.</summary>
    void Post(int route, ChannelMessage message, TimeSpan timestamp);

    /// <summary>Whether input from <paramref name="input"/> reaches Bluestone through the engine, which has already played it on the thru route.</summary>
    bool IsEngineInput(EndpointDescriptor input);
}
