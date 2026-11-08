namespace Bluestone.Engine.Protocol;

/// <summary>Message types on the control channel. Values are part of the wire format.</summary>
public enum EngineMessageType : ushort
{
    Hello = 1,
    Welcome = 2,
    Ready = 3,
    Heartbeat = 4,
    Ping = 5,
    Pong = 6,
    Shutdown = 7,
    Fault = 8,
    TestCommand = 9,
    OpenOutput = 10,
    CloseOutput = 11,
    SetSlots = 12,
    LoadPlan = 13,
    PlanLoaded = 14,
    TransportPlay = 15,
    TransportStop = 16,
    TransportSeek = 17,
    SetLoop = 18,
    SetMetronome = 19,
    Panic = 20,
    Nudge = 21,
    OutputSend = 22,
    SetRoute = 23,
    RemoveRoute = 24,
    RouteInput = 25,
    SetThru = 26,
    SetInputs = 27,
    TransportStatus = 28,
    TimingStatus = 29,
    MidiBatch = 30,
    EngineProblem = 31,
    SetAudio = 32,
    AudioDeviceStatus = 33,
    LoadAudioGraph = 34,
    AudioGraphLoaded = 35,
    AudioStatus = 36,
    TestRender = 37,
    TestRendered = 38,
    WatchJunctions = 39,
}

/// <summary>A control-channel message (ADR 0044). Nothing here is real-time: commands, state, and telemetry.</summary>
public abstract record EngineMessage
{
    public abstract EngineMessageType Type { get; }
}

/// <summary>
/// Engine → Bluestone, first: who it is. <paramref name="Token"/> is the per-launch token Bluestone passed on the command
/// line; <paramref name="Restarted"/> says this engine replaces one that stopped (it has sent All Notes Off).
/// </summary>
public sealed record Hello(int Version, string Token, int ProcessId, bool Restarted) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Hello;
}

/// <summary>Bluestone → engine: the handshake is accepted.</summary>
public sealed record Welcome(int Version) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Welcome;
}

/// <summary>Engine → Bluestone: started, with its devices open; the transport can be enabled.</summary>
public sealed record Ready : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Ready;
}

/// <summary>
/// Engine → Bluestone, every <see cref="EngineLimits.HeartbeatInterval"/>: alive, and how its process is doing. A pause
/// longer than a millisecond while playing breaks the engine's rules (ADR 0043, 0044), so collections are reported.
/// </summary>
public sealed record Heartbeat(long Sequence, EngineTelemetry Telemetry) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Heartbeat;
}

/// <summary>The engine process's own figures, as of one heartbeat.</summary>
/// <param name="Collections">Garbage collections so far, all generations.</param>
/// <param name="PauseTotal">Total time collections have suspended the process.</param>
/// <param name="AllocatedBytes">Bytes allocated so far, all threads.</param>
/// <param name="WorkingSetBytes">The process's working set.</param>
/// <param name="JitCompiledMethods">Methods compiled at run time so far (0 for an ahead-of-time build).</param>
/// <param name="LastPause">
/// The pause of the latest collection, when one happened since the previous heartbeat (zero otherwise). With several
/// collections in one interval only the latest is seen; <paramref name="PauseTotal"/> counts them all.
/// </param>
public readonly record struct EngineTelemetry(int Collections, TimeSpan PauseTotal, long AllocatedBytes, long WorkingSetBytes, long JitCompiledMethods, TimeSpan LastPause = default);

/// <summary>Bluestone → engine: answer with <see cref="Pong"/> carrying the same id.</summary>
public sealed record Ping(long Id) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Ping;
}

public sealed record Pong(long Id) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Pong;
}

/// <summary>Bluestone → engine: stop now, cleanly. An engine that ends after this has not crashed.</summary>
public sealed record Shutdown : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Shutdown;
}

/// <summary>Engine → Bluestone: something on a real-time thread failed; Bluestone restarts the engine.</summary>
public sealed record Fault(string Message) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Fault;
}

/// <summary>What a test asks an engine to do to itself. Honoured only by an engine started for tests.</summary>
public enum TestAction : byte
{
    /// <summary>End the process abruptly, as a native fault would.</summary>
    Crash = 1,

    /// <summary>Stop sending heartbeats but keep the process running, as a hung engine would.</summary>
    Hang = 2,

    /// <summary>Report a fault on a real-time thread.</summary>
    Fault = 3,
}

/// <summary>Bluestone → engine, tests only: see <see cref="TestAction"/>.</summary>
public sealed record TestCommand(TestAction Action) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TestCommand;
}
