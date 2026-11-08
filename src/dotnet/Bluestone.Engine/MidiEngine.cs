using System.Collections.Immutable;
using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Signal.Live;
using ExchangeOptions = Bluestone.Plugins.Protocol.Exchange.ExchangeOptions;

namespace Bluestone.Engine;

/// <summary>
/// MIDI in the engine process (ADR 0044, migration step 5): the playback engine and its thread, the outputs and inputs
/// Bluestone asks for, the live routes to external instruments, and thru from the engine's inputs. Bluestone's commands
/// arrive on the control thread (<see cref="Apply"/>); the sender thread reports back (<see cref="Report"/>).
/// </summary>
/// <remarks>
/// <para>
/// The playback thread, MIDI input callbacks, and the live thread allocate nothing in steady state: input is played on
/// its route under a short lock and copied into the <see cref="OutboundRing"/>, and so is MIDI for forwarded outputs.
/// </para>
/// <para>
/// An engine started after a crash (<c>--restarted</c>) sends All Notes Off on every output it opens, before anything
/// else is sent there: sustain off, All Notes Off, and All Sound Off on all 16 channels. Its transport starts stopped.
/// </para>
/// </remarks>
internal sealed class MidiEngine : IDisposable
{
    private static readonly TimeSpan PlayingStatusInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan IdleStatusInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TimingInterval = TimeSpan.FromMilliseconds(250);

    private readonly bool _restarted;
    private readonly SystemMonotonicClock _clock = SystemMonotonicClock.Instance;
    private readonly PlaybackThread? _thread;
    private readonly Dictionary<int, OutputEntry> _outputs = [];
    private readonly Dictionary<int, InputEntry> _inputs = [];
    private IMidiEndpointProvider? _provider;
    private bool _providerTried;

    // Live routes: the input callbacks, the live thread, and the control thread, under _routes.
    private readonly Lock _routes = new();
    private readonly Dictionary<int, EngineRoute> _routeById = [];
    // Every live route of each track (a layered chain, several outputs: ADR 0037), rebuilt off the input path.
    private readonly Dictionary<Guid, EngineRoute[]> _routeByTrack = [];
    private readonly Dictionary<PluginNode, LiveSourceSet> _players = [];
    private IReadOnlyDictionary<Guid, (PluginNode Node, int DelayToMaster)> _instruments = new Dictionary<Guid, (PluginNode, int)>();
    private readonly PluginRegistry _plugins;
    private Guid _thruTrack;
    private bool _hasThru;
    private readonly Thread? _liveThread;
    private readonly AutoResetEvent _liveWake = new(false);
    private volatile bool _running = true;

    // Plans: the control thread loads, the sender thread retires and acknowledges, under _plans.
    private readonly Lock _plans = new();
    private readonly List<(MappedPlan Plan, long Superseded)> _retiring = [];
    private readonly List<(TempoMap Tempo, long Id)> _planTempos = [];
    private MappedPlan? _current;
    private long _loadsRequested;
    private long? _unacknowledged;
    private TempoMap _tempo = TempoMap.Constant(Ppqn.Default, Bluestone.Domain.Time.Tempo.Default);

    // Transport commands given and not yet applied by the playback thread: (sequence, CommandsQueued after it), under _plans.
    private readonly Queue<(long Sequence, long Queued)> _pendingSequences = new();
    private long _appliedSequence;

    // The sender thread's view of what it last reported.
    private TransportSnapshot _reported;
    private long _reportedSequence;
    private TimeSpan _reportedAt;
    private TimeSpan _timingAt;
    private long _timingDispatched = -1;
    private readonly long[] _histogram = new long[TimingStatistics.BucketBounds.Length + 1];
    private readonly long[] _fine = new long[TimingStatistics.FineBucketCount];

