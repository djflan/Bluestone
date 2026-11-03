using System.Collections.Immutable;
using Bluestone.Application.Audio;
using Bluestone.Application.Live;
using Bluestone.Application.Sessions;
using Bluestone.Audio;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Engine.Protocol;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Application.Engine;

public sealed record EngineAudioOptions
{
    /// <summary>Where audio graph images are written for the engine; null for a folder of its own in the temporary folder.</summary>
    public string? GraphDirectory { get; init; }

    /// <summary>The block size plugin instances accept; the engine renders blocks no larger.</summary>
    public int MaxBlockFrames { get; init; } = 256;
}

/// <summary>
/// Audio in the engine process (ADR 0044, migration step 6), as Bluestone sees it: a stand-in for the in-process
/// <see cref="AudioPlayback"/>. The device choice and settings go to the engine, which opens the device, renders in its
/// callback, follows its own playback transport, and reports the device's state, meters, and figures, mirrored here.
/// Bluestone still describes the graph (<see cref="ProjectAudioGraph.Describe"/>) for the stream's rate and block size,
/// with each plugin's block exchange, and hands it over as an <see cref="AudioGraphImage"/> file; it describes it again
/// after every refresh of the playback controller, every change of a plugin instance (a restarted worker has a new
/// exchange), and every new stream. After an engine restart the settings go again and the graph follows.
/// </summary>
public sealed class EngineAudio : IAudioPlayback, IEngineClient
{
    private readonly PlaybackController _playback;
    private readonly ProjectSession _session;
    private readonly Func<DeviceId, PluginInstance?> _instances;
    private readonly Func<int, Task>? _prepareSampleRate;
    private readonly EngineAudioOptions _options;
    private readonly string _graphDirectory;
    private readonly Lock _gate = new();
    private readonly Lock _rebuild = new();
    private readonly List<(long Id, string Path)> _graphFiles = [];
    private readonly HashSet<PluginInstance> _watched = [];
    private readonly Dictionary<long, TaskCompletionSource<string?>> _renders = [];
    private IEngineLink? _link;
    private IEngineLink? _graphLink;
    private AudioSettings? _wanted;
    private bool _testTone;
    private long _request;
    private long _startRequest;
    private AudioDeviceStatus? _status;
    private (int Rate, int Block) _described;
    private int _preparedRate;
    private ProjectAudioGraph? _describer;
    private long _nextGraph;
    private long _nextRender;
    private int _latencyFrames;
    private ImmutableArray<string> _describeProblems = [];
    private ImmutableArray<string> _engineProblems = [];
    private Meters _meters = Meters.None;

    /// <param name="supervisor">The engine's supervisor; this attaches to it.</param>
    /// <param name="playback">The MIDI playback controller: its refreshes, routing, and devices.</param>
    /// <param name="session">The project.</param>
    /// <param name="instances">The running plugin instance of a device, if any.</param>
    /// <param name="prepareSampleRate">Called with the stream's rate whenever it changes, to run plugin instances at it; the graph is described again when it completes.</param>
    /// <param name="options">Where graph files go, and the block size.</param>
    public EngineAudio(EngineSupervisor supervisor, PlaybackController playback, ProjectSession session, Func<DeviceId, PluginInstance?> instances, Func<int, Task>? prepareSampleRate = null, EngineAudioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _prepareSampleRate = prepareSampleRate;
        _options = options ?? new EngineAudioOptions();
        _graphDirectory = _options.GraphDirectory ?? Path.Combine(Path.GetTempPath(), $"bluestone-engine-{Environment.ProcessId}");
        _playback.Refreshed += (_, _) => _ = Task.Run(Rebuild);
        // The monitored instrument is played live: the engine plays it at depth 1 (and compensates for that).
        _playback.Live.MonitoredInstrumentChanged += (_, _) => _ = Task.Run(Rebuild);
        supervisor.Attach(this);
    }

