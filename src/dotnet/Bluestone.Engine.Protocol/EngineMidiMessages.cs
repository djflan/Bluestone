using System.Collections.Immutable;

namespace Bluestone.Engine.Protocol;

// MIDI in the engine (migration step 5, ADR 0044). Bluestone tells the engine which outputs to open, which plan to
// play, the transport commands, the live routes it runs, and the inputs to listen to; the engine reports its transport,
// timing, MIDI for endpoints only Bluestone can reach, input, and problems. Transport commands carry an increasing
// sequence number, which the engine echoes in TransportStatus once its playback thread has applied the command, so a
// report sent before a command took effect can be told apart from one sent after. Times are monotonic clock ticks
// (TimeSpan ticks of the system's monotonic clock, which both processes share); song positions are in plan ticks.

/// <summary>
/// Bluestone → engine: open an output under <paramref name="Handle"/>. A <paramref name="Forwarded"/> output is one only
/// Bluestone can reach (its monitor and test buses, its own virtual ports): the engine sends what it plays there back in
/// <see cref="MidiBatch"/>es. <paramref name="Scheduled"/> says a forwarded endpoint takes future timestamps.
/// </summary>
public sealed record OpenOutput(int Handle, string Provider, string Key, string Name, bool Forwarded, bool Scheduled) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.OpenOutput;
}

/// <summary>Bluestone → engine: close the output under <paramref name="Handle"/>.</summary>
public sealed record CloseOutput(int Handle) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.CloseOutput;
}

/// <summary>Bluestone → engine: the output for each slot the plan refers to, by handle; -1 for none.</summary>
public sealed record SetSlots(ImmutableArray<int> Handles) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetSlots;
}

/// <summary>
/// Bluestone → engine: play the plan image in the file at <paramref name="Path"/>. The engine maps it read-only and
/// answers <see cref="PlanLoaded"/> once it has stopped reading every earlier plan, whose files can then be deleted.
/// </summary>
public sealed record LoadPlan(long PlanId, string Path) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.LoadPlan;
}

/// <summary>Engine → Bluestone: <paramref name="PlanId"/> is playing, and no earlier plan's file is in use.</summary>
public sealed record PlanLoaded(long PlanId) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.PlanLoaded;
}

/// <summary>A click of a count-in, <paramref name="Offset"/> ticks of clock time after it starts.</summary>
public readonly record struct CountInClickSpec(long Offset, bool Downbeat);

/// <summary>A count-in: its length in clock ticks and its clicks.</summary>
public sealed record CountInSpec(long Duration, ImmutableArray<CountInClickSpec> Clicks);

/// <summary>Bluestone → engine: play from <paramref name="From"/> (a plan tick; -1 for the playhead), after an optional count-in.</summary>
public sealed record TransportPlay(long From, CountInSpec? CountIn, long Sequence) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TransportPlay;
}

/// <summary>Bluestone → engine: stop playing.</summary>
public sealed record TransportStop(long Sequence) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TransportStop;
}

/// <summary>Bluestone → engine: move the playhead to plan tick <paramref name="Position"/>.</summary>
public sealed record TransportSeek(long Position, long Sequence) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TransportSeek;
}

/// <summary>Bluestone → engine: loop from <paramref name="Start"/> to <paramref name="End"/> (plan ticks), or no loop when <paramref name="Active"/> is false.</summary>
public sealed record SetLoop(bool Active, long Start, long End) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetLoop;
}

/// <summary>The metronome's sound: the slot it clicks on, its channel (0–15), notes and velocities, and each click's length in clock ticks.</summary>
public readonly record struct MetronomeSpec(int Slot, byte Channel, byte AccentNote, byte AccentVelocity, byte BeatNote, byte BeatVelocity, long Length);

/// <summary>Bluestone → engine: the metronome, or none, and whether beats click while playing.</summary>
public sealed record SetMetronome(MetronomeSpec? Click, bool Enabled) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetMetronome;
}

/// <summary>Bluestone → engine: release every sounding note and sweep every output's channels (Bluestone replaces the live routes itself).</summary>
public sealed record Panic : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Panic;
}

/// <summary>Bluestone → engine: move the transport's time mapping by <paramref name="Amount"/> clock ticks (audio alignment).</summary>
public sealed record Nudge(long Amount) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.Nudge;
}

