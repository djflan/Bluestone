using System.Collections.Immutable;
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
    IPlaybackTransport Engine,
    ILiveRemote? Remote = null)
{
    /// <summary>
    /// The live clock's resolution: the sequence's, multiplied as far as it goes up to 64 times, so a tick is finer than a
    /// sample at usual tempos and events keep their frame through plugin stages. Devices measure musical lengths in it.
    /// </summary>
    public Ppqn Ppqn
    {
        get
        {
            var ppqn = Project.Sequence.Ppqn.TicksPerQuarterNote;
            return new Ppqn(ppqn * Math.Clamp(Ppqn.MaxValue / ppqn, 1, 64));
        }
    }
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

    private readonly IPlaybackTransport _engine;
    private readonly IMonotonicClock _clock;
    private readonly ILiveRemote? _remote;
    private readonly Lock _gate = new();
    // Every way each track's live input is heard (LiveRoutePlan.FindAll), nearest first; never empty.
    private readonly Dictionary<TrackId, LiveRunner[]> _runners = [];
    private readonly Dictionary<PluginNode, LiveSourceSet> _players = [];
    // Held keys change under _gate (then _heldGate, always in that order) and are read under _heldGate alone: the UI reads
    // them every frame and must never wait behind route processing.
    private readonly Dictionary<TrackId, HashSet<int>> _held = [];
    private readonly Lock _heldGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Project? _project;
    private DeviceCatalog _devices = DeviceCatalog.BuiltIn;
    private Func<ExternalInstrumentId, string?, EndpointDescriptor?> _endpoints = (_, _) => null;
    private IReadOnlyDictionary<EndpointId, IMidiOutput> _outputs = new Dictionary<EndpointId, IMidiOutput>();
    private Func<DeviceId, PluginInstance?> _plugins = _ => null;
    private IReadOnlyDictionary<DeviceId, PluginInstance> _instances = new Dictionary<DeviceId, PluginInstance>();
    private IReadOnlyDictionary<DeviceId, PluginNode>? _nodes;
    private Func<PluginNode, int> _delayToMaster = _ => 0;
    private TrackId? _monitored;
    private DeviceId? _monitoredInstrument;
    private ImmutableHashSet<DeviceId> _monitoredInstruments = [];
    private long _lastSoftwareInput;
    private Thread? _thread;
    private volatile bool _running;

    public LiveInput(IPlaybackTransport engine, IMonotonicClock clock)
        : this(engine, clock, null)
    {
    }

    /// <param name="engine">The transport, and where thru to external instruments is sent.</param>
    /// <param name="clock">The live clock.</param>
    /// <param name="remote">The engine process, which runs routes to external instruments with built-in devices (ADR 0044).</param>
    internal LiveInput(IPlaybackTransport engine, IMonotonicClock clock, ILiveRemote? remote)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _remote = remote;
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
                    RunnersFor(track);
                }
            }

            UpdateMonitoredInstrument();
        }
    }

    /// <summary>The nearest plugin instrument the monitored track plays live, if any.</summary>
    public DeviceId? MonitoredInstrument => _monitoredInstrument;

    /// <summary>Every plugin instrument the monitored track plays live (ADR 0037: a layered chain plays them all).</summary>
    public ImmutableHashSet<DeviceId> MonitoredInstruments => Volatile.Read(ref _monitoredInstruments);

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
    /// The nodes the audio engine plays plugin instruments with, and the frames from each one's output until the master
    /// puts it out (<see cref="Bluestone.Audio.AudioGraph.DelayToMasterOf"/>, for the latency meter), or null while no audio
    /// device is open (routes to plugin instruments are then silent).
    /// </summary>
    public void UseInstruments(IReadOnlyDictionary<DeviceId, PluginNode>? nodes, Func<PluginNode, int>? delayToMaster = null)
    {
        lock (_gate)
        {
            _nodes = nodes;
            _delayToMaster = delayToMaster ?? (_ => 0);
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

    /// <summary>The nearest way live input on <paramref name="track"/> is heard now (the first of <see cref="PlansOf"/>).</summary>
    public LiveRoutePlan? PlanOf(TrackId track)
    {
        lock (_gate)
        {
            return RunnersFor(track) is [var first, ..] ? first.Plan : null;
        }
    }

    /// <summary>Every way live input on <paramref name="track"/> is heard now, nearest first.</summary>
    public ImmutableArray<LiveRoutePlan> PlansOf(TrackId track)
    {
        lock (_gate)
        {
            return [.. (RunnersFor(track) ?? []).Select(r => r.Plan)];
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

        var software = false;
        var played = false;
        lock (_gate)
        {
            // Held keys count what arrives, whether or not the route can play it (callers always send the release). They
            // change under _gate, in order with panic and the routes; the UI reads them under _heldGate alone.
            Release(track, message);
            Press(track, message);
            foreach (var runner in RunnersFor(track) ?? [])
            {
                if (!runner.CanPlay || (source is not null && runner.ExternalEndpoint is { } endpoint && IsFeedback(source, endpoint)))
                {
                    continue;
                }

                played = true;
                if (runner.IsRemote)
                {
                    // The engine already played input from its own inputs on its thru routes; the rest is sent there.
                    if (runner.RemoteRoute >= 0 && !(source is not null && _remote!.IsEngineInput(source)))
                    {
                        _remote!.Post(runner.RemoteRoute, message, timestamp);
                    }

                    continue;
                }

                runner.Enqueue(e, timestamp);
                if (runner.IsAudioDriven)
                {
                    software = true;
                }
                else
                {
                    runner.Pump(_clock.Now, Tempo());
                }
            }
        }

        if (!played)
        {
            return false;
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
            foreach (var runners in _runners.Values)
            {
                foreach (var runner in runners)
                {
                    runner.StoppedTempo = stopped;
                    runner.Pump(now, tempo);
                }
            }
        }
    }

    /// <summary>
    /// Whether live notes are held on <paramref name="track"/> now (keys pressed and not released), for the note lamps of
    /// its chain (ADR 0037). Counts what arrives, whether or not the route could play it; All Notes Off and All Sound Off
    /// release a channel's keys. Any thread.
    /// </summary>
    public bool HasHeldNotes(TrackId track)
    {
        lock (_heldGate)
        {
            return _held.TryGetValue(track, out var keys) && keys.Count > 0;
        }
    }

    /// <summary>Ends every live note now (panic): routes start again from nothing.</summary>
    public void ReleaseAll()
    {
        lock (_gate)
        {
            lock (_heldGate)
            {
                _held.Clear();
            }

            foreach (var track in _runners.Keys.ToList())
            {
                Replace(track, [.. _runners[track].Select(r => Create(r.Plan))]);
            }
        }
    }

    // A key is channel × 128 + note. Under _gate; takes _heldGate.
    private void Press(TrackId track, ChannelMessage message)
    {
        if ((message.Status & 0xF0) != 0x90 || message.Data2 == 0)
        {
            return;
        }

        lock (_heldGate)
        {
            if (!_held.TryGetValue(track, out var keys))
            {
                _held[track] = keys = [];
            }

            keys.Add(((message.Status & 0x0F) * 128) + message.Data1);
        }
    }

    // A note-off, a note-on with velocity 0, or All Sound Off / All Notes Off (CC 120 / 123) for its channel. Under _gate; takes _heldGate.
    private void Release(TrackId track, ChannelMessage message)
    {
        lock (_heldGate)
        {
            ReleaseHeld(track, message);
        }
    }

    private void ReleaseHeld(TrackId track, ChannelMessage message)
    {
        if (!_held.TryGetValue(track, out var keys))
        {
            return;
        }

        var kind = message.Status & 0xF0;
        var channel = message.Status & 0x0F;
        if (kind == 0x80 || (kind == 0x90 && message.Data2 == 0))
        {
            keys.Remove((channel * 128) + message.Data1);
        }
        else if (kind == 0xB0 && message.Data1 is 120 or 123)
        {
            keys.RemoveWhere(key => key / 128 == channel);
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
            foreach (var runner in _runners.Values.SelectMany(r => r))
            {
                Retire(runner);
            }

            _runners.Clear();
        }

        _wake.Dispose();
    }

    /// <summary>Makes sure <paramref name="track"/>'s route is ready, so thru in the engine can play on it.</summary>
    internal void Prepare(TrackId track)
    {
        lock (_gate)
        {
            RunnersFor(track);
        }
    }

    /// <summary>The nearest way's runner (tests).</summary>
    internal LiveRunner? RunnerOf(TrackId track)
    {
        lock (_gate)
        {
            return _runners.GetValueOrDefault(track) is [var first, ..] ? first : null;
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

    // The runners for a track, made when first needed. Under _gate.
    private LiveRunner[]? RunnersFor(TrackId track)
    {
        if (_runners.TryGetValue(track, out var runners))
        {
            return runners;
        }

        if (_project?.Sequence.FindTrack(track) is null)
        {
            return null;
        }

        runners = [.. PlansFor(_project, track).Select(Create)];
        foreach (var runner in runners)
        {
            Attach(runner);
        }

        _runners[track] = runners;
        return runners;
    }

    // A track's live routes, leaving out any that would drive a plugin note effect another track's live route already runs
    // (two tracks into one rack): one instance, one route. The track whose routes were made first keeps it. Under _gate.
    private ImmutableArray<LiveRoutePlan> PlansFor(Project project, TrackId track, HashSet<DeviceId>? claimed = null)
    {
        var plans = LiveRoutePlan.FindAll(project, track, _devices, _endpoints);
        var elsewhere = claimed ?? _runners.Where(r => r.Key != track).SelectMany(r => r.Value).SelectMany(r => r.Plan.PluginStages).ToHashSet();
        if (elsewhere.Count == 0)
        {
            return plans;
        }

        ImmutableArray<LiveRoutePlan> kept = [.. plans.Where(p => !p.PluginStages.Any(elsewhere.Contains))];
        return kept.IsEmpty ? [new LiveRoutePlan(track, [], LiveTargetKind.None, null, null)] : kept;
    }

    private LiveRunner Create(LiveRoutePlan plan)
    {
        var runner = LiveRunner.Create(plan, new LiveContext(_project!, _devices, _outputs, id => _instances.GetValueOrDefault(id), _nodes, _engine, _remote), Latency);
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

        // Deleted tracks go first, so a plugin effect one of them ran is free for the tracks that remain.
        foreach (var track in _runners.Keys.Where(t => project.Sequence.FindTrack(t) is null).ToList())
        {
            foreach (var runner in _runners[track])
            {
                Retire(runner);
            }

            _runners.Remove(track);
        }

        // Plugin effects are claimed afresh in this pass, in the order the tracks' routes were first made, so an effect a
        // track no longer reaches is free for the next one at once.
        var claimed = new HashSet<DeviceId>();
        foreach (var track in _runners.Keys.ToList())
        {
            var current = _runners[track];
            var plans = PlansFor(project, track, claimed);
            claimed.UnionWith(plans.SelectMany(p => p.PluginStages));
            LiveRunner[] fresh = [.. plans.Select(Create)];

            // A route that is the same as before (same stages, same binding) keeps its runner, with the notes it holds: only
            // what changed is replaced.
            var next = fresh.Select(f => current.FirstOrDefault(c => c.Plan.IsSameAs(f.Plan) && SameBinding(c, f)) ?? f).ToArray();
            if (next.Length != current.Length || next.Zip(current).Any(p => !ReferenceEquals(p.First, p.Second)))
            {
                Replace(track, next);
            }

            foreach (var kept in next.Where(current.Contains))
            {
                if (kept.Node is { } node)
                {
                    kept.DelayToMasterFrames = _delayToMaster(node);
                }
            }
        }

        if (_monitored is { } monitored)
        {
            RunnersFor(monitored);
        }
    }

    private static bool SameBinding(LiveRunner a, LiveRunner b) =>
        ReferenceEquals(a.Node, b.Node)
        && a.IsRemote == b.IsRemote
        && (!a.IsRemote || ReferenceEquals(a.External, b.External))
        && a.ExternalEndpoint?.Id == b.ExternalEndpoint?.Id
        && a.CanPlay == b.CanPlay
        && a.PluginStages.Select(s => s.Instance).SequenceEqual(b.PluginStages.Select(s => s.Instance));

    // Old routes go before new ones come, so two generations of a route never play, pump, or drive a plugin effect at once.
    // Runners in both are kept as they are. The engine keeps a retired route's plugin effects loaded a moment, so its last
    // releases still reach them (MidiEngine.Retire).
    private void Replace(TrackId track, LiveRunner[] next)
    {
        var old = _runners.GetValueOrDefault(track) ?? [];
        foreach (var runner in old.Where(r => !next.Contains(r)))
        {
            Retire(runner);
        }

        foreach (var runner in next.Where(r => !old.Contains(r)))
        {
            Attach(runner);
        }

        _runners[track] = next;
    }

    // Every runner that plays a node is in the node's live source set, so several tracks can play one instrument.
    private void Attach(LiveRunner runner)
    {
        if (runner.IsRemote && runner.RemoteRoute < 0)
        {
            runner.RemoteRoute = _remote!.Add(runner.Plan, runner.External, new LiveContext(_project!, _devices, _outputs, _ => null, null, _engine).Ppqn, id => _instances.GetValueOrDefault(id));
        }

        if (runner.Node is { } node)
        {
            runner.DelayToMasterFrames = _delayToMaster(node);
            if (!_players.TryGetValue(node, out var players))
            {
                _players[node] = players = new LiveSourceSet();
            }

            players.Add(runner);
            node.LiveSource = players;
        }
    }

    // The audio thread releases what a node's runner sounds when it leaves the set; others release here and now.
    private void Retire(LiveRunner runner)
    {
        if (runner.Node is { } node)
        {
            if (_players.TryGetValue(node, out var players))
            {
                players.Remove(runner);
                if (players.Count == 0)
                {
                    _players.Remove(node);
                    if (ReferenceEquals(node.LiveSource, players))
                    {
                        node.LiveSource = null;
                    }
                }
            }
        }
        else if (runner.IsRemote)
        {
            if (runner.RemoteRoute >= 0)
            {
                _remote!.Remove(runner.RemoteRoute);
                runner.RemoteRoute = -1;
            }
        }
        else
        {
            runner.ReleaseNow();
        }
    }

    private void UpdateMonitoredInstrument()
    {
        ImmutableHashSet<DeviceId> instruments;
        DeviceId? instrument;
        lock (_gate)
        {
            var software = _monitored is { } track && _runners.TryGetValue(track, out var runners)
                ? runners.Select(r => r.Plan).Where(p => p.Target == LiveTargetKind.Software).Select(p => p.Instrument!.Value).ToList()
                : [];
            instrument = software.Count > 0 ? software[0] : null;
            instruments = [.. software];
        }

        if (instrument != _monitoredInstrument || !instruments.SetEquals(_monitoredInstruments))
        {
            _monitoredInstrument = instrument;
            Volatile.Write(ref _monitoredInstruments, instruments);
            MonitoredInstrumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // The transport's tempo now while it plays, else the tempo at the playhead.
    private double Tempo() => _engine.TryGetSongTimeAt(_clock.Now, out var song) ? song.BeatsPerMinute : StoppedTempo();

    private double StoppedTempo() =>
        _project?.Sequence.TempoMap is { } tempo ? tempo.TempoAt(new Tick(Math.Max(0, _engine.Position.Value))).BeatsPerMinute : 120;
}
