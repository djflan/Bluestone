using Bluestone.Audio.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Signal;
using Bluestone.Signal.Live;

namespace Bluestone.Application.Live;

/// <summary>What live routes are built from.</summary>
internal sealed record LiveContext(
    Project Project,
    DeviceCatalog Devices,
    IReadOnlyDictionary<EndpointId, IMidiOutput> Outputs,
    Func<DeviceId, PluginInstance?> Plugins,
    IReadOnlyDictionary<DeviceId, PluginNode>? Nodes,
    PlaybackEngine Engine)
{
    public Ppqn Ppqn => Project.Sequence.Ppqn;
}

/// <summary>
/// Live input (ADR 0035): notes played on a MIDI keyboard (thru and recording), the on-screen keyboard, and auditioned
/// notes go through every device on the track's live route as they are played: built-in devices in their real-time
/// form, plugin MIDI effects through their own running instance, and then to the instrument: MIDI out to an external
/// instrument, or into a plugin instrument, heard through the audio engine. Recording is not affected: it captures the
/// input before any device, as before.
/// </summary>
/// <remarks>
/// One <see cref="LiveRunner"/> per track that has been played (and the monitored track). Routes are worked out again
/// after every refresh; a route that changed (a device added, removed, bypassed, or edited, a connection, the target)
/// releases every note it sounds before the new one takes over, so nothing can hang. Routes to external instruments run
/// on the thread that posts the input (so thru stays immediate) and on the live thread every millisecond for what
/// happens later; routes to plugin instruments run on the audio thread. Methods are thread-safe.
/// </remarks>
public sealed class LiveInput : IDisposable
{
    /// <summary>The block a plugin MIDI effect gets when the live thread runs it (1.3 ms at 48 kHz).</summary>
    public const int EffectBlockFrames = 64;

    private readonly PlaybackEngine _engine;
    private readonly IMonotonicClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<TrackId, LiveRunner> _runners = [];
    private readonly AutoResetEvent _wake = new(false);
    private Project? _project;
    private DeviceCatalog _devices = DeviceCatalog.BuiltIn;
    private Func<ExternalInstrumentId, string?, EndpointDescriptor?> _endpoints = (_, _) => null;
    private IReadOnlyDictionary<EndpointId, IMidiOutput> _outputs = new Dictionary<EndpointId, IMidiOutput>();
    private Func<DeviceId, PluginInstance?> _plugins = _ => null;
    private IReadOnlyDictionary<DeviceId, PluginInstance> _instances = new Dictionary<DeviceId, PluginInstance>();
    private IReadOnlyDictionary<DeviceId, PluginNode>? _nodes;
    private Func<PluginNode, int> _compensation = _ => 0;
    private TrackId? _monitored;
    private DeviceId? _monitoredInstrument;
    private long _lastSoftwareInput;
    private Thread? _thread;
    private volatile bool _running;

    public LiveInput(PlaybackEngine engine, IMonotonicClock clock)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Input-to-heard latency of notes played into plugin instruments, measured on the audio thread.</summary>
    public LiveLatencyMeter Latency { get; } = new();

    /// <summary>Raised (on the posting thread) whenever input goes to a plugin instrument: live monitoring is active.</summary>
    public event EventHandler? SoftwareInput;

    /// <summary>Raised when the plugin instrument the monitored track plays changes.</summary>
    public event EventHandler? MonitoredInstrumentChanged;

    /// <summary>When input last went to a plugin instrument, on the live clock; <see cref="TimeSpan.MinValue"/> if never.</summary>
    public TimeSpan LastSoftwareInput => Volatile.Read(ref _lastSoftwareInput) is var ticks && ticks == 0 ? TimeSpan.MinValue : TimeSpan.FromTicks(ticks);

    /// <summary>
    /// The track whose route is kept ready (the thru or recording track). Its plugin instrument, if any, is the one
    /// low-latency monitoring applies to.
    /// </summary>
    public TrackId? Monitored
    {
        get => _monitored;
        set
        {
            lock (_gate)
            {
                _monitored = value;
                if (value is { } track)
                {
                    RunnerFor(track);
                }
            }

            UpdateMonitoredInstrument();
        }
    }

