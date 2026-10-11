using System.Collections.Immutable;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using Bluestone.Domain.Devices;
using Bluestone.Plugins;

namespace Bluestone.Application.Audio;

/// <summary>
/// Runs the audio engine for the app (ADR 0032): opens the chosen output device, follows the MIDI playback engine's
/// transport so audio and MIDI play the same song position at the same moment, rebuilds the render graph after every
/// refresh of the playback controller, and keeps the transport aligned with the device's clock. While no device is
/// open nothing here touches MIDI playback.
/// </summary>
/// <remarks>Start, Stop, and Dispose are for one control thread. Graph rebuilds run on the thread pool, one at a time.</remarks>
public sealed class AudioPlayback : IDisposable
{
    private static readonly TimeSpan AlignInterval = TimeSpan.FromSeconds(1);
    private readonly PlaybackController _playback;
    private readonly ProjectSession _session;
    private readonly Func<DeviceId, PluginInstance?> _instances;
    private readonly int _maxBlockFrames;
    private readonly Lock _gate = new();
    private readonly EngineSlot _slot = new();
    private AudioEngine? _engine;
    private AudioOutput? _output;
    private ProjectAudioGraph? _graph;
    private Timer? _align;
    private bool _testTone;

    /// <param name="playback">The MIDI playback controller whose transport the audio follows.</param>
    /// <param name="session">The project.</param>
    /// <param name="instances">The running plugin instance of a device, if any.</param>
    /// <param name="maxBlockFrames">The block size plugin instances accept; the engine renders blocks no larger.</param>
    public AudioPlayback(PlaybackController playback, ProjectSession session, Func<DeviceId, PluginInstance?> instances, int maxBlockFrames = 256)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _maxBlockFrames = maxBlockFrames;
        _playback.Refreshed += (_, _) => _ = Task.Run(Rebuild);
    }

    /// <summary>Raised (on any thread) when the device opens or closes, fails, or the graph's problems change.</summary>
    public event EventHandler? Changed;

    public bool IsRunning => _output is not null;

    /// <summary>Why the device could not be opened, or why it stopped; null when fine.</summary>
    public string? Problem { get; private set; }

    /// <summary>What the last graph could not play (missing files, silent devices).</summary>
    public ImmutableArray<string> GraphProblems { get; private set; } = [];

    public AudioEngine? Engine => _engine;

    public AudioOutput? Output => _output;

    /// <summary>Meter and strip state, for a mixer view.</summary>
    public ProjectAudioGraph? Graph => _graph;

    /// <summary>A 440 Hz sine at −18 dBFS on both channels, after the master.</summary>
    public bool TestTone
    {
        get => _testTone;
        set
        {
            _testTone = value;
            if (_engine is { } engine)
            {
                engine.TestTone = value ? new ToneNode(440, 0.125) : null;
            }
        }
    }

    /// <summary>Opens the device and starts the engine; on failure <see cref="Problem"/> says why and nothing runs.</summary>
    public bool Start(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Stop();
        AudioOutput output;
        var request = new AudioStreamRequest(settings.DeviceId, settings.SampleRate, settings.BufferFrames, QueuedFrames: settings.QueuedFrames);
        try
        {
            output = AudioOutput.Open(_slot, request);
        }
        catch (AudioDeviceException ex) when (settings.SampleRate != 0)
        {
            // The device may not offer the rate; its own rate is better than no sound.
            try
            {
                output = AudioOutput.Open(_slot, request with { SampleRate = 0 });
            }
            catch (AudioDeviceException)
            {
                return Fail(ex.Message);
            }
        }
        catch (AudioDeviceException ex)
        {
            return Fail(ex.Message);
        }

        var rate = output.SampleRate;
        var block = Math.Clamp(settings.BufferFrames, 32, _maxBlockFrames);
        var engine = new AudioEngine(rate, block) { TestTone = _testTone ? new ToneNode(440, 0.125) : null };
        var device = Math.Max(settings.BufferFrames, block);
        engine.Transport.Follow(new PlaybackTimeline(_playback.Engine), toleranceFrames: Math.Max(rate / 100, 2 * (device + block)));
        lock (_gate)
        {
            _engine = engine;
            _graph = new ProjectAudioGraph(rate, block, _instances);
        }

        Rebuild();
        _slot.Engine = engine;
        try
        {
            output.Start();
        }
        catch (AudioDeviceException ex)
        {
            output.Dispose();
            Stop();
            return Fail(ex.Message);
        }

        _output = output;
        Problem = null;
        _align = new Timer(_ => Align(), null, AlignInterval, AlignInterval);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Closes the device. MIDI playback carries on alone.</summary>
    public void Stop()
    {
        _align?.Dispose();
        _align = null;
        _output?.Dispose();
        _output = null;
        _slot.Engine = null;
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
            _graph?.Dispose();
            _graph = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds a graph for the current project and routing and swaps it in. Any thread.</summary>
    public void Rebuild()
    {
        lock (_gate)
        {
            if (_engine is not { } engine || _graph is not { } graph)
            {
                return;
            }

            if (_playback.Routing is not { } routing)
            {
                engine.Publish(AudioGraph.Silent(engine.BlockFrames));
                return;
            }

            var directory = _session.FilePath is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
            var result = graph.Build(_session.Project, routing.Graph, _playback.Devices, directory);
            engine.Publish(result.Graph);
            var changed = !result.Problems.SequenceEqual(GraphProblems);
            GraphProblems = result.Problems;
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Dispose() => Stop();

    // Moves the MIDI transport towards the audio by half the measured difference, and notices a lost device.
    private void Align()
    {
        if (_engine is not { } engine || _output is not { } output)
        {
            return;
        }

        var (average, callbacks) = engine.Transport.TakeDrift();
        if (callbacks >= 10 && Math.Abs(average) >= 1)
        {
            _playback.Engine.Nudge(TimeSpan.FromSeconds(average / 2 / engine.SampleRate));
        }

        if (output.Status.State == AudioStreamState.Failed)
        {
            Problem = "The audio device stopped (it may have been disconnected). Choose a device and start again.";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Fail(string message)
    {
        Problem = message;
        Changed?.Invoke(this, EventArgs.Empty);
        return false;
    }

    // The renderer the device holds; the engine behind it is set once the device's rate is known.
    private sealed class EngineSlot : IAudioRenderer
    {
        private AudioEngine? _engine;

        public AudioEngine? Engine
        {
            get => Volatile.Read(ref _engine);
            set => Volatile.Write(ref _engine, value);
        }

        public void Render(Span<float> interleaved, int frames, int channels, in AudioDeviceBlock block)
        {
            if (Volatile.Read(ref _engine) is { } engine)
            {
                engine.Render(interleaved, frames, channels, block);
            }
        }
    }
}
