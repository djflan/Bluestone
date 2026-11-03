using Bluestone.Engine.Protocol;

namespace Bluestone.Application.Engine;

/// <summary>The control channel to one running engine, for an <see cref="IEngineClient"/>.</summary>
public interface IEngineLink
{
    /// <summary>Queues a message for the engine; messages go out in the order they are queued. Never blocks.</summary>
    /// <returns>False when this engine has stopped.</returns>
    bool Send(EngineMessage message);
}

/// <summary>What uses the engine through its <see cref="EngineSupervisor"/>: MIDI playback (<see cref="EnginePlayback"/>).</summary>
public interface IEngineClient
{
    /// <summary>
    /// A new engine finished its handshake (<paramref name="restarted"/> after a stop) and is about to be reported Ready.
    /// Called on the launching thread; queue everything the engine needs, before anything else is sent.
    /// </summary>
    void Connected(IEngineLink link, bool restarted);

    /// <summary>A message for the client from the engine behind <paramref name="link"/>, on the channel's reading thread.</summary>
    void Received(IEngineLink link, EngineMessage message);

    /// <summary>The engine behind <paramref name="link"/> stopped, or is being shut down.</summary>
    void Disconnected(IEngineLink link);
}