    /// <summary>The plugin instrument the monitored track plays live, if any.</summary>
    public DeviceId? MonitoredInstrument => _monitoredInstrument;

    /// <summary>Plugin instances, for plugin MIDI effects on live routes (the plugin bridge).</summary>
    public void UsePlugins(Func<DeviceId, PluginInstance?> plugins)
    {
        lock (_gate)
        {
            _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
            RebuildLocked();
        }
    }

    /// <summary>
    /// The nodes the audio engine plays plugin instruments with, and the compensation delay after each, or null while no
    /// audio device is open (routes to plugin instruments are then silent).
    /// </summary>
    public void UseInstruments(IReadOnlyDictionary<DeviceId, PluginNode>? nodes, Func<PluginNode, int>? compensation = null)
    {
        lock (_gate)
        {
            _nodes = nodes;
            _compensation = compensation ?? (_ => 0);
            RebuildLocked();
        }
    }

    /// <summary>Works out every route again after a refresh. <paramref name="outputs"/> are the MIDI outputs open now.</summary>
    public void Update(Project project, DeviceCatalog devices, Func<ExternalInstrumentId, string?, EndpointDescriptor?> endpoints, IReadOnlyDictionary<EndpointId, IMidiOutput> outputs)
    {
        lock (_gate)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
            _outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
            RebuildLocked();
        }

