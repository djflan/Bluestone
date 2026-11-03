using System.Collections.Immutable;
using Bluestone.Application.Live;
using Bluestone.Audio;
using Bluestone.Domain.Mixing;

namespace Bluestone.Application.Audio;

/// <summary>The open audio stream as the Audio window shows it.</summary>
/// <param name="SampleRate">The stream's rate.</param>
/// <param name="Channels">Device channels.</param>
/// <param name="BlockFrames">The graph's block size.</param>
/// <param name="QueuedFrames">Frames rendered ahead of the device (0: rendered in the device callback).</param>
/// <param name="RenderThreadPolicy">How the render-ahead thread is scheduled, in words; null in direct mode.</param>
/// <param name="LatencyFrames">The playing graph's delay compensation.</param>
public sealed record AudioStreamInfo(int SampleRate, int Channels, int BlockFrames, int QueuedFrames, string? RenderThreadPolicy, int LatencyFrames);

/// <summary>How the open audio stream has done since it opened.</summary>
/// <param name="Callbacks">Device callbacks.</param>
/// <param name="Xruns">Underruns the device reported.</param>
/// <param name="Discontinuities">Callbacks that came later than the previous block lasted.</param>
/// <param name="LateRenders">Renders longer than their block.</param>
/// <param name="QueueUnderrunFrames">Frames the render-ahead queue could not supply.</param>
/// <param name="RenderFaults">Blocks output as silence because rendering failed.</param>
/// <param name="RenderMax">The longest render.</param>
/// <param name="OutputLatency">From the callback until its audio is heard, as the device says.</param>
/// <param name="PluginFailedBlocks">Plugin blocks that followed the failure policy.</param>
public readonly record struct AudioTelemetry(long Callbacks, long Xruns, long Discontinuities, long LateRenders, long QueueUnderrunFrames, long RenderFaults, TimeSpan RenderMax, TimeSpan OutputLatency, long PluginFailedBlocks);

/// <summary>
/// The app's audio (ADR 0032): the output device, the render graph that follows the MIDI transport, meters, and how the
/// stream is doing. <see cref="AudioPlayback"/> renders in this process; with the engine process (ADR 0044, migration
/// step 6) <c>EngineAudio</c> sends the device choice and the described graph to the engine and mirrors what it reports.
/// The Audio window and the mixer's meters read either through this.
/// </summary>
public interface IAudioPlayback : IDisposable
{
    /// <summary>Raised (on any thread) when the device opens or closes, fails, or the graph's problems change.</summary>
    event EventHandler? Changed;

    /// <summary>Whether a device is open and rendering.</summary>
    bool IsRunning { get; }

    /// <summary>Why the device could not be opened, why it stopped, or that another device stands in for it; null when fine.</summary>
    string? Problem { get; }

    /// <summary>The chosen device is gone and the system default output plays instead until it returns.</summary>
    bool IsOnFallback { get; }

    /// <summary>Audio was started but no device can be opened now; it opens by itself when it can.</summary>
    bool IsWaitingForDevice { get; }

    /// <summary>What the last graph could not play (missing files, silent devices).</summary>
    ImmutableArray<string> GraphProblems { get; }

    /// <summary>A 440 Hz sine at −18 dBFS on both channels, after the master.</summary>
    bool TestTone { get; set; }

    /// <summary>
    /// Whether the engine process renders: the render-ahead queue is then <see cref="AudioSettings.EngineQueuedFrames"/>
    /// (0 by default: rendered in the device callback) instead of <see cref="AudioSettings.QueuedFrames"/>.
    /// </summary>
    bool RendersInEngine { get; }

    /// <summary>The open stream, or null.</summary>
    AudioStreamInfo? Stream { get; }

    /// <summary>How the open stream is doing, or null.</summary>
    AudioTelemetry? Telemetry { get; }

    /// <summary>Measured latency of live notes played into plugin instruments, input timestamp to heard (ADR 0035).</summary>
    LiveLatency LiveLatency { get; }

    /// <summary>Whether the smaller live queue is in use because live input is playing a plugin instrument.</summary>
    bool IsLiveQueue { get; }

    /// <summary>Opens the device (or asks for it) with <paramref name="settings"/>; returns whether one is open.</summary>
    bool Start(AudioSettings settings);

    /// <summary>Closes the device and stops waiting for it.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "AudioPlayback's name; Bluestone is C# only.")]
    void Stop();

    /// <summary>Builds (or describes) the graph for the current project and routing and plays it. Any thread.</summary>
    void Rebuild();

    /// <summary>A mixer channel's meter, or the master's for null; null when audio is off or the channel is not in the graph. Allocates nothing.</summary>
    MeterReading? MeterOf(MixerChannelId? channel);
}
