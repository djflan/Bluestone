using System.Collections.Immutable;
using Bluestone.Application.Editing;
using Bluestone.Application.Live;
using Bluestone.Application.Recording;
using Bluestone.Application.Routing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Application.Sessions;

/// <summary>When the metronome clicks. Count-ins always click when a metronome output is available.</summary>
public enum MetronomeMode
{
    Off,
    WhileRecording,
    Always,
}

/// <summary>Metronome preferences.</summary>
/// <param name="Mode">When beats click during playback.</param>
/// <param name="Output">The endpoint to click on; <see langword="null"/> uses the selected track's output.</param>
public sealed record MetronomeSettings(MetronomeMode Mode = MetronomeMode.WhileRecording, EndpointId? Output = null)
{
    /// <summary>The click's notes, velocities, and channel (the slot is assigned by the controller).</summary>
    public MetronomeClick Sound { get; init; } = new(0);
}

/// <summary>How a take is recorded.</summary>
/// <param name="CountInBars">Bars to count in before recording from a stop; 0 for none.</param>
/// <param name="Replace">Replace what is already on the track in the recorded range, rather than merging (overdub).</param>
public sealed record RecordOptions(int CountInBars = 1, bool Replace = false);

/// <summary>
/// Connects a <see cref="ProjectSession"/> to MIDI outputs and the playback engine. It keeps routes
/// resolved as endpoints come and go, keeps outputs open for the endpoints in use, and recompiles the
/// plan after every edit, including while playing.
/// </summary>
/// <remarks>Methods are intended to be called from one (UI) thread; endpoint notifications from other threads are marshalled through <see cref="RefreshAsync"/>.</remarks>
public sealed class PlaybackController : IAsyncDisposable
{
    private readonly ProjectSession _session;
    private readonly EndpointDirectory _endpoints;
    private readonly PlaybackThread? _thread;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<EndpointId, IMidiOutput> _open = [];

    // _open as of the last refresh, for thru and audition, which run outside the refresh gate.
    private ImmutableDictionary<EndpointId, IMidiOutput> _published = ImmutableDictionary<EndpointId, IMidiOutput>.Empty;
    private ImmutableArray<EndpointId> _slots = [];
    private MetronomeSettings _metronome = new();
    private TrackId? _thruTrack;
    private IReadOnlyCollection<EndpointId>? _inputSelection;
    private RecordOptions _recordOptions = new();
    private HeldKey? _audition;
    private readonly IMonotonicClock _clock;
    private long _takeWraps;

    // Orders output changes and metronome slot assignments into the engine, which run on different threads.
    private readonly Lock _engineGate = new();

    /// <param name="session">The project being played.</param>
    /// <param name="endpoints">Every available MIDI provider.</param>
    /// <param name="profiles">Installed device profiles.</param>
    /// <param name="clock">The monotonic clock shared with the endpoints.</param>
    /// <param name="options">Scheduler options.</param>
    /// <param name="startThread">False to drive <see cref="PlaybackEngine.Pump"/> manually (tests).</param>
    /// <param name="playbackThreadSetup">Platform setup run on the playback thread, e.g. real-time scheduling (see ADR 0012).</param>
    public PlaybackController(ProjectSession session, EndpointDirectory endpoints, ProfileCatalog profiles, IMonotonicClock clock, PlaybackOptions? options = null, bool startThread = true, Action? playbackThreadSetup = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Engine = new PlaybackEngine(clock, session.Project.Sequence.TempoMap, options);
        Live = new LiveInput(Engine, clock);
        Recorder = new MidiRecorder(Engine, Live);
        _thread = startThread ? new PlaybackThread(Engine, playbackThreadSetup) : null;
        if (startThread)
        {
            Live.Start();
        }
    }

    public PlaybackEngine Engine { get; }

    /// <summary>Live input through the devices on each track's live route (ADR 0035). Without a live thread (tests), call <see cref="LiveInput.Pump"/>.</summary>
    public LiveInput Live { get; }

    /// <summary>MIDI input, thru, and take capture.</summary>
    public MidiRecorder Recorder { get; }

