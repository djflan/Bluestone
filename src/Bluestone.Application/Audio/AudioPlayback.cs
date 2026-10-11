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
/// <remarks>
/// A lost device (unplugged, or its stream invalidated) is noticed within a check interval: the stream is closed,
/// the system default output is opened in its place when the lost device was a specific one, and the chosen device is
/// opened again as soon as it is listed again. With nothing to open, audio stays off and waits for the device. Each
/// time a device opens at a rate, plugin instances are asked to run at that rate. Start, Stop, the checks, and Dispose
/// are serialized; graph rebuilds run on the thread pool, one at a time.
/// </remarks>
public sealed class AudioPlayback : IDisposable
{
    private static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(1);
    private readonly PlaybackController _playback;
    private readonly ProjectSession _session;
    private readonly Func<DeviceId, PluginInstance?> _instances;
    private readonly int _maxBlockFrames;
    private readonly IAudioBackend _backend;
    private readonly Func<int, Task>? _prepareSampleRate;
    private readonly TimeSpan _checkInterval;
    private readonly Lock _gate = new();
    private readonly Lock _control = new();
    private readonly EngineSlot _slot = new();
    private AudioEngine? _engine;
    private IAudioOutput? _output;
    private ProjectAudioGraph? _graph;
    private Timer? _check;
    private AudioSettings? _wanted;
    private string? _wantedName;
    private string? _waitingReason;
    private string _devicesTried = string.Empty;
    private bool _testTone;

    /// <param name="playback">The MIDI playback controller whose transport the audio follows.</param>
    /// <param name="session">The project.</param>
    /// <param name="instances">The running plugin instance of a device, if any.</param>
    /// <param name="maxBlockFrames">The block size plugin instances accept; the engine renders blocks no larger.</param>
    /// <param name="backend">Where streams come from; the operating system's devices by default.</param>
    /// <param name="prepareSampleRate">
    /// Called with the device's rate whenever a device opens, to run plugin instances at it (they are silent at any
    /// other rate); the graph is rebuilt when it completes.
    /// </param>
    /// <param name="checkInterval">How often the device is checked (and the transport aligned); infinite for tests that call <see cref="CheckDevice"/>.</param>
    public AudioPlayback(
        PlaybackController playback,
        ProjectSession session,
        Func<DeviceId, PluginInstance?> instances,
        int maxBlockFrames = 256,
        IAudioBackend? backend = null,
        Func<int, Task>? prepareSampleRate = null,
        TimeSpan? checkInterval = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _maxBlockFrames = maxBlockFrames;
        _backend = backend ?? SystemAudioBackend.Instance;
        _prepareSampleRate = prepareSampleRate;
        _checkInterval = checkInterval ?? DefaultCheckInterval;
        _playback.Refreshed += (_, _) => _ = Task.Run(Rebuild);
    }

    /// <summary>Raised (on any thread) when the device opens or closes, fails, or the graph's problems change.</summary>
    public event EventHandler? Changed;

    public bool IsRunning => _output is not null;

    /// <summary>Why the device could not be opened, why it stopped, or that another device stands in for it; null when fine.</summary>
    public string? Problem { get; private set; }

    /// <summary>The chosen device is gone and the system default output plays instead until it returns.</summary>
    public bool IsOnFallback { get; private set; }

    /// <summary>Audio was started but no device can be opened now; it opens again when the chosen device returns.</summary>
    public bool IsWaitingForDevice => _wanted is not null && _output is null;

    /// <summary>What the last graph could not play (missing files, silent devices).</summary>
    public ImmutableArray<string> GraphProblems { get; private set; } = [];

    public AudioEngine? Engine => _engine;

    public IAudioOutput? Output => _output;

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