    /// <param name="restarted">Started after a crash: sweep every output when it opens.</param>
    /// <param name="startThreads">False to pump the playback engine and routes by hand (the allocation check).</param>
    /// <param name="plugins">The plugin instances the engine drives, shared with the audio graph (plugin MIDI effects on routes).</param>
    /// <param name="meter">Where live notes into plugin instruments are measured.</param>
    public MidiEngine(bool restarted, bool startThreads = true, PluginRegistry? plugins = null, LiveMeter? meter = null)
    {
        _restarted = restarted;
        _plugins = plugins ?? new PluginRegistry(restarted);
        Meter = meter ?? new LiveMeter();
        Playback = new PlaybackEngine(_clock, _tempo);
        if (startThreads)
        {
            _thread = new PlaybackThread(Playback, EnginePlatform.ConfigurePlaybackThread, EnginePlatform.PlaybackPreciseSleep);
            _liveThread = new Thread(RunLive) { IsBackground = true, Name = "Bluestone engine live", Priority = ThreadPriority.AboveNormal };
            _liveThread.Start();
        }
    }

    public PlaybackEngine Playback { get; }

    /// <summary>Live notes into plugin instruments, measured where they are heard.</summary>
    public LiveMeter Meter { get; }

    public OutboundRing Outbound { get; } = new();

    /// <summary>The playback thread's fault, if it had one: a crash (ADR 0044).</summary>
    public Exception? Fault => _thread?.Fault;