    public MetronomeSettings Metronome => _metronome;

    /// <summary>Problems opening MIDI inputs during the last refresh.</summary>
    public ImmutableArray<string> InputProblems { get; private set; } = [];

    public ProfileCatalog Profiles { get; private set; }

    /// <summary>The devices chains can run: built-ins, and plugin definitions as data (plugins run in workers).</summary>
    public DeviceCatalog Devices { get; private set; } = DeviceCatalog.BuiltIn;

    /// <summary>Every track's output as last resolved, in track order.</summary>
    public ImmutableArray<ResolvedTrackOutput> Tracks => Routing?.Tracks ?? [];

    /// <summary>Every external instrument as last resolved.</summary>
    public ImmutableArray<ResolvedInstrument> Instruments => Routing?.Instruments ?? [];

    /// <summary>The routing as last prepared, including the evaluated signal graph.</summary>
    public PreparedRouting? Routing { get; private set; }

    /// <summary>Problems opening outputs during the last refresh.</summary>
    public ImmutableArray<string> OutputProblems { get; private set; } = [];

    public ImmutableArray<PlanDiagnostic> PlanDiagnostics { get; private set; } = [];

    /// <summary>Raised after <see cref="Routing"/> or the prepared plan changes.</summary>
    public event EventHandler? Refreshed;

    public void UseProfiles(ProfileCatalog profiles) => Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

    public void UseDevices(DeviceCatalog devices) => Devices = devices ?? throw new ArgumentNullException(nameof(devices));

    /// <summary>
    /// Re-resolves routes against the current endpoints, opens and closes outputs as needed, and loads
    /// a freshly compiled plan into the engine. Call after edits and when endpoints change.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = _session.Project;
            var devices = Devices;
            var profiles = Profiles;
            var endpoints = _endpoints.GetEndpoints();

            // Out-of-process devices are asked over IPC while the graph is evaluated; keep that off the caller's thread.
            var prepared = !NeedsRendering(project, devices)
                ? PlaybackRouting.Prepare(project, devices, profiles, endpoints)
                : await Task.Run(() => PlaybackRouting.Prepare(project, devices, profiles, endpoints), cancellationToken).ConfigureAwait(false);
            Routing = prepared;
            var problems = ImmutableArray.CreateBuilder<string>();

            // The metronome may click on an output no track uses; it gets a slot of its own.
            var slots = prepared.Slots;
            var metronomeEndpoint = MetronomeEndpoint();
            if (metronomeEndpoint is { } clickOn && !slots.Contains(clickOn))
            {
                slots = slots.Add(clickOn);
            }

            var stale = _slots.Any(id => !_open.TryGetValue(id, out var output) || output.State != EndpointState.Open);
            if (stale || !slots.SequenceEqual(_slots))
            {
                var outputs = new IMidiOutput?[slots.Length];
                for (var i = 0; i < outputs.Length; i++)
                {
                    outputs[i] = await GetOrOpenAsync(slots[i], problems, cancellationToken).ConfigureAwait(false);
                }

                lock (_engineGate)
                {
                    Engine.SetOutputs(outputs);
                    _slots = slots;
                }

                CloseUnused(slots);
            }

            Volatile.Write(ref _published, _open.ToImmutableDictionary());
            var instruments = prepared.Instruments;
            Live.Update(project, devices, (id, port) => instruments.FirstOrDefault(i => i.Instrument.Id == id)?.FindPort(port) is { Endpoint.IsUsable: true } usable ? usable.Endpoint.Endpoint : null, Volatile.Read(ref _published));
            ApplyMetronome();
            ApplyThru();
            InputProblems = await Recorder.SetInputsAsync(_endpoints, WantedInputs(), cancellationToken).ConfigureAwait(false);