    public event EventHandler? Changed;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _link is not null && _status is { State: AudioDeviceState.Running or AudioDeviceState.Fallback };
            }
        }
    }

    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                return _wanted is not null && _link is null ? "The engine is not running; audio starts when it is." : _status?.Problem;
            }
        }
    }

    public bool IsOnFallback
    {
        get
        {
            lock (_gate)
            {
                return _link is not null && _status?.State == AudioDeviceState.Fallback;
            }
        }
    }

    /// <summary>The settings went to the engine, which has not said yet what it opened.</summary>
    public bool IsStarting
    {
        get
        {
            lock (_gate)
            {
                return _wanted is not null && _link is not null && (_status is null || _status.Request < _startRequest);
            }
        }
    }

    public bool IsWaitingForDevice => _wanted is not null && !IsRunning && !IsStarting;

    public ImmutableArray<string> GraphProblems
    {
        get
        {
            lock (_gate)
            {
                return [.. _describeProblems.Concat(_engineProblems).Distinct()];
            }
        }
    }

    public bool TestTone
    {
        get => _testTone;
        set
        {
            lock (_gate)
            {
                _testTone = value;
                if (_wanted is { } wanted)
                {
                    _link?.Send(SetAudio(wanted));
                }
            }
        }
    }

    public bool RendersInEngine => true;

    public AudioStreamInfo? Stream
    {
        get
        {
            lock (_gate)
            {
                return _link is not null && _status is { State: AudioDeviceState.Running or AudioDeviceState.Fallback } status
                    ? new AudioStreamInfo(status.SampleRate, status.Channels, status.BlockFrames, status.QueuedFrames, status.RenderThreadPolicy, _latencyFrames)
                    : null;
            }
        }
    }

    public AudioTelemetry? Telemetry => IsRunning && Volatile.Read(ref _meters).Counters is { } counters
        ? new AudioTelemetry(counters.Callbacks, counters.Xruns, counters.Discontinuities, counters.LateRenders, counters.QueueUnderrunFrames, counters.RenderFaults, TimeSpan.FromTicks(counters.RenderMax), TimeSpan.FromTicks(counters.OutputLatency), counters.PluginFailedBlocks)
        : null;

    /// <summary>Live notes into plugin instruments, measured in the engine where they are heard (ADR 0044 step 7).</summary>
    public LiveLatency LiveLatency => Volatile.Read(ref _meters).Live is { Notes: > 0 } live
        ? new LiveLatency(live.Notes, TimeSpan.FromTicks(live.Average), TimeSpan.FromTicks(live.Min), TimeSpan.FromTicks(live.Max))
        : new LiveLatency(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);

    public bool IsLiveQueue => IsRunning && Volatile.Read(ref _meters).Counters is { LiveQueue: true };

    /// <summary>Whether an engine is connected now.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _link is not null;
            }
        }
    }

    /// <summary>
    /// Asks the engine to open the device with <paramref name="settings"/> (rendering ahead by
    /// <see cref="AudioSettings.EngineQueuedFrames"/>) and returns at once, without waiting for the engine:
    /// <see cref="IsStarting"/> until it reports the stream it opened (raising <see cref="Changed"/>), and the graph is
    /// described then. With no engine running yet the settings wait for one.
    /// </summary>
    /// <returns>Whether a device is open now (normally not yet).</returns>
    public bool Start(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _wanted = settings;
            var message = SetAudio(settings);
            _startRequest = message.Request;
            _link?.Send(message);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return IsRunning;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wanted = null;
            _link?.Send(new SetAudio(++_request, false, null, 0, 0, 0, _options.MaxBlockFrames, false));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public MeterReading? MeterOf(MixerChannelId? channel)
    {
        if (!IsRunning)
        {
            return null;
        }

        var meters = Volatile.Read(ref _meters);
        if (channel is not { } id)
        {
            return meters.Master;
        }

        return meters.Strips.TryGetValue(id.Value, out var reading) ? reading : null;
    }

    /// <summary>
    /// Describes the graph for the current project and routing at the open stream's rate and block size, writes it as an
    /// image file, and tells the engine to play it. Does nothing until the engine reports an open stream. Any thread.
    /// </summary>
    public void Rebuild()
    {
        lock (_rebuild)
        {
            IEngineLink link;
            int rate, block;
            lock (_gate)
            {
                if (_link is not { } connected || _status is not { State: AudioDeviceState.Running or AudioDeviceState.Fallback, SampleRate: > 0, BlockFrames: > 0 } status)
                {
                    return;
                }

                (link, rate, block) = (connected, status.SampleRate, status.BlockFrames);
            }

            if (_describer is null || _described != (rate, block))
            {
                _describer?.Dispose();
                _describer = new ProjectAudioGraph(rate, block, _instances);
                _described = (rate, block);
            }

            AudioGraphDescription description;
            ImmutableArray<string> problems;
            if (_playback.Routing is not { } routing)
            {
                (description, problems) = (AudioGraphDescription.Empty, []);
            }
            else
            {
                var directory = _session.FilePath is { } file ? Path.GetDirectoryName(Path.GetFullPath(file)) : null;
                var live = _playback.Live.MonitoredInstrument;
                _describer.Monitored = _wanted?.LowLatencyMonitoring == true && live is { } monitored ? [monitored] : [];
                _describer.LivePlayed = live is { } played ? [played] : [];
                (description, problems) = _describer.Describe(_session.Project, routing.Graph, _playback.Devices, directory);
            }

            Watch(description);
            Directory.CreateDirectory(_graphDirectory);
            var id = Interlocked.Increment(ref _nextGraph);
            var path = Path.Combine(_graphDirectory, $"graph-{id}.bsgr");
            File.WriteAllBytes(path, AudioGraphImage.ToArray(description));
            bool changed;
            lock (_gate)
            {
                _graphFiles.Add((id, path));
                changed = !problems.SequenceEqual(_describeProblems);
                _describeProblems = problems;
                _graphLink = link;
                link.Send(new LoadAudioGraph(id, path, rate, block));
            }

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Deletes the graph files.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _wanted = null;
            foreach (var (_, path) in _graphFiles)
            {
                TryDelete(path);
            }

            _graphFiles.Clear();
        }

        lock (_rebuild)
        {
            Watch(AudioGraphDescription.Empty);
            _describer?.Dispose();
            _describer = null;
        }
    }

    /// <summary>
    /// Tests only, with an engine started for tests: the engine renders <paramref name="frames"/> of the current graph from
    /// the song's start through its device callback's render path; returns the interleaved stereo samples.
    /// </summary>
    internal async Task<float[]> RenderForTestAsync(string path, int frames, int callbackFrames, CancellationToken cancellationToken, ImmutableArray<TestNote> notes = default)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var id = ++_nextRender;
            _renders[id] = done;
            if (_link?.Send(new TestRender(id, path, frames, callbackFrames, notes.IsDefault ? [] : notes)) != true)
            {
                throw new InvalidOperationException("No engine is running.");
            }
        }

        if (await done.Task.WaitAsync(cancellationToken).ConfigureAwait(false) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
    }

    /// <summary>Tests only: the graph the engine plays now, by id (0 before any).</summary>
    internal long PlayingGraph { get; private set; }

    /// <summary>Tests only: the last graph described, by id (0 before any).</summary>
    internal long DescribedGraph => Interlocked.Read(ref _nextGraph);

    void IEngineClient.Connected(IEngineLink link, bool restarted)
    {
        lock (_gate)
        {
            _link = link;
            _status = null;
            _graphLink = null;
            Volatile.Write(ref _meters, Meters.None);
            if (_wanted is { } wanted)
            {
                // The graph follows once the engine reports the stream it opened.
                var message = SetAudio(wanted);
                _startRequest = message.Request;
                link.Send(message);
            }
        }
    }

    void IEngineClient.Received(IEngineLink link, EngineMessage message)
    {
        switch (message)
        {
            case AudioDeviceStatus status:
                OnStatus(link, status);
                break;
            case AudioGraphLoaded loaded:
                OnGraphLoaded(link, loaded);
                break;
            case AudioStatus status:
                lock (_gate)
                {
                    if (!ReferenceEquals(link, _link))
                    {
                        return;
                    }
                }

                var strips = new Dictionary<Guid, MeterReading>(status.Strips.Length);
                foreach (var strip in status.Strips)
                {
                    strips[strip.Key] = Reading(strip.Levels);
                }

                Volatile.Write(ref _meters, new Meters(Reading(status.Master), strips, status.Counters, status.Live));
                break;
            case TestRendered rendered:
                TaskCompletionSource<string?>? done;
                lock (_gate)
                {
                    _renders.Remove(rendered.Id, out done);
                }

                done?.TrySetResult(rendered.Problem);
                break;
        }
    }

    void IEngineClient.Disconnected(IEngineLink link)
    {
        List<TaskCompletionSource<string?>> renders;
        lock (_gate)
        {
            if (!ReferenceEquals(link, _link))
            {
                return;
            }

            _link = null;
            _status = null;
            _graphLink = null;
            Volatile.Write(ref _meters, Meters.None);
            renders = [.. _renders.Values];
            _renders.Clear();
        }

        foreach (var render in renders)
        {
            render.TrySetResult("The engine stopped.");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatus(IEngineLink link, AudioDeviceStatus status)
    {
        bool describe;
        int? prepare = null;
        lock (_gate)
        {
            if (!ReferenceEquals(link, _link))
            {
                return;
            }

            var previous = _status;
            _status = status;

            var open = status.State is AudioDeviceState.Running or AudioDeviceState.Fallback;
            describe = open && (!ReferenceEquals(_graphLink, link) || previous is null || previous.SampleRate != status.SampleRate || previous.BlockFrames != status.BlockFrames
                || previous.State is not (AudioDeviceState.Running or AudioDeviceState.Fallback));
            if (open && status.SampleRate != _preparedRate && _prepareSampleRate is not null)
            {
                _preparedRate = status.SampleRate;
                prepare = status.SampleRate;
            }
        }

        if (prepare is { } rate)
        {
            _ = PrepareAsync(rate);
        }
        else if (describe)
        {
            _ = Task.Run(Rebuild);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnGraphLoaded(IEngineLink link, AudioGraphLoaded loaded)
    {
        List<string> stale;
        bool changed = false;
        lock (_gate)
        {
            stale = [.. _graphFiles.Where(g => g.Id < loaded.GraphId).Select(g => g.Path)];
            _graphFiles.RemoveAll(g => g.Id < loaded.GraphId);
            if (ReferenceEquals(link, _link) && loaded.Applied)
            {
                PlayingGraph = loaded.GraphId;
                _latencyFrames = loaded.LatencyFrames;
                changed = !loaded.Problems.SequenceEqual(_engineProblems);
                _engineProblems = loaded.Problems;
            }
        }

        foreach (var path in stale)
        {
            TryDelete(path);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task PrepareAsync(int rate)
    {
        try
        {
            await _prepareSampleRate!(rate).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Plugins that cannot follow the rate stay silent and say so in the graph's problems.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning($"Plugins could not be restarted at {rate} Hz: {ex.Message}");
        }

        Rebuild();
    }

    // Under _gate.
    private SetAudio SetAudio(AudioSettings settings) =>
        new(++_request, true, settings.DeviceId, settings.SampleRate, settings.BufferFrames, settings.EngineQueuedFrames, _options.MaxBlockFrames, _testTone, settings.EngineLiveQueuedFrames);

    // A plugin in the graph that restarts (a new exchange) or reports a new latency is described again. Under _rebuild.
    private void Watch(AudioGraphDescription description)
    {
        var current = description.Nodes
            .Where(n => n.Kind == DescribedNodeKind.Plugin)
            .Select(n => _instances(new DeviceId(n.Key)))
            .OfType<PluginInstance>()
            .ToHashSet();
        foreach (var gone in _watched.Where(i => !current.Contains(i)).ToList())
        {
            gone.StatusChanged -= OnInstanceStatus;
            gone.Changed -= OnInstanceChanged;
            _watched.Remove(gone);
        }

        foreach (var added in current.Where(_watched.Add))
        {
            added.StatusChanged += OnInstanceStatus;
            added.Changed += OnInstanceChanged;
        }
    }

    private void OnInstanceStatus(object? sender, PluginStatusChangedEventArgs e) => _ = Task.Run(Rebuild);

    private void OnInstanceChanged(object? sender, NotificationKind kind)
    {
        if (kind == NotificationKind.LatencyChanged)
        {
            _ = Task.Run(Rebuild);
        }
    }

    private static MeterReading Reading(in MeterSpec meter) => new(meter.PeakLeft, meter.PeakRight, meter.RmsLeft, meter.RmsRight);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder goes when Bluestone closes.
        }
    }

    /// <summary>The meters and figures the engine last reported.</summary>
    private sealed record Meters(MeterReading Master, Dictionary<Guid, MeterReading> Strips, AudioCounters? Counters, LiveLatencySpec Live = default)
    {
        public static Meters None { get; } = new(default, [], null);
    }
}