        UpdateMonitoredInstrument();
    }

    /// <summary>The route live input on <paramref name="track"/> takes now.</summary>
    public LiveRoutePlan? PlanOf(TrackId track)
    {
        lock (_gate)
        {
            return RunnerFor(track)?.Plan;
        }
    }

    /// <summary>
    /// Plays <paramref name="message"/> on <paramref name="track"/>'s live route. <paramref name="source"/> is the input it
    /// came from: input is never sent back out to where it came from (an IAC bus used both ways would loop forever).
    /// </summary>
    /// <returns>Whether the route can play it (it reaches an instrument that is available now).</returns>
    public bool Post(TrackId track, ChannelMessage message, TimeSpan timestamp, EndpointDescriptor? source = null)
    {
        if (!LiveEvent.TryFromMidi(0, message.Status, message.Data1, message.Data2, out var e))
        {
            return false;
        }

        bool software;
        lock (_gate)
        {
            if (RunnerFor(track) is not { CanPlay: true } runner
                || (source is not null && runner.ExternalEndpoint is { } endpoint && IsFeedback(source, endpoint)))
            {
                return false;
            }

            runner.Enqueue(e, timestamp);
            software = runner.IsAudioDriven;
            if (!software)
            {
                runner.Pump(_clock.Now, Tempo());
            }
        }

        if (software)
        {
            Volatile.Write(ref _lastSoftwareInput, Math.Max(1, _clock.Now.Ticks));
            SoftwareInput?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _wake.Set();
        }

        return true;
    }

    /// <summary>Advances the routes the audio engine does not run to now. The live thread calls this every millisecond; tests call it.</summary>
    public void Pump()
    {
        lock (_gate)
        {
            var now = _clock.Now;
            var tempo = Tempo();
            var stopped = StoppedTempo();
            foreach (var runner in _runners.Values)
            {
                runner.StoppedTempo = stopped;
                runner.Pump(now, tempo);
            }
        }
    }

    /// <summary>Ends every live note now (panic): routes start again from nothing.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            foreach (var track in _runners.Keys.ToList())
            {
                Replace(track, Create(_runners[track].Plan));
            }
        }
    }

    /// <summary>Starts the live thread, which pumps routes to external instruments every millisecond.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "Bluestone live input", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Releases every live note and stops the live thread.</summary>
    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread?.Join();
        _thread = null;
        lock (_gate)
        {
            foreach (var runner in _runners.Values)
            {
                Retire(runner);
            }

            _runners.Clear();
        }

        _wake.Dispose();
    }

    internal LiveRunner? RunnerOf(TrackId track)
    {
        lock (_gate)
        {
            return _runners.GetValueOrDefault(track);
        }
    }

    private static bool IsFeedback(EndpointDescriptor input, EndpointDescriptor output) =>
        input.Id.Provider == output.Id.Provider && string.Equals(input.DisplayName, output.DisplayName, StringComparison.Ordinal);

    private void Run()
    {
        while (_running)
        {
            _wake.WaitOne(1);
            Pump();
        }
    }

    // The runner for a track, made when first needed. Under _gate.
    private LiveRunner? RunnerFor(TrackId track)
    {
        if (_runners.TryGetValue(track, out var runner))
        {
            return runner;
        }

        if (_project?.Sequence.FindTrack(track) is null)
        {
            return null;
        }

        runner = Create(LiveRoutePlan.Find(_project, track, _devices, _endpoints));
        Attach(runner);
        _runners[track] = runner;
        return runner;
    }

    private LiveRunner Create(LiveRoutePlan plan)
    {
        var runner = LiveRunner.Create(plan, new LiveContext(_project!, _devices, _outputs, id => _instances.GetValueOrDefault(id), _nodes, _engine), Latency);
        runner.StoppedTempo = StoppedTempo();
        return runner;
    }

    // Under _gate. Plugin instances are looked up here, on a control thread, never where input is posted.
    private void RebuildLocked()
    {
        if (_project is not { } project)
        {
            return;
        }

        var instances = new Dictionary<DeviceId, PluginInstance>();
        foreach (var device in project.Chains.SelectMany(c => c.Devices))
        {
            if (_plugins(device.Id) is { } instance)
            {
                instances[device.Id] = instance;
            }
        }

        _instances = instances;

        foreach (var track in _runners.Keys.ToList())
        {
            var current = _runners[track];
            if (project.Sequence.FindTrack(track) is null)
            {
                Retire(current);
                _runners.Remove(track);
                continue;
            }

            var plan = LiveRoutePlan.Find(project, track, _devices, _endpoints);
            var next = Create(plan);
            if (!plan.IsSameAs(current.Plan) || !SameBinding(current, next))
            {
                Replace(track, next);
            }
            else if (current.Node is { } node)
            {
                current.CompensationFrames = _compensation(node);
            }
        }

        if (_monitored is { } monitored)
        {
            RunnerFor(monitored);
        }
    }

    private static bool SameBinding(LiveRunner a, LiveRunner b) =>
        ReferenceEquals(a.Node, b.Node)
        && a.ExternalEndpoint?.Id == b.ExternalEndpoint?.Id
        && a.CanPlay == b.CanPlay
        && a.PluginStages.Select(s => s.Instance).SequenceEqual(b.PluginStages.Select(s => s.Instance));

    private void Replace(TrackId track, LiveRunner next)
    {
        if (_runners.TryGetValue(track, out var old))
        {
            Retire(old);
        }

        Attach(next);
        _runners[track] = next;
    }

    private void Attach(LiveRunner runner)
    {
        if (runner.Node is { } node)
        {
            runner.CompensationFrames = _compensation(node);
            node.LiveSource = runner;
        }
    }

    // The audio thread releases what a node's runner sounds when its source is replaced; others release here and now.
    private static void Retire(LiveRunner runner)
    {
        if (runner.Node is { } node)
        {
            if (ReferenceEquals(node.LiveSource, runner))
            {
                node.LiveSource = null;
            }
        }
        else
        {
            runner.ReleaseNow();
        }
    }

    private void UpdateMonitoredInstrument()
    {
        DeviceId? instrument;
        lock (_gate)
        {
            instrument = _monitored is { } track && _runners.GetValueOrDefault(track)?.Plan is { Target: LiveTargetKind.Software } plan ? plan.Instrument : null;
        }

        if (instrument != _monitoredInstrument)
        {
            _monitoredInstrument = instrument;
            MonitoredInstrumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // The transport's tempo now while it plays, else the tempo at the playhead.
    private double Tempo() => _engine.TryGetSongTimeAt(_clock.Now, out var song) ? song.BeatsPerMinute : StoppedTempo();

    private double StoppedTempo() =>
        _project?.Sequence.TempoMap is { } tempo ? tempo.TempoAt(new Tick(Math.Max(0, _engine.Position.Value))).BeatsPerMinute : 120;
}