            var plan = prepared.Compile(project.Sequence);
            Engine.Load(plan);
            Engine.SetLoop(project.Loop is { } loop ? new LoopRegion(loop.Start, loop.End) : null);
            PlanDiagnostics = [.. plan.Diagnostics];
            OutputProblems = problems.ToImmutable();
        }
        finally
        {
            _gate.Release();
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public async Task PlayAsync(Tick? from = null, CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        Engine.Play(from);
    }

    // Whether compiling the plan will ask a device outside Bluestone's process (a plugin MIDI effect in its worker).
    private static bool NeedsRendering(Project project, DeviceCatalog devices) =>
        devices.Renderer is { } renderer
        && project.Chains.Any(chain => chain.Devices.Any(device =>
            !device.IsBypassed && devices.Find(device.Definition.Id) is { } definition && renderer.CanRender(device, definition)));

    /// <summary>Stops playback. A take being recorded is finished and added to its track first.</summary>
    public RecordedTake? Stop()
    {
        var take = FinishRecording();
        Engine.Stop();
        return take;
    }

    /// <summary>The input endpoints recording can use.</summary>
    public IReadOnlyList<EndpointDescriptor> InputEndpoints => _endpoints.GetEndpoints(EndpointDirection.Input);

    /// <summary>
    /// Chooses which inputs to listen to; <see langword="null"/> listens to every input except
    /// Bluestone's built-in test and monitor buses (which would echo Bluestone's own output back).
    /// Takes effect on the next <see cref="RefreshAsync"/>.
    /// </summary>
    public void SelectInputs(IReadOnlyCollection<EndpointId>? inputs) => _inputSelection = inputs;

    /// <summary>Echoes input to <paramref name="track"/>'s output (MIDI thru), or stops echoing when null.</summary>
    public void SetThruTrack(TrackId? track)
    {
        _thruTrack = track;
        ApplyThru();
    }

    /// <summary>Changes the metronome. Takes effect on the next <see cref="RefreshAsync"/> when its output changes.</summary>
    public void SetMetronome(MetronomeSettings settings)
    {
        _metronome = settings ?? throw new ArgumentNullException(nameof(settings));
        ApplyMetronome();
    }

    /// <summary>
    /// Starts recording into <paramref name="track"/>. While playing, recording punches in at the
    /// playhead; from a stop, playback starts at the playhead after the count-in.
    /// </summary>
    public async Task RecordAsync(TrackId track, RecordOptions? options = null, CancellationToken cancellationToken = default)
    {
        _recordOptions = options ?? new RecordOptions();
        _takeWraps = Engine.LoopWraps;
        if (Engine.State == TransportState.Playing)
        {
            Recorder.Start(track, Engine.Position);
            ApplyMetronome();
            ApplyThru();
            return;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var from = Engine.Position;
        var sequence = _session.Project.Sequence;
        var countIn = _recordOptions.CountInBars > 0 ? CountIn.Bars(sequence.TempoMap, sequence.MeterMap, from, _recordOptions.CountInBars) : null;
        Recorder.Start(track, from);
        ApplyMetronome();
        ApplyThru();
        Engine.Play(from, countIn);
    }

    /// <summary>
    /// Ends the take (punch out) and adds it to its track as one undoable step, leaving the transport
    /// running. Returns null when nothing was being recorded.
    /// </summary>
    public RecordedTake? FinishRecording()
    {
        if (!Recorder.IsRecording)
        {
            return null;
        }

        var project = _session.Project;
        var end = Engine.Position;
        // The loop is the take's range only if playback actually wrapped while recording.
        var loop = project.Loop is { } l && Engine.LoopWraps != _takeWraps ? l : null;
        var take = Recorder.Stop(end, loop);
        ApplyMetronome();
        ApplyThru();
        if (project.Sequence.FindTrack(take.Track) is not null && (take.Events.Length > 0 || _recordOptions.Replace))
        {
            try
            {
                _session.Execute(ProjectCommands.Record(take.Track, take.Events, _recordOptions.Replace ? take.Range : null));
            }
            catch (CommandRefusedException ex)
            {
                take = take with { NotAdded = ex.Message };
            }
        }

        return take;
    }

    /// <summary>The channel <paramref name="track"/>'s live notes play on: the one its route forces, else its first event's, else channel 1.</summary>
    public MidiChannel ChannelFor(TrackId track) =>
        LiveRouteOf(track)?.Channel
        ?? _session.Project.Sequence.FindTrack(track)?.FirstChannel
        ?? MidiChannel.FromIndex(0);

    /// <summary>Where live and auditioned notes for <paramref name="track"/> go, as last resolved.</summary>
    public LiveRoute? LiveRouteOf(TrackId track) => Routing?.FindTrack(track)?.Live;

    /// <summary>Sounds a note on <paramref name="track"/>'s live route until <see cref="EndAudition"/>, e.g. while clicking or dragging a note.</summary>
    public void Audition(TrackId track, NoteNumber note, Velocity velocity)
    {
        EndAudition();
        if (SoundOn(track, note, velocity) is { } sounded)
        {
            _audition = new HeldKey(sounded, null);
        }
    }

    /// <summary>
    /// Plays a key of the on-screen keyboard until <see cref="EndAudition"/>. When <paramref name="track"/> is
    /// being recorded, the key is input like a note from a MIDI keyboard: it sounds through MIDI thru (or
    /// directly, with thru off) and is recorded. Otherwise it is auditioned.
    /// </summary>
    public void PlayKey(TrackId track, NoteNumber note, Velocity velocity)
    {
        if (Recorder.RecordingTrack != track)
        {
            Audition(track, note, velocity);
            return;
        }

        EndAudition();
        var channel = ChannelFor(track);
        var recordedOff = ChannelMessage.NoteOff(channel, note, Velocity.DefaultRelease);
        var echoed = Recorder.PlayOnScreen(ChannelMessage.NoteOn(channel, note, velocity), _clock.Now);
        _audition = new HeldKey(echoed ? null : SoundOn(track, note, velocity), recordedOff);
    }

    public void EndAudition()
    {
        if (_audition is not { } held)
        {
            return;
        }

        _audition = null;
        if (held.Recorded is { } recorded)
        {
            // The release is input too, so the recorded note gets its length and thru releases it.
            Recorder.PlayOnScreen(recorded, _clock.Now);
        }

        if (held.Sounded is { } sounded)
        {
            Live.Post(sounded.Track, sounded.Off, _clock.Now);
        }
    }

    /// <summary>Plays a note-on on <paramref name="track"/>'s live route, through its devices; returns the matching note-off.</summary>
    private (TrackId Track, ChannelMessage Off)? SoundOn(TrackId track, NoteNumber note, Velocity velocity)
    {
        var channel = ChannelFor(track);
        return Live.Post(track, ChannelMessage.NoteOn(channel, note, velocity), _clock.Now)
            ? (track, ChannelMessage.NoteOff(channel, note, Velocity.DefaultRelease))
            : null;
    }

    public void Seek(Tick position) => Engine.Seek(position);

    /// <summary>Ends every live note, then sends every output's all-notes-off.</summary>
    public void Panic()
    {
        Live.ReleaseAll();
        Engine.Panic();
    }

    /// <summary>
    /// Sends the profile initialization messages (for example a System On) of the instrument <paramref name="track"/>'s live route reaches, after
    /// asking <paramref name="confirm"/> for each template that resets or overwrites instrument state.
    /// Nothing is sent unless the user asks; opening a project never does this.
    /// </summary>
    /// <returns>The number of messages sent.</returns>
    public async Task<int> InitializeInstrumentAsync(TrackId track, Func<SysExTemplate, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (LiveRouteOf(track) is not { Instrument.Profile.Profile: { } profile } route)
        {
            return 0;
        }

        var problems = ImmutableArray.CreateBuilder<string>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var output = await GetOrOpenAsync(route.Endpoint.Id, problems, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                return 0;
            }

            var sent = 0;
            foreach (var step in profile.Initialization)
            {
                var template = profile.FindTemplate(step.TemplateId)!;
                if (template.RequiresConfirmation && !await confirm(template).ConfigureAwait(false))
                {
                    continue;
                }

                if (output.Send(template.Render().Bytes.Span, MidiTimestamp.Immediate) == SendResult.Sent)
                {
                    sent++;
                }

                await Task.Delay(step.DelayAfter, cancellationToken).ConfigureAwait(false);
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops playback (releasing sounding notes), stops the playback thread, then closes every output.</summary>
    public async ValueTask DisposeAsync()
    {
        EndAudition();
        Recorder.Dispose();
        Live.Dispose();
        Engine.Stop();
        if (_thread is not null)
        {
            // The thread applies the Stop on its way out; pumping here would race it.
            _thread.Dispose();
        }
        else
        {
            Engine.Pump();
        }
        foreach (var output in _open.Values)
        {
            output.Dispose();
        }

        _open.Clear();
        Engine.Dispose();
        _gate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// The chosen inputs, or by default every input except Bluestone's built-in buses and inputs that
    /// share a name with an output Bluestone is playing to (an IAC bus used both ways would record
    /// Bluestone's own playback).
    /// </summary>
    private IReadOnlyCollection<EndpointId> WantedInputs()
    {
        if (_inputSelection is { } chosen)
        {
            return chosen;
        }

        var outputs = _endpoints.GetEndpoints(EndpointDirection.Output);
        return DefaultInputs(InputEndpoints, [.. _slots.Select(id => outputs.FirstOrDefault(o => o.Id == id)).OfType<EndpointDescriptor>()]);
    }

    /// <summary>
    /// Every input except Bluestone's built-in buses and inputs that share a provider and name with an
    /// output being played to (an IAC bus used both ways would record Bluestone's own playback).
    /// </summary>
    internal static IReadOnlyCollection<EndpointId> DefaultInputs(IEnumerable<EndpointDescriptor> inputs, IReadOnlyCollection<EndpointDescriptor> playing) =>
        [.. inputs
            .Where(e => e.Transport != EndpointTransport.Test)
            .Where(e => !playing.Any(o => o.Id.Provider == e.Id.Provider && string.Equals(o.DisplayName, e.DisplayName, StringComparison.Ordinal)))
            .Select(e => e.Id)];

    /// <summary>The metronome's endpoint: the one chosen, or else the recording or thru track's live output.</summary>
    private EndpointId? MetronomeEndpoint()
    {
        if (_metronome.Output is { } chosen)
        {
            return chosen;
        }

        var track = Recorder.RecordingTrack ?? _thruTrack;
        return track is { } id && LiveRouteOf(id) is { } route ? route.Endpoint.Id : null;
    }

    private void ApplyMetronome()
    {
        var enabled = _metronome.Mode == MetronomeMode.Always || (_metronome.Mode == MetronomeMode.WhileRecording && Recorder.IsRecording);
        lock (_engineGate)
        {
            var slot = MetronomeEndpoint() is { } endpoint ? _slots.IndexOf(endpoint) : -1;
            Engine.SetMetronome(slot >= 0 ? _metronome.Sound with { Slot = slot } : null, enabled);
        }
    }

    private void ApplyThru()
    {
        Recorder.SetThru(_thruTrack);
        Live.Monitored = Recorder.RecordingTrack ?? _thruTrack;
    }

    private async Task<IMidiOutput?> GetOrOpenAsync(EndpointId id, ImmutableArray<string>.Builder problems, CancellationToken cancellationToken)
    {
        if (_open.TryGetValue(id, out var existing) && existing.State == EndpointState.Open)
        {
            return existing;
        }

        existing?.Dispose();
        _open.Remove(id);
        try
        {
            var output = await _endpoints.OpenOutputAsync(id, cancellationToken).ConfigureAwait(false);
            _open[id] = output;
            return output;
        }
        catch (EndpointUnavailableException ex)
        {
            problems.Add(ex.Message);
            return null;
        }
    }

    private void CloseUnused(ImmutableArray<EndpointId> inUse)
    {
        foreach (var id in _open.Keys.Except(inUse).ToList())
        {
            _open[id].Dispose();
            _open.Remove(id);
        }
    }

    /// <summary>A held audition or key: the release to play on a track's live route, and the release to play into the take.</summary>
    private sealed record HeldKey((TrackId Track, ChannelMessage Off)? Sounded, ChannelMessage? Recorded);
}
