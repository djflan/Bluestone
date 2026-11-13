using System.Collections.Immutable;

namespace Bluestone.Engine.Protocol;

// Audio in the engine (migration step 6, ADR 0044). Bluestone chooses the device and settings; the engine opens it,
// renders in its callback, and reports how it went. Graphs cross as audio graph image files, which the engine reads and
// realizes off the real-time thread; it acknowledges each one so older files can be deleted. Meters and the stream's
// figures come back regularly while a device is open.

/// <summary>Bluestone → engine: the audio output to open, or none. Sent again whenever anything here changes.</summary>
/// <param name="Request">Increases with every message; the engine echoes it in <see cref="AudioDeviceStatus"/>.</param>
/// <param name="Enabled">Open a device; false closes it and stops waiting for it.</param>
/// <param name="DeviceId">The device, or null for the selected host's default output.</param>
/// <param name="SampleRate">In Hz; 0 for the device's own.</param>
/// <param name="BufferFrames">Frames per callback; 0 for the device or ASIO driver's default.</param>
/// <param name="QueuedFrames">Frames rendered ahead of the device (0: render in the device callback).</param>
/// <param name="MaxBlockFrames">The largest block plugins accept; the engine renders blocks no larger.</param>
/// <param name="TestTone">A 440 Hz tone at −18 dBFS after the master.</param>
/// <param name="Host">The selected audio host, or null for the system default.</param>
/// <param name="LiveQueuedFrames">
/// The smaller render-ahead queue used while live input plays a plugin instrument, when <paramref name="QueuedFrames"/>
/// is above 0; 0 keeps <paramref name="QueuedFrames"/> (ADR 0035).
/// </param>
public sealed record SetAudio(long Request, bool Enabled, string? DeviceId, int SampleRate, int BufferFrames, int QueuedFrames, int MaxBlockFrames, bool TestTone, int LiveQueuedFrames = 0, string? Host = null) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.SetAudio;
}

/// <summary>Where the engine's audio output stands.</summary>
public enum AudioDeviceState : byte
{
    /// <summary>No device is wanted, or none could be opened and the engine is not waiting.</summary>
    Off = 0,

    /// <summary>The chosen device is open and rendering.</summary>
    Running = 1,

    /// <summary>The chosen device is not available; the system default output plays until it returns.</summary>
    Fallback = 2,

    /// <summary>No device can be opened now; the engine opens one as soon as it can.</summary>
    Waiting = 3,
}

/// <summary>
/// Engine → Bluestone, when it changes: the audio output, as of <see cref="SetAudio"/> number <paramref name="Request"/>.
/// The figures describe the open stream (zero when none is open); <paramref name="Problem"/> says why the chosen device is
/// not the one playing. A graph must be described at this rate and block size to play.
/// </summary>
/// <param name="Request">The latest audio request sent by Bluestone.</param>
/// <param name="State">Whether the engine has opened or is waiting for a device.</param>
/// <param name="Problem">Why the requested output is unavailable or has fallen back, if applicable.</param>
/// <param name="SampleRate">The opened stream's sample rate.</param>
/// <param name="Channels">The opened stream's channel count.</param>
/// <param name="BlockFrames">The graph block size.</param>
/// <param name="QueuedFrames">The current render-ahead queue.</param>
/// <param name="RenderThreadPolicy">The render thread policy, if applicable.</param>
/// <param name="DeviceBufferFrames">The device callback size, as reported by the audio backend (0 when none is open).</param>
public sealed record AudioDeviceStatus(long Request, AudioDeviceState State, string? Problem, int SampleRate, int Channels, int BlockFrames, int QueuedFrames, string? RenderThreadPolicy, int DeviceBufferFrames = 0) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.AudioDeviceStatus;
}

/// <summary>
/// Bluestone → engine: play the audio graph image in the file at <paramref name="Path"/>, described for
/// <paramref name="SampleRate"/> and <paramref name="BlockFrames"/>. The engine reads and realizes it off the real-time
/// thread, and answers <see cref="AudioGraphLoaded"/>; a graph for another rate or block size is not played.
/// </summary>
public sealed record LoadAudioGraph(long GraphId, string Path, int SampleRate, int BlockFrames) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.LoadAudioGraph;
}

/// <summary>
/// Engine → Bluestone: graph <paramref name="GraphId"/> was read; <paramref name="Applied"/> when it is the one playing.
/// Its file and every earlier one can be deleted. <paramref name="Problems"/> are what it could not play (files that could
/// not be opened, plugin exchanges it could not attach to); <paramref name="LatencyFrames"/> is its delay compensation.
/// </summary>
public sealed record AudioGraphLoaded(long GraphId, bool Applied, int LatencyFrames, ImmutableArray<string> Problems) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.AudioGraphLoaded;
}

