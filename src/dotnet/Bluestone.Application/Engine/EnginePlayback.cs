using System.Collections.Immutable;
using Bluestone.Application.Live;
using Bluestone.Application.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Signal;

namespace Bluestone.Application.Engine;

public sealed record EnginePlaybackOptions
{
    /// <summary>
    /// Whether the engine opens an endpoint itself. Others (Bluestone's monitor and test buses, the computer keyboard,
    /// its own virtual ports) stay in Bluestone: outputs are forwarded from the engine, input is played from here.
    /// </summary>
    public Func<EndpointDescriptor, bool> IsEngineEndpoint { get; init; } = _ => false;

    /// <summary>Where plan images are written for the engine; null for a folder of its own in the temporary folder.</summary>
    public string? PlanDirectory { get; init; }
}

/// <summary>
/// MIDI playback in the engine process (ADR 0044, migration step 5), as Bluestone sees it: a stand-in for the in-process
/// <see cref="PlaybackEngine"/>. Commands, plans, outputs, live routes, and inputs go to the engine; the transport the
/// engine reports is mirrored here, so <see cref="Position"/>, <see cref="TryGetTickAt"/>, and
/// <see cref="TryGetSongTimeAt"/> answer as the in-process engine would. After every (re)start of the engine,
/// everything it needs is sent again; the transport is then stopped, at the position it had.
/// </summary>
/// <remarks>
/// Plans cross as <see cref="PlanImage"/> files, which the engine maps read-only; a file is deleted once the engine has
/// moved past it. Both processes time MIDI with the system's monotonic clock, so timestamps compare directly.
/// </remarks>
public sealed class EnginePlayback : IPlaybackTransport, IEngineClient, ILiveRemote, IDisposable
{
    private const int KeptTempos = 16;

    private readonly EnginePlaybackOptions _options;
    private readonly Lock _gate = new();
    private readonly string _planDirectory;
    private readonly Dictionary<int, IEngineEndpoint> _endpoints = [];
    private readonly Dictionary<int, OpenOutput> _openOutputs = [];
    private readonly Dictionary<int, InputSpec> _openInputs = [];
    private readonly Dictionary<int, SetRoute> _routes = [];
    private readonly Dictionary<int, RemoteRoute> _remoteRoutes = [];
    private readonly List<(long Id, string Path)> _planFiles = [];
    private readonly Dictionary<long, TempoMap> _tempos = [];
    private readonly List<string> _problems = [];
    private IEngineLink? _link;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextHandle;
    private long _nextPlan;
    private SetSlots _slots = new([]);
    private LoadPlan? _plan;
    private SetLoop _loop = new(false, 0, 0);
    private SetMetronome _metronome = new(null, false);
    private SetThru _thru = new(null);
    private Mirror _mirror = Mirror.Stopped(0);
    private long _position;
    private long _countInUntil;

    // The last transport command sent, by sequence number. A report from the engine that has not applied it yet is
    // older than what Bluestone already shows, so it is ignored. Numbers keep rising across engine restarts.
    private long _transportSequence;
    private TimingSnapshot _timing = new(0, 0, 0, 0, 0, TimeSpan.Zero, [.. new long[TimingStatistics.BucketBounds.Length + 1]]);

    public EnginePlayback(EngineSupervisor supervisor, EnginePlaybackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        _options = options ?? new EnginePlaybackOptions();
        _planDirectory = _options.PlanDirectory ?? Path.Combine(Path.GetTempPath(), $"bluestone-engine-{Environment.ProcessId}");
        supervisor.Attach(this);
    }

    /// <summary>Raised (on the engine's reading thread) after the engine reports its transport.</summary>
    public event EventHandler? TransportChanged;

    /// <summary>The connected engine stopped (it crashed, hung, or was shut down); the transport is stopped. Any thread.</summary>
    public event EventHandler? EngineLost;

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

    public IMonotonicClock Clock => SystemMonotonicClock.Instance;

    public async Task<bool> WhenReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task ready;
        lock (_gate)
        {
            ready = _ready.Task;
        }