/// <summary>
/// Bluestone → engine: send one MIDI message to the output under <paramref name="Handle"/> now: a channel message
/// through the playback thread (thru and auditioning from routes Bluestone runs), anything else directly.
/// </summary>
public sealed record OutputSend(int Handle, ReadOnlyMemory<byte> Bytes) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.OutputSend;
}

/// <summary>One parameter of a live route's device: its id and stored value.</summary>
public readonly record struct RouteParameter(uint Id, uint Value);

/// <summary>
/// One stage of a live route: a built-in device by definition id with its parameters, or, with an empty
/// <paramref name="Device"/>, a connection's forced <paramref name="Channel"/> (0–15; -1 for a device).
/// </summary>
public sealed record RouteStageSpec(string Device, int Channel, ImmutableArray<RouteParameter> Parameters);

/// <summary>
/// Bluestone → engine: run live route <paramref name="Route"/> for <paramref name="Track"/>: its stages, at
/// <paramref name="Ppqn"/>, to the output under <paramref name="Output"/>. It replaces the track's earlier route for thru.
/// </summary>
public sealed record SetRoute(int Route, Guid Track, int Output, int Ppqn, ImmutableArray<RouteStageSpec> Stages) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetRoute;
}

/// <summary>Bluestone → engine: end live route <paramref name="Route"/>, releasing the notes it sounds.</summary>
public sealed record RemoveRoute(int Route) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.RemoveRoute;
}

/// <summary>Bluestone → engine: play a channel message, received by Bluestone at <paramref name="Timestamp"/>, on live route <paramref name="Route"/>.</summary>
public sealed record RouteInput(int Route, long Timestamp, byte Status, byte Data1, byte Data2) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.RouteInput;
}

/// <summary>Bluestone → engine: the track whose live route input from the engine's inputs plays on (MIDI thru), or none.</summary>
public sealed record SetThru(Guid? Track) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetThru;
}

/// <summary>An input for the engine to open, under <paramref name="Handle"/>.</summary>
public sealed record InputSpec(int Handle, string Provider, string Key, string Name);

/// <summary>Bluestone → engine: listen to these inputs and no others.</summary>
public sealed record SetInputs(ImmutableArray<InputSpec> Inputs) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetInputs;
}

/// <summary>
/// One published tick-to-time mapping: plan tick time <paramref name="SongTime"/> plays at clock time
/// <paramref name="Time"/> under the tempo of plan <paramref name="PlanId"/> (-1 for none), and the loop it wraps at.
/// </summary>
public readonly record struct AnchorSpec(bool Playing, long Time, long SongTime, long PlanId, long LoopStart, long LoopEnd);

/// <summary>
/// Engine → Bluestone, when it changes and every few milliseconds while playing: the transport, as of the transport
/// command with sequence number <paramref name="Applied"/> (0 before any).
/// </summary>
public sealed record TransportStatus(bool Playing, long Position, long LoopWraps, long CountInUntil, AnchorSpec Current, AnchorSpec Previous, long Applied) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TransportStatus;
}

/// <summary>A non-empty bucket of the fine lateness histogram.</summary>
public readonly record struct FineBucket(int Index, long Count);

/// <summary>Engine → Bluestone, now and then: the playback thread's dispatch timing (clock ticks).</summary>
public sealed record TimingStatus(long Dispatched, long Late, long SkippedLateNotes, long Dropped, long NoteOverflow, long MaxLateness, ImmutableArray<long> Histogram, ImmutableArray<FineBucket> Fine) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TimingStatus;
}

/// <summary>What a <see cref="MidiEvent"/> is.</summary>
public enum MidiEventKind : byte
{
    /// <summary>Played by the engine to a forwarded output: Bluestone sends it there.</summary>
    Output = 1,

    /// <summary>Received on one of the engine's inputs: Bluestone records it and shows it.</summary>
    Input = 2,
}

/// <summary>One MIDI message for a handle, with its clock time (0 for at once).</summary>
public readonly record struct MidiEvent(MidiEventKind Kind, int Handle, long Timestamp, ReadOnlyMemory<byte> Bytes);

/// <summary>Engine → Bluestone: MIDI for forwarded outputs, and input, in the order it happened.</summary>
public sealed record MidiBatch(ImmutableArray<MidiEvent> Events) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.MidiBatch;
}

/// <summary>Engine → Bluestone: something the engine could not do (open a device, read a plan), about <paramref name="Handle"/> or -1.</summary>
public sealed record EngineProblem(int Handle, string Message) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.EngineProblem;
}