    /// <summary>
    /// Opens the device and starts the engine. When a specific device cannot be opened, the system default output
    /// plays instead (<see cref="IsOnFallback"/>) until it can; when nothing can be opened, <see cref="Problem"/> says
    /// why and audio starts by itself once the device appears. Returns whether a device is open.
    /// </summary>
    public bool Start(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_control)
        {
            Close();
            _wanted = settings;
            _wantedName = null;
            _waitingReason = null;
            IsOnFallback = false;
            _check ??= new Timer(_ => Tick(), null, _checkInterval, _checkInterval);
            return Recover(null);
        }
    }

    /// <summary>Closes the device and stops waiting for it. MIDI playback carries on alone.</summary>
    public void Stop()
    {
        lock (_control)
        {
            _wanted = null;
            _waitingReason = null;
            _check?.Dispose();
            _check = null;
            Close();
            Problem = null;
            IsOnFallback = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Notices a lost device and replaces it, or opens the chosen device again once it is back. Runs on a timer; tests
    /// call it directly.
    /// </summary>
    public void CheckDevice()
    {
        lock (_control)
        {
            if (_wanted is not { } wanted)
            {
                return;
            }

            if (_output is { } output && Failed(output))
            {
                var lost = IsOnFallback ? "The system default output" : Name(wanted.DeviceId);
                Close();
                IsOnFallback = false;
                Recover($"{lost} stopped (it may have been disconnected).");
                return;
            }

            // Waiting: try again only when the devices present have changed. On the stand-in: once the chosen one is back.
            if ((_output is null && DeviceList() != _devicesTried) || (IsOnFallback && Listed(wanted.DeviceId)))
            {
                Recover(null);
            }
        }
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

    // Opens the chosen device, or the default in its place, or nothing (and waits). Under _control.
    private bool Recover(string? lost)
    {
        var wanted = _wanted!;
        _devicesTried = DeviceList();
        if (TryOpen(wanted, wanted.DeviceId, out var problem))
        {
            _wantedName = wanted.DeviceId is null ? null : Name(wanted.DeviceId);
            _waitingReason = null;
            Problem = null;
            IsOnFallback = false;
        }
        else if (wanted.DeviceId is not null && (IsOnFallback || TryOpen(wanted, null, out _)))
        {
            _waitingReason = null;
            Problem = $"{Name(wanted.DeviceId)} is not available ({problem}). Playing through the system default output until it returns.";
            IsOnFallback = true;
        }
        else if (_output is null)
        {
            _waitingReason = lost ?? _waitingReason ?? problem;
            Problem = $"{_waitingReason} Audio starts by itself when the device is available, or choose another device.";
            IsOnFallback = false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return _output is not null;
    }

    private bool TryOpen(AudioSettings settings, string? deviceId, out string? problem)
    {
        problem = null;
        if (deviceId is not null && _output is not null && !Listed(deviceId))
        {
            problem = "it is not connected";
            return false;
        }

        IAudioOutput output;
        var request = new AudioStreamRequest(deviceId, settings.SampleRate, settings.BufferFrames, QueuedFrames: settings.QueuedFrames);
        try
        {
            output = Open(request);
        }
        catch (AudioDeviceException ex)
        {
            problem = ex.Message;
            return false;
        }

        // The stand-in (if any) closes only once the chosen device is open.
        Close();
        var rate = output.SampleRate;
        var block = Math.Clamp(settings.BufferFrames, 32, _maxBlockFrames);
        var engine = new AudioEngine(rate, block) { TestTone = _testTone ? new ToneNode(440, 0.125) : null };
        var device = Math.Max(settings.BufferFrames, block);
        engine.Transport.Follow(new PlaybackTimeline(_playback.Engine), toleranceFrames: Math.Max(rate / 100, 2 * (device + block)));
        lock (_gate)
        {
            _engine = engine;
            _graph = new ProjectAudioGraph(rate, block, _instances);
            _graph.LatencyChanged += (_, _) => _ = Task.Run(Rebuild);
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
            Close();
            problem = ex.Message;
            return false;
        }

        _output = output;
        if (_prepareSampleRate is { } prepare)
        {
            _ = PrepareAsync(prepare, rate);
        }

        return true;
    }

    // The device may not offer the rate; its own rate is better than no sound.
    private IAudioOutput Open(AudioStreamRequest request)
    {
        try
        {
            return _backend.Open(_slot, request);
        }
        catch (AudioDeviceException) when (request.SampleRate != 0)
        {
            return _backend.Open(_slot, request with { SampleRate = 0 });
        }
    }

    private async Task PrepareAsync(Func<int, Task> prepare, int rate)
    {
        try
        {
            await prepare(rate).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Plugins that cannot follow the rate stay silent and say so in the graph's problems.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning($"Plugins could not be restarted at {rate} Hz: {ex.Message}");
        }

        Rebuild();
    }

    // Closes the stream and the engine, keeping what was asked for. Under _control.
    private void Close()
    {
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
    }

    private void Tick()
    {
        Align();
        CheckDevice();

        // A plugin restarted or changed its latency without telling us: compensate for what it reports now.
        if (_engine?.Graph.IsLatencyStale == true)
        {
            Rebuild();
        }
    }

    // Moves the MIDI transport towards the audio by half the measured difference.
    private void Align()
    {
        if (_engine is not { } engine || _output is null)
        {
            return;
        }

        var (average, callbacks) = engine.Transport.TakeDrift();
        if (callbacks >= 10 && Math.Abs(average) >= 1)
        {
            _playback.Engine.Nudge(TimeSpan.FromSeconds(average / 2 / engine.SampleRate));
        }
    }

    private static bool Failed(IAudioOutput output)
    {
        try
        {
            return output.Status.State == AudioStreamState.Failed;
        }
        catch (Exception ex) when (ex is AudioDeviceException or ObjectDisposedException)
        {
            return true;
        }
    }

    private bool Listed(string? deviceId)
    {
        var devices = _backend.ListDevices();
        return deviceId is null ? devices.Count > 0 : devices.Any(d => d.Id == deviceId);
    }

    private string Name(string? deviceId) =>
        deviceId is null
            ? "The system default output"
            : _backend.ListDevices().FirstOrDefault(d => d.Id == deviceId)?.Name ?? _wantedName ?? $"The audio device \"{deviceId}\"";

    private string DeviceList() => string.Join("\n", _backend.ListDevices().Select(d => d.Id));

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
