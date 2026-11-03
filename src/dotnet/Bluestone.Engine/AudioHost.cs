using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using Bluestone.Engine.Protocol;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Engine;

/// <summary>
/// Audio in the engine process (ADR 0044, migration step 6): the output device Bluestone chooses, the audio engine that
/// renders in its callback while following the playback engine's transport, the graphs Bluestone describes (read and
/// realized here, off the real-time thread), the clip streams and the disk streamer, and the data plane of every plugin
/// in the graph, attached to the block exchanges of workers that Bluestone supervises.
/// </summary>
/// <remarks>
/// <para>
/// Three threads use it. The control thread queues Bluestone's commands (<see cref="Apply"/>). The audio thread carries
/// them out in order (opens the device, reads and realizes graphs, renders for tests), reads the meters about 30 times a
/// second, and every second checks the device (a lost device is replaced by the system default output, or waited for,
/// as in Bluestone) and moves the playback transport towards the audio clock. The sender thread reports what the audio
/// thread left for it (<see cref="Report"/>). Neither the control thread nor the sender waits for the audio thread, and
/// the device callback takes no lock: it reads the engine it renders, and the engine its graph, through single
/// references.
/// </para>
/// <para>
/// A graph is realized for the open stream's rate and block size only; Bluestone describes it for those, which it learns
/// from <see cref="AudioDeviceStatus"/>. Every graph is acknowledged, played or not, so its file can be deleted. An engine
/// started after a crash sends sustain off, All Notes Off, and All Sound Off to each plugin before its first block.
/// </para>
/// </remarks>
internal sealed class AudioHost : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);
    private const int MaxProblemLength = 400;

    private readonly PlaybackEngine _playback;
    private readonly IAudioBackend _backend;
    private readonly bool _restarted;
    private readonly bool _test;
    private readonly EngineTimeline _timeline;
    private readonly RendererSlot _slot = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, AttachedPluginInstance> _plugins = [];
    private readonly List<string> _attachProblems = [];
    private readonly Lock _queues = new();
    private readonly Queue<EngineMessage> _work = new();
    private readonly Queue<EngineMessage> _outbox = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread? _thread;
    private volatile bool _running = true;

    // Under _gate.
    private SetAudio? _wanted;
    private IAudioOutput? _output;
    private AudioEngine? _engine;
    private AudioGraphRealizer? _realizer;
    private (LoadAudioGraph Load, AudioGraphDescription Description)? _lastGraph;
    private string? _problem;
    private string? _waitingReason;
    private string? _wantedName;
    private bool _onFallback;
    private string _devicesTried = string.Empty;
    private bool _statusChanged;
    private string? _reportedProblem;

    // Under _queues.
    private long _newestGraph = -1;
    private AudioStatus? _meters;

    /// <param name="playback">The playback engine whose transport the audio follows.</param>
    /// <param name="backend">Where streams come from.</param>
    /// <param name="restarted">Started after a crash: sweep every plugin before its first block.</param>
    /// <param name="test">The backend is a test's stand-in that never calls back: graphs render only on <see cref="TestRender"/>.</param>
    /// <param name="startThread">False to work through <see cref="RunPending"/> by hand (tests and the allocation check).</param>
    public AudioHost(PlaybackEngine playback, IAudioBackend backend, bool restarted, bool test, bool startThread = true)
    {
        _playback = playback;
        _backend = backend;
        _restarted = restarted;
        _test = test;
        _timeline = new EngineTimeline(playback);
        if (startThread)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Bluestone engine audio" };
            _thread.Start();
        }
    }

    /// <summary>The engine rendering now, if a device is open.</summary>
    internal AudioEngine? Engine => Volatile.Read(ref _engine);

    /// <summary>Whether <paramref name="message"/> is an audio command.</summary>
    public static bool Handles(EngineMessage message) => message is SetAudio or LoadAudioGraph or TestRender;

    /// <summary>
    /// Queues one command from Bluestone for the audio thread, in order: the control thread never waits for a device to
    /// open or a graph to be realized. The control thread only.
    /// </summary>
    public void Apply(EngineMessage message)
    {
        lock (_queues)
        {
            if (message is LoadAudioGraph load)
            {
                _newestGraph = Math.Max(_newestGraph, load.GraphId);
            }

            _work.Enqueue(message);
        }

        _wake.Set();
    }

    /// <summary>The device's state when it changed, graphs read, test renders done, and the latest meters. The sender thread only; it never waits for the audio thread.</summary>
    public void Report(Action<EngineMessage> send)
    {
        List<EngineMessage>? messages = null;
        lock (_queues)
        {
            while (_outbox.TryDequeue(out var message))
            {
                (messages ??= []).Add(message);
            }

            if (_meters is { } meters)
            {
                (messages ??= []).Add(meters);
                _meters = null;
            }
        }

        messages?.ForEach(send);
    }

    /// <summary>Does the audio thread's queued work now, on the calling thread. For hosts started without the thread.</summary>
    internal void RunPending()
    {
        while (TakeWork() is { } work)
        {
            Do(work);
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread?.Join();
        lock (_gate)
        {
            Close();
            foreach (var plugin in _plugins.Values)
            {
                plugin.Dispose();
            }

            _plugins.Clear();
        }

        _wake.Dispose();
    }

    private void Run()
    {
        var checkedAt = Stopwatch.GetTimestamp();
        var meteredAt = checkedAt;
        while (_running)
        {
            _wake.WaitOne(Volatile.Read(ref _engine) is null ? CheckInterval : MeterInterval);
            RunPending();
            if (Stopwatch.GetElapsedTime(meteredAt) >= MeterInterval)
            {
                meteredAt = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    if (Meters() is { } meters)
                    {
                        lock (_queues)
                        {
                            _meters = meters;
                        }
                    }
                }
            }

            if (Stopwatch.GetElapsedTime(checkedAt) >= CheckInterval)
            {
                checkedAt = Stopwatch.GetTimestamp();
                lock (_gate)
                {
                    Align();
                    CheckDevice();
                    PostStatus();
                }
            }
        }
    }

    private EngineMessage? TakeWork()
    {
        lock (_queues)
        {
            return _work.TryDequeue(out var work) ? work : null;
        }
    }

    private void Do(EngineMessage work)
    {
        switch (work)
        {
            case SetAudio audio:
                lock (_gate)
                {
                    Configure(audio);
                    PostStatus();
                }

                break;
            case LoadAudioGraph load:
                Load(load);
                break;
            case TestRender render:
                lock (_gate)
                {
                    Post(new TestRendered(render.Id, TestRenderNow(render)));
                }

                break;
        }
    }

    private void Post(EngineMessage message)
    {
        lock (_queues)
        {
            _outbox.Enqueue(message);
        }
    }

    // The device's state, if it changed, and a new device problem as an engine problem. Under _gate.
    private void PostStatus()
    {
        if (!_statusChanged)
        {
            return;
        }

        _statusChanged = false;
        var status = Status();
        Post(status);
        if (status.Problem is { } problem && problem != _reportedProblem)
        {
            Post(new EngineProblem(-1, problem, EngineProblemArea.Audio));
        }

        _reportedProblem = status.Problem;
    }

    // Under _gate.
    private void Configure(SetAudio audio)
    {
        var previous = _wanted;
        _wanted = audio;
        _statusChanged = true;
        if (!audio.Enabled)
        {
            Close();
            _problem = null;
            _waitingReason = null;
            _onFallback = false;
            return;
        }

        if (previous is { Enabled: true } && previous.DeviceId == audio.DeviceId && previous.SampleRate == audio.SampleRate && previous.BufferFrames == audio.BufferFrames
            && previous.QueuedFrames == audio.QueuedFrames && previous.MaxBlockFrames == audio.MaxBlockFrames)
        {
            if (_engine is { } engine)
            {
                engine.TestTone = audio.TestTone ? new ToneNode(440, 0.125) : null;
            }

            return;
        }

        Close();
        _wantedName = null;
        _waitingReason = null;
        _onFallback = false;
        Recover(null);
    }

    // Opens the chosen device, or the default in its place, or nothing (and waits). Under _gate.
    private void Recover(string? lost)
    {
        var wanted = _wanted!;
        _devicesTried = DeviceList();
        _statusChanged = true;
        if (TryOpen(wanted, wanted.DeviceId, out var problem))
        {
            _wantedName = wanted.DeviceId is null ? null : Name(wanted.DeviceId);
            _waitingReason = null;
            _problem = null;
            _onFallback = false;
        }
        else if (wanted.DeviceId is not null && (_onFallback || TryOpen(wanted, null, out _)))
        {
            _waitingReason = null;
            _problem = $"{Name(wanted.DeviceId)} is not available ({problem}). Playing through the system default output until it returns.";
            _onFallback = true;
        }
        else if (_output is null)
        {
            _waitingReason = lost ?? _waitingReason ?? problem;
            _problem = $"{_waitingReason} Audio starts by itself when the device is available, or choose another device.";
            _onFallback = false;
        }
    }

    // Under _gate.
    private bool TryOpen(SetAudio wanted, string? deviceId, out string? problem)
    {
        problem = null;
        if (deviceId is not null && _output is not null && !Listed(deviceId))
        {
            problem = "it is not connected";
            return false;
        }

        IAudioOutput output;
        var request = new AudioStreamRequest(deviceId, wanted.SampleRate, wanted.BufferFrames, QueuedFrames: wanted.QueuedFrames);
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
        var block = Math.Clamp(wanted.BufferFrames, 32, Math.Max(32, wanted.MaxBlockFrames));
        var engine = new AudioEngine(rate, block, streamFromDisk: !_test) { TestTone = wanted.TestTone ? new ToneNode(440, 0.125) : null };
        var device = Math.Max(wanted.BufferFrames, block);
        engine.Transport.Follow(_timeline, ToleranceFrames(rate, device, block));
        Volatile.Write(ref _realizer, new AudioGraphRealizer(rate, block, ProcessorFor));
        Volatile.Write(ref _engine, engine);
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

        // The graph Bluestone described last plays again if it was described for this stream.
        if (_lastGraph is { } last)
        {
            Realize(last.Load, last.Description, acknowledge: false);
        }

        return true;
    }

    private static int ToleranceFrames(int rate, int deviceFrames, int blockFrames) => Math.Max(rate / 100, 2 * (deviceFrames + blockFrames));

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

    // Closes the stream and the engine, keeping what was asked for. Under _gate.
    private void Close()
    {
        _output?.Dispose();
        _output = null;
        _slot.Engine = null;
        Interlocked.Exchange(ref _engine, null)?.Dispose();
        Interlocked.Exchange(ref _realizer, null)?.Dispose();
        _statusChanged = true;
    }

    // Notices a lost device and replaces it, or opens the chosen device again once it is back. Under _gate.
    private void CheckDevice()
    {
        if (_wanted is not { Enabled: true } wanted)
        {
            return;
        }

        if (_output is { } output && Failed(output))
        {
            var lost = _onFallback ? "The system default output" : Name(wanted.DeviceId);
            Close();
            _onFallback = false;
            Recover($"{lost} stopped (it may have been disconnected).");
            return;
        }

        if ((_output is null && DeviceList() != _devicesTried) || (_onFallback && Listed(wanted.DeviceId)))
        {
            Recover(null);
        }
    }

    // Moves the playback transport towards the audio by half the measured difference (ADR 0032). Under _gate.
    private void Align()
    {
        if (_engine is not { } engine || _output is null)
        {
            return;
        }

        var (average, callbacks) = engine.Transport.TakeDrift();
        if (callbacks >= 10 && Math.Abs(average) >= 1)
        {
            _playback.Nudge(TimeSpan.FromSeconds(average / 2 / engine.SampleRate));
        }
    }

    private void Load(LoadAudioGraph load)
    {
        AudioGraphDescription? description = null;
        string? unreadable = null;
        lock (_queues)
        {
            if (load.GraphId < _newestGraph)
            {
                // A newer graph is on its way: this one is skipped, but its file can go.
                _outbox.Enqueue(new AudioGraphLoaded(load.GraphId, false, 0, []));
                return;
            }
        }

        try
        {
            description = AudioGraphImage.Read(File.ReadAllBytes(load.Path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            unreadable = $"The engine could not read the audio graph: {ex.Message}";
        }

        lock (_gate)
        {
            if (description is null)
            {
                Post(new AudioGraphLoaded(load.GraphId, false, 0, [Shorten(unreadable!)]));
                return;
            }

            _lastGraph = (load, description);
            Realize(load, description, acknowledge: true);
        }
    }

    // Under _gate.
    private void Realize(LoadAudioGraph load, AudioGraphDescription description, bool acknowledge)
    {
        if (_engine is not { } engine || _realizer is not { } realizer || engine.SampleRate != load.SampleRate || engine.BlockFrames != load.BlockFrames)
        {
            if (acknowledge)
            {
                Post(new AudioGraphLoaded(load.GraphId, false, 0, []));
            }

            return;
        }

        _attachProblems.Clear();
        var realized = realizer.Realize(description);
        engine.Publish(realized.Graph);

        // Instances the graph no longer has are let go; the old graph may still be in its last block, which then
        // renders their failure policy.
        foreach (var gone in _plugins.Keys.Where(key => !realizer.PluginNodes.ContainsKey(key)).ToList())
        {
            _plugins.Remove(gone, out var plugin);
            plugin!.Dispose();
        }

        if (acknowledge)
        {
            var problems = realized.Problems.Concat(_attachProblems).Distinct().Take(EngineLimits.MaxGraphProblems).Select(Shorten);
            Post(new AudioGraphLoaded(load.GraphId, true, realized.Graph.LatencyFrames, [.. problems]));
        }
    }

    // The realizer's plugin bridge: one attached instance per device, kept while its shape and pipeline stay the same, so
    // the node keeps its note tracking; attached to the exchange the description names. Under _gate.
    private AttachedPluginInstance? ProcessorFor(DescribedNode described)
    {
        if (described.Link is not { } link)
        {
            return null;
        }

        if (!_plugins.TryGetValue(described.Key, out var plugin) || plugin.Shape != link.Shape || plugin.PipelineDepth != link.PipelineDepth
            || plugin.CollectWait != link.CollectWait || plugin.BypassOnFailure != link.BypassOnFailure)
        {
            plugin?.Dispose();
            plugin = new AttachedPluginInstance(link.Shape, link.PipelineDepth, link.CollectWait, link.BypassOnFailure);
            if (_restarted)
            {
                plugin.SweepBeforeNextBlock();
            }

            _plugins[described.Key] = plugin;
        }

        plugin.ReportedLatencyFrames = link.LatencyFrames;
        try
        {
            plugin.Attach(link.DataPlane);
        }
        catch (Exception ex) when (ex is ProtocolException or IOException or UnauthorizedAccessException)
        {
            plugin.Attach(null);
            _attachProblems.Add($"{described.Name}: the engine could not reach its plugin ({ex.Message}), so it is silent.");
        }

        return plugin;
    }

    // Tests: the current graph from the song's start, through the device callback's render path. Under _gate.
    private string? TestRenderNow(TestRender render)
    {
        if (!_test)
        {
            return "Only an engine with the test audio device renders for tests.";
        }

        if (_engine is not { } engine || _wanted is not { } wanted)
        {
            return "No audio device is open.";
        }

        engine.Transport.Follow(null, 0);
        engine.Transport.Play(0);
        try
        {
            // Whole blocks, so the next render starts on a block boundary as this one did.
            var frames = (render.Frames + engine.BlockFrames - 1) / engine.BlockFrames * engine.BlockFrames;
            var samples = new OfflineAudioDriver(engine).Render(frames, render.CallbackFrames);
            File.WriteAllBytes(render.Path, MemoryMarshal.AsBytes(samples.AsSpan(0, render.Frames * 2)).ToArray());
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        finally
        {
            engine.Transport.Stop();
            engine.Transport.Follow(_timeline, ToleranceFrames(engine.SampleRate, Math.Max(wanted.BufferFrames, engine.BlockFrames), engine.BlockFrames));
        }
    }

    // Under _gate.
    private AudioDeviceStatus Status()
    {
        var state = _output is not null ? (_onFallback ? AudioDeviceState.Fallback : AudioDeviceState.Running)
            : _wanted is { Enabled: true } ? AudioDeviceState.Waiting
            : AudioDeviceState.Off;
        var problem = _problem is null ? null : Shorten(_problem);
        return _output is { } output && _engine is { } engine
            ? new AudioDeviceStatus(_wanted?.Request ?? 0, state, problem, output.SampleRate, output.Channels, engine.BlockFrames, output.QueuedFrames, output.RenderThreadPolicy)
            : new AudioDeviceStatus(_wanted?.Request ?? 0, state, problem, 0, 0, 0, 0, null);
    }

    // The meters and the stream's figures, while a stream is open. Under _gate; the meters themselves are read lock-free.
    private AudioStatus? Meters()
    {
        if (_output is not { } output || _engine is not { } engine || _realizer is not { } realizer)
        {
            return null;
        }

        AudioStreamStatus stream;
        try
        {
            stream = output.Status;
        }
        catch (Exception ex) when (ex is AudioDeviceException or ObjectDisposedException)
        {
            return null;
        }

        var strips = ImmutableArray.CreateBuilder<StripMeter>();
        foreach (var (key, state) in realizer.Strips)
        {
            if (strips.Count < EngineLimits.MaxMeters)
            {
                strips.Add(new StripMeter(key, Meter(state.Meter.Read())));
            }
        }

        var failed = 0L;
        foreach (var node in realizer.PluginNodes.Values)
        {
            failed += node.FailedBlocks;
        }

        var graph = engine.Graph;
        var counters = new AudioCounters(
            stream.Callbacks,
            stream.Xruns,
            stream.Discontinuities,
            stream.LateRenders,
            stream.QueueUnderrunFrames,
            engine.Statistics.Faults,
            stream.RenderMax.Ticks,
            stream.OutputLatency.Ticks,
            failed,
            graph.LatencyFrames);
        return new AudioStatus(Meter(realizer.Master.Meter.Read()), strips.ToImmutable(), counters);
    }

    private static MeterSpec Meter(MeterReading reading) => new(Level(reading.PeakLeft), Level(reading.PeakRight), Level(reading.RmsLeft), Level(reading.RmsRight));

    private static float Level(float value) => float.IsFinite(value) && value > 0 ? value : 0;

    private static string Shorten(string text) => text.Length <= MaxProblemLength ? text : text[..MaxProblemLength] + "…";

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

    /// <summary>The playback engine's transport as the audio engine's timeline: the song time heard at a moment (ADR 0032).</summary>
    private sealed class EngineTimeline(PlaybackEngine playback) : IAudioTimeline
    {
        public bool TryGetPosition(long timestamp, out TimelinePosition position)
        {
            if (!playback.TryGetSongTimeAt(Stopwatch.GetElapsedTime(0, timestamp), out var song))
            {
                position = default;
                return false;
            }

            position = new TimelinePosition(song.SongTime.TotalSeconds, song.LoopStart.TotalSeconds, song.LoopEnd.TotalSeconds, song.BeatsPerMinute);
            return true;
        }
    }

    /// <summary>The renderer the device holds; the engine behind it is set once the device's rate is known.</summary>
    private sealed class RendererSlot : IAudioRenderer
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