/// <summary>A meter's reading: linear peak and RMS levels per side.</summary>
public readonly record struct MeterSpec(float PeakLeft, float PeakRight, float RmsLeft, float RmsRight);

/// <summary>A mixer strip's meter, by its channel's key.</summary>
public readonly record struct StripMeter(Guid Key, MeterSpec Levels);

/// <summary>How the audio stream has been doing since it opened.</summary>
/// <param name="Callbacks">Device callbacks so far.</param>
/// <param name="Xruns">Underruns the device or its driver reported.</param>
/// <param name="Discontinuities">Callbacks that came later than the previous block lasted.</param>
/// <param name="LateRenders">Renders that took longer than their block lasts.</param>
/// <param name="QueueUnderrunFrames">Frames the render-ahead queue could not supply (0 in direct mode).</param>
/// <param name="RenderFaults">Blocks output as silence because rendering failed.</param>
/// <param name="RenderMax">The longest render, in clock ticks.</param>
/// <param name="OutputLatency">From the callback until its audio is heard, as the device says, in clock ticks.</param>
/// <param name="PluginFailedBlocks">Plugin blocks that followed the failure policy (late, failed, or unavailable).</param>
/// <param name="LatencyFrames">The playing graph's delay compensation.</param>
/// <param name="QueuedFrames">Frames rendered ahead now (0: in the device callback).</param>
/// <param name="LiveQueue">The smaller live queue is in use.</param>
/// <param name="CallbackThreadPolicy">How the device callback thread is scheduled: an <c>AudioCallbackThreadPolicy</c> value.</param>
public readonly record struct AudioCounters(long Callbacks, long Xruns, long Discontinuities, long LateRenders, long QueueUnderrunFrames, long RenderFaults, long RenderMax, long OutputLatency, long PluginFailedBlocks, int LatencyFrames, int QueuedFrames = 0, bool LiveQueue = false, int CallbackThreadPolicy = 0);

/// <summary>
/// Live notes into plugin instruments, from their input timestamp until heard (ADR 0035), measured in the engine: how
/// many, and their average, shortest, and longest latency in clock ticks. <paramref name="DeadlineMisses"/> counts
/// live-played instruments' blocks that missed their depth-1 deadline (ADR 0043 decision 6).
/// </summary>
public readonly record struct LiveLatencySpec(long Notes, long Average, long Min, long Max, long DeadlineMisses);

/// <summary>Engine → Bluestone, about 30 times a second while a device is open: the meters and the stream's figures.</summary>
public sealed record AudioStatus(MeterSpec Master, ImmutableArray<StripMeter> Strips, AudioCounters Counters, LiveLatencySpec Live = default) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.AudioStatus;

    /// <summary>The watched chain junctions' meters (ADR 0037), by junction key, for those the playing graph has.</summary>
    public ImmutableArray<StripMeter> Junctions { get; init; } = [];
}

/// <summary>
/// Bluestone → engine: measure these chain junctions (a chain's ID for its input, a device's for the point after it),
/// replacing the previous set, until the next such message. Junctions not named cost nothing to render.
/// </summary>
public sealed record WatchJunctions(ImmutableArray<Guid> Keys) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.WatchJunctions;
}

/// <summary>
/// Bluestone → engine, tests only (and only with an engine started for tests, whose audio device is a stand-in that does
/// not call back): play the current graph from the song's start for <paramref name="Frames"/>, in callbacks of
/// <paramref name="CallbackFrames"/>, through the render path a device callback uses, and write the interleaved stereo
/// samples to <paramref name="Path"/> as 32-bit floats.
/// </summary>
/// <param name="Id">Echoed in <see cref="TestRendered"/>.</param>
/// <param name="Path">Where the samples go.</param>
/// <param name="Frames">How many frames.</param>
/// <param name="CallbackFrames">Frames per callback.</param>
/// <param name="Notes">Live input to play on the way, as input on a track's route would arrive (see <see cref="TestNote"/>).</param>
public sealed record TestRender(long Id, string Path, int Frames, int CallbackFrames, ImmutableArray<TestNote> Notes = default) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TestRender;
}

/// <summary>
/// A channel message posted on <paramref name="Track"/>'s live route just before the callback that renders frame
/// <paramref name="Frame"/>, with an input timestamp <paramref name="Lead"/> clock ticks before that callback's first
/// frame is heard.
/// </summary>
public readonly record struct TestNote(Guid Track, int Frame, long Lead, byte Status, byte Data1, byte Data2);

/// <summary>Engine → Bluestone, tests only: <see cref="TestRender"/> <paramref name="Id"/> is done, or why it failed.</summary>
public sealed record TestRendered(long Id, string? Problem) : EngineMessage
{
    public override EngineMessageType Type => EngineMessageType.TestRendered;
}