    /// <summary>Applies one command from Bluestone. The control thread only.</summary>
    /// <returns>Problems to report: devices that could not be opened, say.</returns>
    public IReadOnlyList<EngineProblem> Apply(EngineMessage message)
    {
        var problems = new List<EngineProblem>();
        try
        {
            switch (message)
            {
                case OpenOutput open:
                    Open(open, problems);
                    break;
                case CloseOutput close:
                    if (_outputs.Remove(close.Handle, out var closing))
                    {
                        closing.Output.Dispose();
                    }

                    break;
                case SetSlots slots:
                    Playback.SetOutputs([.. slots.Handles.Select(h => h >= 0 && _outputs.TryGetValue(h, out var o) ? o.Output : null)]);
                    break;
                case LoadPlan load:
                    Load(load);
                    break;
                case TransportPlay play:
                    Playback.Play(play.From < 0 ? null : new Tick(play.From), play.CountIn is { } c
                        ? new CountIn(TimeSpan.FromTicks(c.Duration), [.. c.Clicks.Select(k => new CountInClick(TimeSpan.FromTicks(k.Offset), k.Downbeat))])
                        : null);
                    Given(play.Sequence);
                    break;
                case TransportStop stop:
                    Playback.Stop();
                    Given(stop.Sequence);
                    break;
                case TransportSeek seek:
                    Playback.Seek(new Tick(seek.Position));
                    Given(seek.Sequence);
                    break;
                case SetLoop loop:
                    Playback.SetLoop(loop.Active ? new LoopRegion(new Tick(loop.Start), new Tick(loop.End)) : null);
                    break;
                case SetMetronome metronome:
                    Playback.SetMetronome(metronome.Click is { } k ? Click(k) : null, metronome.Enabled);
                    break;
                case Panic:
                    Playback.Panic();
                    break;
                case Nudge nudge:
                    Playback.Nudge(TimeSpan.FromTicks(nudge.Amount));
                    break;
                case OutputSend send:
                    Send(send);
                    break;
                case SetRoute route:
                    SetRoute(route, problems);
                    break;
                case RemoveRoute remove:
                    lock (_routes)
                    {
                        if (_routeById.Remove(remove.Route, out var removed))
                        {
                            Retire(removed);
                            Index(removed.Track);
                        }

                        UseRoutePlugins();
                    }

                    break;
                case RouteInput input:
                    if (LiveEvent.TryFromMidi(0, input.Status, input.Data1, input.Data2, out var e))
                    {
                        lock (_routes)
                        {
                            if (_routeById.TryGetValue(input.Route, out var target))
                            {
                                Play(target, e, TimeSpan.FromTicks(input.Timestamp));
                            }
                        }
                    }

                    break;
                case SetThru thru:
                    lock (_routes)
                    {
                        _hasThru = thru.Track is not null;
                        _thruTrack = thru.Track ?? Guid.Empty;
                    }

                    break;
                case SetInputs inputs:
                    SetInputs(inputs, problems);
                    break;
                default:
                    throw new EngineProtocolException($"{message.Type} is not a command for the engine.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or EndpointUnavailableException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            problems.Add(new EngineProblem(Handle(message), ex.Message));
        }

        return problems;
    }

    /// <summary>
    /// What to tell Bluestone now: MIDI for forwarded outputs and from inputs, the transport when it changed (and
    /// regularly while playing), timing figures now and then, and plans that are loaded. The sender thread only.
    /// </summary>
    public void Report(Action<EngineMessage> send)
    {
        while (Outbound.Drain() is { } batch)
        {
            send(batch);
        }

        // Which transport commands the playback thread has applied, read before the transport so the report reflects them.
        var commandsApplied = Playback.CommandsApplied;
        long sequence;
        lock (_plans)
        {
            while (_pendingSequences.TryPeek(out var pending) && pending.Queued <= commandsApplied)
            {
                _appliedSequence = Math.Max(_appliedSequence, pending.Sequence);
                _pendingSequences.Dequeue();
            }

            sequence = _appliedSequence;
        }

        var now = _clock.Now;
        var transport = Playback.ReadTransport();
        var playing = transport.State == TransportState.Playing;
        if (sequence != _reportedSequence || transport.Version != _reported.Version || transport.State != _reported.State || transport.Position != _reported.Position
            || transport.LoopWraps != _reported.LoopWraps || transport.CountInUntil != _reported.CountInUntil
            || now - _reportedAt >= (playing ? PlayingStatusInterval : IdleStatusInterval))
        {
            _reported = transport;
            _reportedSequence = sequence;
            _reportedAt = now;
            send(new TransportStatus(playing, transport.Position.Value, transport.LoopWraps, transport.CountInUntil.Ticks, Anchor(transport.Current), Anchor(transport.Previous), sequence));
        }

        if (now - _timingAt >= TimingInterval && Timing() is { } timing)
        {
            _timingAt = now;
            send(timing);
        }

        long? loaded = null;
        lock (_plans)
        {
            var applied = Playback.LoadsApplied;
            for (var i = _retiring.Count - 1; i >= 0; i--)
            {
                if (_retiring[i].Superseded <= applied)
                {
                    ((IDisposable)_retiring[i].Plan).Dispose();
                    _retiring.RemoveAt(i);
                }
            }

            if (_unacknowledged is { } id && applied >= _loadsRequested && _retiring.Count == 0)
            {
                loaded = id;
                _unacknowledged = null;
            }
        }

        if (loaded is { } done)
        {
            send(new PlanLoaded(done));
        }
    }

    /// <summary>Plays a message from one of the engine's inputs: thru on the thru track's route, and on to Bluestone. Allocation-free.</summary>
    internal void Receive(InputEntry input, ReadOnlySpan<byte> bytes, TimeSpan timestamp)
    {
        if (bytes.IsEmpty || bytes[0] >= 0xF8)
        {
            // Clock and active sensing are neither played nor recorded.
            return;
        }

        if (bytes.Length >= 2 && bytes[0] is >= 0x80 and < 0xF0
            && ChannelMessage.TryCreate(bytes[0], bytes[1], bytes.Length > 2 ? bytes[2] : (byte)0, out var message)
            && LiveEvent.TryFromMidi(0, message.Status, message.Data1, message.Data2, out var e))
        {
            lock (_routes)
            {
                Thru(input, message, e, timestamp);
            }
        }

        Outbound.TryWrite(MidiEventKind.Input, input.Handle, timestamp.Ticks, bytes);
    }

    /// <summary>Advances every route to now. The live thread every millisecond; the allocation check by hand.</summary>
    internal void PumpRoutes()
    {
        lock (_routes)
        {
            var now = _clock.Now;
            var tempo = Tempo();
            var stopped = StoppedTempo();
            foreach (var route in _routeById.Values)
            {
                route.StoppedTempo = stopped;
                route.Pump(now, tempo);
            }
        }
    }

    /// <summary>
    /// The plugin instruments of the audio graph playing now, by device, with the frames from each one's output to the
    /// master; routes into them become their live sources (rendered in the device callback). Called by the audio thread
    /// whenever it realizes a graph or the device closes.
    /// </summary>
    internal void UseInstruments(IReadOnlyDictionary<Guid, (PluginNode Node, int DelayToMaster)> instruments)
    {
        lock (_routes)
        {
            _instruments = instruments;
            foreach (var route in _routeById.Values)
            {
                Bind(route);
            }
        }
    }

    /// <summary>
    /// Plays a channel message on <paramref name="track"/>'s route as input received at <paramref name="timestamp"/>
    /// would be. Tests (<see cref="TestRender"/>) and the allocation check. Allocation-free.
    /// </summary>
    internal bool PostLive(Guid track, byte status, byte data1, byte data2, TimeSpan timestamp)
    {
        if (!LiveEvent.TryFromMidi(0, status, data1, data2, out var e))
        {
            return false;
        }

        lock (_routes)
        {
            if (!_routeByTrack.TryGetValue(track, out var routes))
            {
                return false;
            }

            var played = false;
            foreach (var route in routes)
            {
                if (!route.Released)
                {
                    Play(route, e, timestamp);
                    played = true;
                }
            }

            return played;
        }
    }

    /// <summary>An input the allocation check feeds by hand, as an input callback would.</summary>
    internal InputEntry AddTestInput(int handle, string provider, string name)
    {
        var entry = new InputEntry(handle, null, provider, name);
        _inputs[handle] = entry;
        return entry;
    }

    public void Dispose()
    {
        _running = false;
        _liveWake.Set();
        _liveThread?.Join();
        foreach (var input in _inputs.Values)
        {
            input.Input?.SetReceiver(null);
            input.Input?.Dispose();
        }

        _inputs.Clear();
        lock (_routes)
        {
            foreach (var route in _routeById.Values)
            {
                Retire(route);
            }

            _routeById.Clear();
            _routeByTrack.Clear();
        }

        Playback.Stop();
        if (_thread is not null)
        {
            _thread.Dispose();
        }
        else
        {
            Playback.Pump();
        }

        foreach (var output in _outputs.Values)
        {
            output.Output.Dispose();
        }

        _outputs.Clear();
        lock (_plans)
        {
            foreach (var (plan, _) in _retiring)
            {
                ((IDisposable)plan).Dispose();
            }

            _retiring.Clear();
            if (_current is not null)
            {
                ((IDisposable)_current).Dispose();
                _current = null;
            }
        }

        _provider?.Dispose();
        Playback.Dispose();
        Outbound.Dispose();
        _liveWake.Dispose();
    }

    // A transport command was just queued: it counts as applied once the playback thread has applied everything queued so far.
    private void Given(long sequence)
    {
        lock (_plans)
        {
            _pendingSequences.Enqueue((sequence, Playback.CommandsQueued));
        }
    }

    private static int Handle(EngineMessage message) => message switch
    {
        OpenOutput open => open.Handle,
        CloseOutput close => close.Handle,
        OutputSend send => send.Handle,
        _ => -1,
    };

    private static MetronomeClick Click(MetronomeSpec spec) => new(spec.Slot)
    {
        Channel = MidiChannel.FromIndex(spec.Channel),
        AccentNote = new NoteNumber(spec.AccentNote),
        AccentVelocity = new Velocity(spec.AccentVelocity),
        BeatNote = new NoteNumber(spec.BeatNote),
        BeatVelocity = new Velocity(spec.BeatVelocity),
        Length = TimeSpan.FromTicks(spec.Length),
    };

    private static bool IsFeedback(InputEntry input, EngineRoute route) =>
        route.Output is { } output && output.Endpoint.Id.Provider == input.Provider && string.Equals(output.Endpoint.DisplayName, input.Name, StringComparison.Ordinal);

    // The plugin MIDI effect of a route: the registry's instance for the device, at depth 1 (it only ever plays live input).
    private AttachedPluginInstance Effect(PluginStageSpec spec, List<EngineProblem> problems)
    {
        var shape = new ExchangeOptions(spec.MaxFrames, spec.InputChannels, spec.OutputChannels, spec.SlotCount, spec.EventCapacity, spec.ParameterCapacity);
        var plane = spec.ExchangePath is { } path && Path.IsPathRooted(path) ? new PluginDataPlane(path, spec.Generation, shape) : null;
        var link = new DescribedPluginLink(shape, spec.PipelineDepth, TimeSpan.FromTicks(spec.CollectWait), false, PipelineLatency.ReportedLatencyFrames(spec.LatencyFrames, spec.PipelineDepth, spec.MaxFrames), plane);
        var messages = new List<string>();
        var instance = _plugins.Attach(spec.Device, spec.Name, link, messages);
        problems.AddRange(messages.Select(m => new EngineProblem(-1, m)));
        return instance;
    }

    // Plays input on a route: at once to an output, at the next audio block into an instrument. Under _routes.
    private void Play(EngineRoute route, in LiveEvent e, TimeSpan timestamp)
    {
        route.Post(e, timestamp);
        if (route.Instrument is null)
        {
            route.Pump(_clock.Now, Tempo());
        }
        else
        {
            Meter.MarkInput(_clock.Now);
        }
    }

    // A route into an instrument plays through that instrument's node in the graph, when it has one. Under _routes.
    private void Bind(EngineRoute route)
    {
        if (route.Instrument is { } instrument)
        {
            var found = _instruments.TryGetValue(instrument, out var entry);
            route.Bind(found ? entry.Node : null, found ? entry.DelayToMaster : 0, _players);
        }
    }

    // Ends a route: an output's notes now, an instrument's at its next audio block. Under _routes.
    private void Retire(EngineRoute route)
    {
        route.ReleaseNow();
        route.Unbind(_players);
    }

    // The registry keeps the plugin effects that routes use. Under _routes.
    private void UseRoutePlugins() => _plugins.Use(graph: false, _routeById.Values.SelectMany(r => r.PluginDevices));

    private void Open(OpenOutput open, List<EngineProblem> problems)
    {
        if (_outputs.Remove(open.Handle, out var replaced))
        {
            replaced.Output.Dispose();
        }

        var id = new EndpointId(open.Provider, open.Key);
        IMidiOutput output;
        if (open.Forwarded)
        {
            var capabilities = EndpointCapabilities.SystemExclusive | (open.Scheduled ? EndpointCapabilities.ScheduledDelivery : EndpointCapabilities.None);
            output = new ForwardingOutput(open.Handle, new EndpointDescriptor(id, open.Name, EndpointDirection.Output, EndpointTransport.Software, capabilities), Outbound);
        }
        else if (Provider() is { } provider && provider.Id == open.Provider)
        {
            output = provider.OpenOutputAsync(id).AsTask().GetAwaiter().GetResult();
        }
        else
        {
            problems.Add(new EngineProblem(open.Handle, $"The engine cannot open {open.Name}: no {open.Provider} provider."));
            return;
        }

        if (_restarted)
        {
            Sweep(output);
        }

        _outputs[open.Handle] = new OutputEntry(output);
    }

    // All Notes Off before anything else: hardware does not release notes when a port closes (ADR 0044).
    private static void Sweep(IMidiOutput output)
    {
        Span<byte> message = stackalloc byte[3];
        for (var channel = 0; channel < 16; channel++)
        {
            foreach (var controller in (ReadOnlySpan<byte>)[64, 123, 120])
            {
                message[0] = (byte)(0xB0 | channel);
                message[1] = controller;
                message[2] = 0;
                output.Send(message, MidiTimestamp.Immediate);
            }
        }
    }

    private void Load(LoadPlan load)
    {
        var mapped = MappedPlan.Open(load.PlanId, load.Path);
        lock (_plans)
        {
            _planTempos.Add((mapped.Plan.TempoMap, load.PlanId));
            if (_planTempos.Count > 8)
            {
                _planTempos.RemoveAt(0);
            }

            Volatile.Write(ref _tempo, mapped.Plan.TempoMap);
            Playback.Load(mapped.Plan);
            _loadsRequested++;
            if (_current is not null)
            {
                _retiring.Add((_current, _loadsRequested));
            }

            _current = mapped;
            _unacknowledged = load.PlanId;
        }
    }

    private void Send(OutputSend send)
    {
        if (!_outputs.TryGetValue(send.Handle, out var entry))
        {
            return;
        }

        var bytes = send.Bytes.Span;
        if (bytes.Length is 2 or 3 && bytes[0] is >= 0x80 and < 0xF0
            && ChannelMessage.TryCreate(bytes[0], bytes[1], bytes.Length > 2 ? bytes[2] : (byte)0, out var message))
        {
            Playback.SendNow(entry.Output, message);
        }
        else
        {
            entry.Output.Send(bytes, MidiTimestamp.Immediate);
        }
    }

    private void SetRoute(SetRoute spec, List<EngineProblem> problems)
    {
        IMidiOutput? output = null;
        if (spec.Instrument is null)
        {
            if (!_outputs.TryGetValue(spec.Output, out var entry))
            {
                problems.Add(new EngineProblem(-1, "A live route's output is not open in the engine."));
                return;
            }

            output = entry.Output;
        }

        lock (_routes)
        {
            var route = EngineRoute.Create(spec, output, Playback, Meter, effect => Effect(effect, problems), out var unknown);
            if (_routeById.Remove(spec.Route, out var previous))
            {
                Retire(previous);
            }

            _routeById[spec.Route] = route;
            Index(spec.Track);
            route.StoppedTempo = StoppedTempo();
            Bind(route);
            UseRoutePlugins();
            if (unknown is not null)
            {
                problems.Add(new EngineProblem(-1, $"The engine has no live processor for {unknown}; it passes events through."));
            }
        }
    }

    // Under _routes, on the control thread: the track's routes, in the order Bluestone added them (nearest first).
    // A track's routes, oldest first, that thru plays. While Bluestone replaces a route (it adds the new one before it
    // removes the old one, so a plugin effect on both stays loaded), the older of two routes to the same target through the
    // same stages is left out: a note is never played twice, and retiring the old one cannot cut off the new one's note.
    private void Index(Guid track)
    {
        var all = _routeById.OrderBy(r => r.Key).Select(r => r.Value).Where(r => r.Track == track).ToList();
        EngineRoute[] routes = [.. all.Where((r, i) => !all.Skip(i + 1).Any(newer => newer.SameTargetAs(r)))];
        if (routes.Length == 0)
        {
            _routeByTrack.Remove(track);
        }
        else
        {
            _routeByTrack[track] = routes;
        }
    }

    private void SetInputs(SetInputs spec, List<EngineProblem> problems)
    {
        var wanted = spec.Inputs.ToDictionary(i => i.Handle);
        foreach (var handle in _inputs.Keys.Where(h => !wanted.ContainsKey(h)).ToList())
        {
            _inputs.Remove(handle, out var closing);
            closing!.Input?.SetReceiver(null);
            closing.Input?.Dispose();
            ReleaseHeld(closing);
        }

        foreach (var input in spec.Inputs.Where(i => !_inputs.ContainsKey(i.Handle)))
        {
            if (Provider() is not { } provider || provider.Id != input.Provider)
            {
                problems.Add(new EngineProblem(input.Handle, $"The engine cannot open {input.Name}: no {input.Provider} provider."));
                continue;
            }

            try
            {
                var opened = provider.OpenInputAsync(new EndpointId(input.Provider, input.Key)).AsTask().GetAwaiter().GetResult();
                var entry = new InputEntry(input.Handle, opened, input.Provider, input.Name);
                _inputs[input.Handle] = entry;
                opened.SetReceiver((bytes, timestamp) => Receive(entry, bytes, timestamp));
            }
            catch (EndpointUnavailableException ex)
            {
                problems.Add(new EngineProblem(input.Handle, ex.Message));
            }
        }
    }

    // Under _routes.
    private void Thru(InputEntry input, ChannelMessage message, in LiveEvent e, TimeSpan timestamp)
    {
        var slot = (message.Channel.Index << 7) | message.Data1;
        if (message.IsNoteOff)
        {
            // A release goes wherever its note went, even if the thru track changed in between.
            if (input.Held[slot] is { } holding)
            {
                input.Held[slot] = null;
                foreach (var route in holding)
                {
                    if (!IsFeedback(input, route))
                    {
                        Play(route, e, timestamp);
                    }
                }
            }

            return;
        }

        if (!_hasThru || !_routeByTrack.TryGetValue(_thruTrack, out var routes))
        {
            return;
        }

        foreach (var route in routes)
        {
            if (!route.Released && !IsFeedback(input, route))
            {
                Play(route, e, timestamp);
            }
        }

        if (message.IsNoteOn)
        {
            input.Held[slot] = routes;
        }
    }

    private void ReleaseHeld(InputEntry input)
    {
        lock (_routes)
        {
            for (var slot = 0; slot < input.Held.Length; slot++)
            {
                if (input.Held[slot] is { } routes)
                {
                    input.Held[slot] = null;
                    foreach (var route in routes)
                    {
                        // As for an ordinary release: a route the note was not played on (feedback) gets no note-off.
                        if (!IsFeedback(input, route))
                        {
                            Play(route, LiveEvent.NoteOff(0, slot >> 7, slot & 127), _clock.Now);
                        }
                    }
                }
            }
        }
    }

    private IMidiEndpointProvider? Provider()
    {
        if (!_providerTried)
        {
            _providerTried = true;
            _provider = EnginePlatform.CreateProvider(_clock);
        }

        return _provider;
    }

    // The transport's tempo now while it plays, else the tempo at the playhead.
    private double Tempo() =>
        Playback.TryGetSongTimeAt(_clock.Now, out var song)
            ? song.BeatsPerMinute
            : StoppedTempo();

    private double StoppedTempo() => Volatile.Read(ref _tempo).TempoAt(new Tick(Math.Max(0, Playback.Position.Value))).BeatsPerMinute;

    private AnchorSpec Anchor(in TransportAnchor anchor)
    {
        var plan = -1L;
        lock (_plans)
        {
            foreach (var (tempo, id) in _planTempos)
            {
                if (ReferenceEquals(tempo, anchor.Tempo))
                {
                    plan = id;
                }
            }
        }

        return new AnchorSpec(anchor.Playing, anchor.Time.Ticks, anchor.SongTime.Ticks, plan, anchor.LoopStart, anchor.LoopEnd);
    }

    private TimingStatus? Timing()
    {
        var figures = Playback.Statistics.Read(_histogram, _fine);
        if (figures.Dispatched == _timingDispatched)
        {
            return null;
        }

        _timingDispatched = figures.Dispatched;
        var fine = ImmutableArray.CreateBuilder<FineBucket>();
        for (var i = 0; i < _fine.Length; i++)
        {
            if (_fine[i] != 0)
            {
                fine.Add(new FineBucket(i, _fine[i]));
            }
        }

        return new TimingStatus(figures.Dispatched, figures.Late, figures.SkippedLateNotes, figures.Dropped, figures.NoteOverflow, figures.MaxLateness.Ticks, [.. _histogram], fine.ToImmutable());
    }

    private void RunLive()
    {
        while (_running)
        {
            _liveWake.WaitOne(1);
            PumpRoutes();
        }
    }

    private sealed record OutputEntry(IMidiOutput Output);

    /// <summary>An open input, and for each channel and note the route its held note went to.</summary>
    internal sealed class InputEntry(int handle, IMidiInput? input, string provider, string name)
    {
        public int Handle { get; } = handle;

        public IMidiInput? Input { get; } = input;

        public string Provider { get; } = provider;

        public string Name { get; } = name;

        /// <summary>For each held key, the routes its note went to: the release goes there too.</summary>
        public EngineRoute[]?[] Held { get; } = new EngineRoute[]?[LiveEvent.NoteSlots];
    }
}