        try
        {
            await ready.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public TransportState State => Volatile.Read(ref _mirror).State;

    public Tick Position => new(Interlocked.Read(ref _position));

    public long LoopWraps => Volatile.Read(ref _mirror).LoopWraps;

    public bool IsCountingIn => State == TransportState.Playing && Clock.Now.Ticks < Interlocked.Read(ref _countInUntil);

    /// <summary>Whether the engine opens <paramref name="endpoint"/> itself.</summary>
    public bool Owns(EndpointDescriptor endpoint) => _options.IsEngineEndpoint(endpoint ?? throw new ArgumentNullException(nameof(endpoint)));

    /// <summary>Problems the engine reported since the last call (devices it could not open, say).</summary>
    public ImmutableArray<string> TakeProblems()
    {
        lock (_gate)
        {
            var problems = _problems.ToImmutableArray();
            _problems.Clear();
            return problems;
        }
    }

    public TimingSnapshot SnapshotStatistics() => Volatile.Read(ref _timing);

    public bool TryGetTickAt(TimeSpan time, out Tick tick)
    {
        var mirror = Volatile.Read(ref _mirror);
        return TransportAnchor.TryGetTickAt(mirror.Current, mirror.Previous, time, out tick);
    }

    public bool TryGetSongTimeAt(TimeSpan time, out SongPosition position) =>
        TransportAnchor.TryGetSongTimeAt(Volatile.Read(ref _mirror).Current, time, out position);

    /// <summary>Writes <paramref name="plan"/> as an image file and tells the engine to play it.</summary>
    public void Load(PlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(_planDirectory);
        var id = Interlocked.Increment(ref _nextPlan);
        var path = Path.Combine(_planDirectory, $"plan-{id}.bspl");
        File.WriteAllBytes(path, PlanImage.ToArray(plan));
        lock (_gate)
        {
            _tempos[id] = plan.TempoMap;
            _tempos.Remove(id - KeptTempos);
            _planFiles.Add((id, path));
            _plan = new LoadPlan(id, path);
            _link?.Send(_plan);
        }
    }

    public void SetOutputs(IReadOnlyList<IMidiOutput?> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        Send(_slots = new SetSlots([.. outputs.Select(o => o is IEngineEndpoint e ? e.Handle : -1)]));
    }

    public bool Play(Tick? from = null, CountIn? countIn = null)
    {
        if (from is { } start)
        {
            Interlocked.Exchange(ref _position, start.Value);
        }

        Interlocked.Exchange(ref _countInUntil, countIn is null ? 0 : (Clock.Now + countIn.Duration).Ticks);
        var spec = countIn is null ? null : new CountInSpec(countIn.Duration.Ticks, [.. countIn.Clicks.Select(c => new CountInClickSpec(c.Offset.Ticks, c.Downbeat))]);
        lock (_gate)
        {
            return _link?.Send(new TransportPlay(from?.Value ?? -1, spec, ++_transportSequence)) == true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _link?.Send(new TransportStop(++_transportSequence));
        }
    }

    public void Seek(Tick position)
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _position, position.Value);
            Interlocked.Exchange(ref _countInUntil, 0);
            _link?.Send(new TransportSeek(position.Value, ++_transportSequence));
        }
    }

    public void SetLoop(LoopRegion? loop) => Send(_loop = loop is null ? new SetLoop(false, 0, 0) : new SetLoop(true, loop.Start.Value, loop.End.Value));

    public void SetMetronome(MetronomeClick? click, bool enabled) => Send(_metronome = new SetMetronome(
        click is null ? null : new MetronomeSpec(click.Slot, (byte)click.Channel.Index, click.AccentNote.Value, click.AccentVelocity.Value, click.BeatNote.Value, click.BeatVelocity.Value, click.Length.Ticks),
        enabled));

    public void Panic() => Send(new Panic());

    public void Nudge(TimeSpan amount) => Send(new Nudge(amount.Ticks));

    public void SendNow(IMidiOutput output, ChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(output);
        Span<byte> bytes = stackalloc byte[3];
        var length = message.CopyTo(bytes);
        if (output is ForwardedOutput forwarded)
        {
            forwarded.Local.Send(bytes[..length], MidiTimestamp.Immediate);
        }
        else if (output is EngineOutput engine)
        {
            SendToOutput(engine.Handle, bytes[..length]);
        }
    }

    /// <summary>Plays the track's route for input from the engine's inputs (MIDI thru), or none.</summary>
    public void SetThru(Guid? track) => Send(_thru = new SetThru(track));

    /// <summary>An output the engine opens itself.</summary>
    public IMidiOutput OpenOutput(EndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            var output = new EngineOutput(this, ++_nextHandle, endpoint);
            _endpoints[output.Handle] = output;
            var open = new OpenOutput(output.Handle, endpoint.Id.Provider, endpoint.Id.Value, endpoint.DisplayName, Forwarded: false, Scheduled: false);
            _openOutputs[output.Handle] = open;
            _link?.Send(open);
            return output;
        }
    }

    /// <summary>An output opened here that the engine plays to through Bluestone; disposing it closes <paramref name="local"/>.</summary>
    public IMidiOutput Forward(IMidiOutput local)
    {
        ArgumentNullException.ThrowIfNull(local);
        lock (_gate)
        {
            var output = new ForwardedOutput(this, ++_nextHandle, local);
            _endpoints[output.Handle] = output;
            var endpoint = local.Endpoint;
            var open = new OpenOutput(output.Handle, endpoint.Id.Provider, endpoint.Id.Value, endpoint.DisplayName, Forwarded: true, endpoint.Capabilities.HasFlag(EndpointCapabilities.ScheduledDelivery));
            _openOutputs[output.Handle] = open;
            _link?.Send(open);
            return output;
        }
    }

    /// <summary>An input the engine opens itself; what it receives arrives on the returned input.</summary>
    public IMidiInput OpenInput(EndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            var input = new EngineInput(this, ++_nextHandle, endpoint);
            _endpoints[input.Handle] = input;
            _openInputs[input.Handle] = new InputSpec(input.Handle, endpoint.Id.Provider, endpoint.Id.Value, endpoint.DisplayName);
            _link?.Send(Inputs());
            return input;
        }
    }

    /// <summary>Deletes the plan files.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _link = null;
            NotReady();
            _planFiles.Clear();
        }

        try
        {
            if (Directory.Exists(_planDirectory))
            {
                Directory.Delete(_planDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An engine still has a plan mapped (Windows); the temporary folder is cleaned up eventually.
        }
    }

    void IEngineClient.Connected(IEngineLink link, bool restarted)
    {
        lock (_gate)
        {
            _link = link;
            Volatile.Write(ref _mirror, Mirror.Stopped(_mirror.LoopWraps));
            foreach (var open in _openOutputs.Values)
            {
                link.Send(open);
            }

            link.Send(_slots);
            if (_plan is { } plan)
            {
                link.Send(plan);
            }

            link.Send(_loop);
            link.Send(_metronome);
            foreach (var route in _routes.Values)
            {
                link.Send(route);
            }

            link.Send(_thru);
            link.Send(Inputs());

            // The transport stays stopped where it was (ADR 0044).
            link.Send(new TransportSeek(Interlocked.Read(ref _position), ++_transportSequence));
            _ready.TrySetResult();
        }

        TransportChanged?.Invoke(this, EventArgs.Empty);
    }

    void IEngineClient.Received(IEngineLink link, EngineMessage message)
    {
        switch (message)
        {
            case MidiBatch batch:
                foreach (var e in batch.Events)
                {
                    IEngineEndpoint? endpoint;
                    lock (_gate)
                    {
                        endpoint = _endpoints.GetValueOrDefault(e.Handle);
                    }

                    if (e.Kind == MidiEventKind.Output && endpoint is ForwardedOutput output)
                    {
                        output.Local.Send(e.Bytes.Span, e.Timestamp == 0 ? MidiTimestamp.Immediate : MidiTimestamp.At(TimeSpan.FromTicks(e.Timestamp)));
                    }
                    else if (e.Kind == MidiEventKind.Input && endpoint is EngineInput input)
                    {
                        input.Deliver(e.Bytes.Span, TimeSpan.FromTicks(e.Timestamp));
                    }
                }

                break;
            case TransportStatus status:
                lock (_gate)
                {
                    // From an engine that stopped, or sent before the engine applied the last transport command.
                    if (!ReferenceEquals(link, _link) || status.Applied < _transportSequence)
                    {
                        return;
                    }

                    Volatile.Write(ref _mirror, new Mirror(
                        status.Playing ? TransportState.Playing : TransportState.Stopped,
                        status.LoopWraps,
                        Anchor(status.Current),
                        Anchor(status.Previous)));
                    Interlocked.Exchange(ref _position, status.Position);
                    Interlocked.Exchange(ref _countInUntil, status.CountInUntil);
                }

                TransportChanged?.Invoke(this, EventArgs.Empty);
                break;
            case TimingStatus timing:
                var fine = new long[TimingStatistics.FineBucketCount];
                foreach (var bucket in timing.Fine)
                {
                    if (bucket.Index < fine.Length)
                    {
                        fine[bucket.Index] = bucket.Count;
                    }
                }

                Volatile.Write(ref _timing, new TimingSnapshot(timing.Dispatched, timing.Late, timing.SkippedLateNotes, timing.Dropped, timing.NoteOverflow, TimeSpan.FromTicks(timing.MaxLateness), timing.Histogram) { FineHistogram = [.. fine] });
                break;
            case PlanLoaded loaded:
                List<string> stale;
                lock (_gate)
                {
                    stale = [.. _planFiles.Where(p => p.Id < loaded.PlanId).Select(p => p.Path)];
                    _planFiles.RemoveAll(p => p.Id < loaded.PlanId);
                }

                foreach (var path in stale)
                {
                    TryDelete(path);
                }

                break;
            case EngineProblem { Area: EngineProblemArea.Midi } problem:
                IEngineEndpoint? failed;
                lock (_gate)
                {
                    _problems.Add(problem.Message);
                    failed = _endpoints.GetValueOrDefault(problem.Handle);
                }

                switch (failed)
                {
                    case EngineOutput output:
                        output.Fail(problem.Message);
                        break;
                    case EngineInput input:
                        input.Fail(problem.Message);
                        break;
                }

                break;
        }
    }

    void IEngineClient.Disconnected(IEngineLink link)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(link, _link))
            {
                return;
            }

            _link = null;
            NotReady();
            Volatile.Write(ref _mirror, Mirror.Stopped(_mirror.LoopWraps));
        }

        TransportChanged?.Invoke(this, EventArgs.Empty);
        EngineLost?.Invoke(this, EventArgs.Empty);
    }

    // Under _gate: later WhenReadyAsync calls wait for the next engine.
    private void NotReady()
    {
        if (_ready.Task.IsCompleted)
        {
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // The engine runs routes through built-in live devices, forced channels, and plugin MIDI effects, to an output it
    // reaches or into a plugin instrument of its audio graph (ADR 0044 steps 5 and 7).
    bool ILiveRemote.CanRun(LiveRoutePlan plan, IMidiOutput? output) =>
        plan.Target switch
        {
            LiveTargetKind.External => plan.Endpoint is not null && output is IEngineEndpoint,
            LiveTargetKind.Software => plan.Instrument is not null,
            _ => false,
        }
        && plan.Stages.All(s => s.Channel is not null || (s.Device is { } device
            && (DeviceCatalog.BuiltIn.HasLiveProcessor(device.Definition.Id) || device.Definition.Id.Value.StartsWith(PluginDeviceHost.DefinitionPrefix, StringComparison.Ordinal))));

    int ILiveRemote.Add(LiveRoutePlan plan, IMidiOutput? output, Ppqn ppqn, Func<DeviceId, PluginInstance?> plugins)
    {
        var remote = new RemoteRoute(this, plan, output, ppqn, plugins);
        lock (_gate)
        {
            remote.Id = ++_nextHandle;
            _remoteRoutes[remote.Id] = remote;
            var route = remote.Spec();
            _routes[route.Route] = route;
            _link?.Send(route);
        }

        remote.Watch();
        return remote.Id;
    }

    void ILiveRemote.Remove(int route)
    {
        RemoteRoute? remote;
        lock (_gate)
        {
            _remoteRoutes.Remove(route, out remote);
            if (_routes.Remove(route))
            {
                _link?.Send(new RemoveRoute(route));
            }
        }

        remote?.Unwatch();
    }

    void ILiveRemote.Post(int route, ChannelMessage message, TimeSpan timestamp) =>
        Send(new RouteInput(route, Math.Max(0, timestamp.Ticks), message.Status, message.Data1, message.Data2));

    bool ILiveRemote.IsEngineInput(EndpointDescriptor input) => _options.IsEngineEndpoint(input);

    internal SendResult SendToOutput(int handle, ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty || message.Length > EngineLimits.MaxMidiMessageBytes)
        {
            return SendResult.Rejected;
        }

        lock (_gate)
        {
            return _link?.Send(new OutputSend(handle, message.ToArray())) == true ? SendResult.Sent : SendResult.Disconnected;
        }
    }

    internal void Close(IEngineEndpoint endpoint)
    {
        lock (_gate)
        {
            _endpoints.Remove(endpoint.Handle);
            if (_openOutputs.Remove(endpoint.Handle))
            {
                _link?.Send(new CloseOutput(endpoint.Handle));
            }

            if (_openInputs.Remove(endpoint.Handle))
            {
                _link?.Send(Inputs());
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still mapped somewhere; the folder goes when Bluestone closes.
        }
    }

    // Under _gate.
    private SetInputs Inputs() => new([.. _openInputs.Values]);

    private void Send(EngineMessage message)
    {
        lock (_gate)
        {
            _link?.Send(message);
        }
    }

    // Under _gate.
    private TransportAnchor Anchor(in AnchorSpec spec) =>
        new(spec.Playing, TimeSpan.FromTicks(spec.Time), TimeSpan.FromTicks(spec.SongTime), _tempos.GetValueOrDefault(spec.PlanId), spec.LoopStart, spec.LoopEnd);

    // A plugin effect on a route the engine runs restarted (a new exchange) or stopped: the route goes again, with it.
    private void Resend(RemoteRoute remote)
    {
        lock (_gate)
        {
            if (_remoteRoutes.TryGetValue(remote.Id, out var current) && ReferenceEquals(current, remote))
            {
                var route = remote.Spec();
                _routes[route.Route] = route;
                _link?.Send(route);
            }
        }
    }

    /// <summary>A live route the engine runs, as Bluestone planned it, with the plugin effects it watches.</summary>
    private sealed class RemoteRoute(EnginePlayback owner, LiveRoutePlan plan, IMidiOutput? output, Ppqn ppqn, Func<DeviceId, PluginInstance?> plugins)
    {
        private readonly List<PluginInstance> _watched = [];

        public int Id { get; set; }

        // The route as the engine runs it. A plugin effect with no instance runs nowhere and is left out (it passes events through).
        public SetRoute Spec()
        {
            var stages = new List<RouteStageSpec>();
            foreach (var stage in plan.Stages)
            {
                if (stage.Channel is { } channel)
                {
                    stages.Add(new RouteStageSpec(string.Empty, channel.Index, []));
                }
                else if (stage.Device is { } device && DeviceCatalog.BuiltIn.HasLiveProcessor(device.Definition.Id))
                {
                    stages.Add(new RouteStageSpec(device.Definition.Id.Value, -1, [.. (DeviceCatalog.BuiltIn.Find(device.Definition.Id)?.Parameters ?? [])
                        .Select(p => new RouteParameter(p.Id.Value, (device.ValueOf(p.Id) ?? p.DefaultValue).Value))]));
                }
                else if (stage.Device is { } effect && plugins(effect.Id) is { } instance)
                {
                    stages.Add(new RouteStageSpec(string.Empty, -1, [], Effect(effect, instance)));
                }
            }

            return plan.Target == LiveTargetKind.Software
                ? new SetRoute(Id, plan.Track.Value, -1, ppqn.TicksPerQuarterNote, [.. stages], plan.Instrument!.Value.Value)
                : new SetRoute(Id, plan.Track.Value, ((IEngineEndpoint)output!).Handle, ppqn.TicksPerQuarterNote, [.. stages]);
        }

        public void Watch()
        {
            foreach (var stage in plan.Stages)
            {
                if (stage.Device is { } device && plugins(device.Id) is { } instance && !_watched.Contains(instance))
                {
                    instance.StatusChanged += OnStatus;
                    _watched.Add(instance);
                }
            }
        }

        public void Unwatch()
        {
            foreach (var instance in _watched)
            {
                instance.StatusChanged -= OnStatus;
            }

            _watched.Clear();
        }

        // Plugin MIDI effects play only live input, so the engine runs them at depth 1: their output comes in the same block.
        private static PluginStageSpec Effect(DeviceInstance device, PluginInstance instance)
        {
            var shape = instance.ExchangeShape;
            var plane = instance.DataPlane;
            return new PluginStageSpec(
                device.Id.Value,
                device.Definition.DisplayName ?? device.Definition.Id.Value,
                (int)instance.Request.SampleRate,
                shape.MaxFrames,
                shape.InputChannels,
                shape.OutputChannels,
                shape.SlotCount,
                shape.EventCapacity,
                shape.ParameterChangeCapacity,
                1,
                instance.CollectWait.Ticks,
                instance.PluginLatencyFrames,
                plane?.Path,
                plane?.Generation ?? 0);
        }

        private void OnStatus(object? sender, PluginStatusChangedEventArgs e) => owner.Resend(this);
    }

    /// <summary>The engine's transport as last reported.</summary>
    private sealed record Mirror(TransportState State, long LoopWraps, TransportAnchor Current, TransportAnchor Previous)
    {
        public static Mirror Stopped(long loopWraps) => new(TransportState.Stopped, loopWraps, default, default);
    }
}
