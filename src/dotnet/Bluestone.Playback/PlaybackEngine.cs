using System.Collections.Concurrent;
using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;

namespace Bluestone.Playback;

/// <summary>
/// The real-time scheduler and transport. Commands (play, stop, seek, loop, panic, plan and output
/// changes) may be issued from any thread; they are queued and applied by <see cref="Pump"/>, which a
/// single playback thread calls repeatedly (see <see cref="PlaybackThread"/>). Tests drive
/// <see cref="Pump"/> directly with a <see cref="VirtualClock"/>.
/// </summary>
/// <remarks>
/// <para>
/// Messages for endpoints with <see cref="EndpointCapabilities.ScheduledDelivery"/> are handed over up
/// to <see cref="PlaybackOptions.LookAhead"/> early with explicit timestamps; messages for other
/// endpoints are sent when due. Each class has its own cursor through the plan.
/// </para>
/// <para>
/// Note releases are not part of the plan: when a note starts, its release is entered in a fixed-size
/// table. Replacing the plan mid-note, stopping, seeking, or looping therefore always releases every
/// note that sounded, at the right time, even if the note was deleted by an edit.
/// </para>
/// <para>
/// <see cref="Pump"/> does not allocate, lock, log, or throw for endpoint failures. Applying a command
/// may allocate; commands are rare.
/// </para>
/// </remarks>
public sealed class PlaybackEngine : IPlaybackTransport, IAudioMetronome, IDisposable
{
    private const int ChannelsPerSlot = 16;
    private const int MaxLoopWrapsPerPump = 10_000;
    private static readonly TimeSpan Never = TimeSpan.MaxValue;

    private readonly IMonotonicClock _clock;
    private readonly PlaybackOptions _options;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly ConcurrentQueue<ImmediateMessage> _immediateSends = new();
    private readonly AutoResetEvent _workSignal = new(false);
    private readonly ActiveNote[] _active;
    private int _activeCount;
    private int _pumping;

    // Engine-thread state.
    private PlaybackPlan _plan;
    private IMidiOutput?[] _outputs = [];
    private bool[] _slotScheduled = [];
    private bool[] _sustain = [];
    private LoopRegion? _loop;

    // What to send at each loop wrap so held controllers match the loop start (ChaseState.AtWrap). Load
    // and SetLoop compute it under this lock from the latest plan and loop together, and enqueue in the
    // same order, so the playback thread always ends with the chase for its own plan and loop. The memo
    // saves recomputing it when only one of the two calls changes anything.
    private readonly Lock _wrapLock = new();
    private LoopRegion? _latestLoop;
    private (PlaybackPlan Plan, LoopRegion? Loop, ChaseMessage[] Chase)? _wrapMemo;
    private ChaseMessage[] _wrapChase = [];
    private bool _playing;
    private Cursor _immediate;
    private Cursor _scheduled;
    private MetronomeClick? _click;
    private bool _clickEnabled;
    private ImmutableArray<CountInClick> _countIn = [];
    private TimeSpan _countInStart;
    private int _countInIndex;
    private bool _audioClick;
    private long _clickGeneration;
    private TimeSpan _audioBeatsEnabledAt;
    private long _audioNudgeTicks;
    private int _audioMetronomeVersion;
    private AudioMetronomeSnapshot _audioMetronome;

    // Audio-thread state. The snapshot is pinned for the whole block, including every click lookup.
    private AudioMetronomeSnapshot _audioMetronomeReaderCache;
    private AudioMetronomeSnapshot _audioBlockSnapshot;
    private bool _audioBlockPinned;
    private TimeSpan _audioBlockTime;
    private TimeSpan _audioBlockEnd = TimeSpan.MinValue;
    private TimeSpan _audioBeatBoundary = TimeSpan.MinValue;
    private TimeSpan _audioCountInBoundary = TimeSpan.MinValue;

    // The immediate cursor's tick-to-time mapping, published for input timestamping (seqlock).
    private int _anchorVersion;
    private TransportAnchor _anchor;
    private TransportAnchor _previousAnchor;
    private long _loadsApplied;
    private long _commandsQueued;
    private long _commandsApplied;
    private long _countInUntil;
    private long _loopWraps;
    private ChaseMessage[] _stoppedChase = [];

    // Caller-visible state.
    private PlaybackPlan _latestPlan;
    private long _positionTick;
    private int _state;

    public PlaybackEngine(IMonotonicClock clock, TempoMap initialTempo, PlaybackOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? PlaybackOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxActiveNotes, nameof(options));
        _active = new ActiveNote[_options.MaxActiveNotes];
        _plan = _latestPlan = PlaybackPlan.Empty(initialTempo);
        _audioMetronome = new AudioMetronomeSnapshot(_plan, default, false, false, TimeSpan.Zero, [], TimeSpan.Zero, _clickGeneration, 0);
        _audioMetronomeReaderCache = _audioMetronome;
        Statistics = new TimingStatistics(_options.LateThreshold);
    }

    public TimingStatistics Statistics { get; }

    /// <summary>Signalled when a command is queued, so an idle playback thread can wake.</summary>
    public WaitHandle WorkSignal => _workSignal;

    public TransportState State => (TransportState)Volatile.Read(ref _state);

    /// <summary>The playhead as of the last <see cref="Pump"/>, or the last requested position.</summary>
    public Tick Position => new(Volatile.Read(ref _positionTick));

    public IMonotonicClock Clock => _clock;

    /// <summary>Replaces the plan. During playback, nothing already dispatched is repeated and every sounding note still releases.</summary>
    public void Load(PlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_wrapLock)
        {
            Volatile.Write(ref _latestPlan, plan);
            Enqueue(new LoadCommand(plan, WrapChase(plan, _latestLoop), StoppedChase(plan, Volatile.Read(ref _positionTick))));
        }
    }

    /// <summary>Sets the output for each slot used by plan bindings. Sounding notes are released first.</summary>
    public void SetOutputs(IReadOnlyList<IMidiOutput?> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        Enqueue(new OutputsCommand(
            [.. outputs],
            [.. outputs.Select(o => o?.Endpoint.Capabilities.HasFlag(EndpointCapabilities.ScheduledDelivery) == true)],
            new bool[outputs.Count * ChannelsPerSlot],
            StoppedChase(Volatile.Read(ref _latestPlan), Volatile.Read(ref _positionTick))));
    }

    /// <summary>
    /// Starts (or restarts) playback from <paramref name="from"/>, or from <see cref="Position"/>, chasing
    /// controller state. With <paramref name="countIn"/>, the transport first waits out the count-in,
    /// sounding its clicks on the metronome (if one is set), then plays.
    /// </summary>
    public bool Play(Tick? from = null, CountIn? countIn = null)
    {
        var tick = from ?? Position;
        Volatile.Write(ref _positionTick, tick.Value);
        Volatile.Write(ref _countInUntil, countIn is null ? 0 : (_clock.Now + countIn.Duration).Ticks);

        Enqueue(new PlayCommand(tick.Value, ChaseState.Compute(Volatile.Read(ref _latestPlan), tick.Value), countIn));
        return true;
    }

    /// <summary>How many times playback has wrapped from the loop's end to its start, ever. Compare two readings to tell whether a pass looped.</summary>
    public long LoopWraps => Interlocked.Read(ref _loopWraps);

    /// <summary>True while a count-in requested by <see cref="Play"/> is still running.</summary>
    public bool IsCountingIn => State == TransportState.Playing && _clock.Now.Ticks < Volatile.Read(ref _countInUntil);

    /// <summary>Always ready: commands take effect at once.</summary>
    public Task<bool> WhenReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>
    /// Configures the optional MIDI click and audio metronome. Beat clicks follow
    /// <paramref name="enabled"/>; count-in clicks use each configured output.
    /// </summary>
    public void SetMetronome(MetronomeClick? click, bool enabled, bool audioClick = false) => Enqueue(new MetronomeCommand(click, enabled, audioClick));

    public bool TryGetAudioClick(TimeSpan from, TimeSpan until, out AudioMetronomeClick click)
        => TryGetAudioClick(from, until, clickGeneration: 0, out click, out _);

    public bool TryGetAudioClick(TimeSpan from, TimeSpan until, long clickGeneration, out AudioMetronomeClick click, out bool ready)
    {
        click = default;
        if (until <= from)
        {
            if (clickGeneration != 0 && _audioBlockPinned && _audioBlockSnapshot.ClickGeneration == clickGeneration)
            {
                _audioBlockEnd = Max(_audioBlockEnd, until);
            }

            ready = true;
            return false;
        }

        var audioBlock = clickGeneration != 0 && _audioBlockPinned;
        var snapshot = audioBlock ? _audioBlockSnapshot : ReadAudioMetronome();
        ready = clickGeneration == 0 || snapshot.ClickGeneration == clickGeneration;
        if (!ready)
        {
            return false;
        }

        if (audioBlock)
        {
            _audioBlockEnd = Max(_audioBlockEnd, until);
        }

        if (!snapshot.Configured)
        {
            return false;
        }

        var countInFrom = audioBlock ? Max(from, _audioCountInBoundary) : from;
        var beatFrom = audioBlock ? Max(from, _audioBeatBoundary) : from;
        return FindAudioClick(snapshot, countInFrom, beatFrom, until, out click);
    }

    private static bool FindAudioClick(
        in AudioMetronomeSnapshot snapshot,
        TimeSpan countInFrom,
        TimeSpan beatFrom,
        TimeSpan until,
        out AudioMetronomeClick click)
    {
        click = default;
        var found = false;
        foreach (var countIn in snapshot.CountIn)
        {
            var timestamp = snapshot.CountInStart + countIn.Offset;
            if (timestamp >= countInFrom && timestamp < until && (!found || timestamp < click.Timestamp))
            {
                click = new AudioMetronomeClick(timestamp, countIn.Downbeat);
                found = true;
            }
        }

        if (!snapshot.BeatsEnabled)
        {
            return found;
        }

        var searchFrom = Max(beatFrom, snapshot.Anchor.Time, snapshot.BeatsEnabledAt);
        if (searchFrom >= until || !TransportAnchor.TryGetSongTimeAt(snapshot.Anchor, searchFrom, out var song))
        {
            return found;
        }

        var tick = song.SongTime < TimeSpan.Zero ? 0 : snapshot.Plan.TempoMap.TickAt(song.SongTime).Value;
        var (beatTick, downbeat) = snapshot.Plan.NextBeat(tick);
        var beatTime = snapshot.Plan.TempoMap.TimeAt(new Tick(beatTick));
        if (beatTime < song.SongTime)
        {
            (beatTick, downbeat) = snapshot.Plan.NextBeat(beatTick + 1);
            beatTime = snapshot.Plan.TempoMap.TimeAt(new Tick(beatTick));
        }

        TimeSpan offset;
        if (song.LoopEnd > song.LoopStart && beatTime >= song.LoopEnd)
        {
            var loopStartTick = snapshot.Plan.TempoMap.TickAt(song.LoopStart).Value;
            (beatTick, downbeat) = snapshot.Plan.NextBeat(loopStartTick);
            beatTime = snapshot.Plan.TempoMap.TimeAt(new Tick(beatTick));
            if (beatTime >= song.LoopEnd)
            {
                return found;
            }

            offset = (song.LoopEnd - song.SongTime) + (beatTime - song.LoopStart);
        }
        else
        {
            offset = beatTime - song.SongTime;
        }

        var beatTimestamp = searchFrom + offset;
        if (beatTimestamp >= searchFrom && beatTimestamp < until && (!found || beatTimestamp < click.Timestamp))
        {
            click = new AudioMetronomeClick(beatTimestamp, downbeat);
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Sends a channel message to <paramref name="output"/> on the playback thread as soon as possible,
    /// for MIDI thru and auditioning. Safe to call from any thread, including MIDI input callbacks.
    /// </summary>
    public void SendNow(IMidiOutput output, ChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(output);
        _immediateSends.Enqueue(new ImmediateMessage(output, message));
        _workSignal.Set();
    }

    /// <summary>
    /// Maps a clock time (such as a MIDI input timestamp) to the song position playing at that moment,
    /// following loops and seeks. Times during a count-in map to the bars before the start position.
    /// Safe to call from any thread.
    /// </summary>
    /// <returns>False when the transport is stopped, or the time maps before the start of the song.</returns>
    public bool TryGetTickAt(TimeSpan time, out Tick tick)
    {
        ReadAnchors(out var current, out var previous, out _);
        return TransportAnchor.TryGetTickAt(current, previous, time, out tick);
    }

    /// <summary>
    /// The song time heard at a clock time, for the audio engine (ADR 0032): like <see cref="TryGetTickAt"/>, but in song
    /// time (not rounded to ticks), with loop wraps applied, and before the start position during a count-in (negative
    /// before the song's start). Lock-free and allocation-free. Audio callbacks use
    /// <see cref="TryGetAudioSongTimeAt"/> instead, which never waits for a concurrent publication.
    /// </summary>
    /// <returns>False when the transport is stopped.</returns>
    public bool TryGetSongTimeAt(TimeSpan time, out SongPosition position)
    {
        ReadAnchors(out var current, out _, out _);
        return TransportAnchor.TryGetSongTimeAt(current, time, out position);
    }

    /// <summary>
    /// Reads one bounded, coherent snapshot for an audio block and its click lookups. Single audio reader.
    /// If publication overlaps the read, the last complete snapshot is used without waiting.
    /// </summary>
    public bool TryGetAudioSongTimeAt(TimeSpan time, out SongPosition position)
    {
        var snapshot = ReadAudioMetronome();
        if (!_audioBlockPinned || snapshot.ClickGeneration != _audioBlockSnapshot.ClickGeneration || time <= _audioBlockTime)
        {
            _audioBeatBoundary = TimeSpan.MinValue;
            _audioCountInBoundary = TimeSpan.MinValue;
        }
        else
        {
            // Previously rendered beat boundaries follow nudges; count-in clicks stay at their original clock times.
            var delta = unchecked(snapshot.NudgeTicks - _audioBlockSnapshot.NudgeTicks);
            _audioBeatBoundary = Max(ShiftBoundary(_audioBeatBoundary, delta), ShiftBoundary(_audioBlockEnd, delta));
            _audioCountInBoundary = Max(_audioCountInBoundary, _audioBlockEnd);
        }

        _audioBlockSnapshot = snapshot;
        _audioBlockPinned = true;
        _audioBlockTime = time;
        _audioBlockEnd = TimeSpan.MinValue;
        return TransportAnchor.TryGetSongTimeAt(snapshot.Anchor, time, out position);
    }

    private static TimeSpan ShiftBoundary(TimeSpan boundary, long nudgeTicks)
    {
        if (boundary == TimeSpan.MinValue || nudgeTicks == 0)
        {
            return boundary;
        }

        var shifted = (Int128)boundary.Ticks - nudgeTicks;
        return TimeSpan.FromTicks((long)Int128.Clamp(shifted, long.MinValue, long.MaxValue));
    }

    /// <summary>
    /// The transport as last published, read consistently, for reporting it to another process. Lock-free and
    /// allocation-free; any thread.
    /// </summary>
    public TransportSnapshot ReadTransport()
    {
        ReadAnchors(out var current, out var previous, out var version);
        return new TransportSnapshot(State, Position, LoopWraps, TimeSpan.FromTicks(Volatile.Read(ref _countInUntil)), current, previous, version);
    }

    /// <summary>How many plans the playback thread has switched to so far; a plan given to <see cref="Load"/> before that many is no longer read by it.</summary>
    public long LoadsApplied => Interlocked.Read(ref _loadsApplied);

    /// <summary>Commands given so far (every call that queues one: play, stop, seek, load, loop, outputs, and the rest).</summary>
    public long CommandsQueued => Interlocked.Read(ref _commandsQueued);

    /// <summary>
    /// Commands the playback thread has applied so far, and published the transport for. Once this reaches a reading of
    /// <see cref="CommandsQueued"/> taken after a command was given, <see cref="ReadTransport"/> reflects that command.
    /// </summary>
    public long CommandsApplied => Interlocked.Read(ref _commandsApplied);

    public TimingSnapshot SnapshotStatistics() => Statistics.Snapshot();

    private void ReadAnchors(out TransportAnchor current, out TransportAnchor previous, out int version)
    {
        var spinner = default(SpinWait);
        while (true)
        {
            version = Volatile.Read(ref _anchorVersion);
            if ((version & 1) == 0)
            {
                current = _anchor;
                previous = _previousAnchor;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _anchorVersion) == version)
                {
                    return;
                }
            }

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Moves the transport's time mapping by <paramref name="amount"/>: positive makes the song position at any later
    /// moment that much further on. The audio engine uses it to keep MIDI aligned with the audio device's clock, a few
    /// microseconds at a time. Nothing is resent or skipped. Any thread; ignored while stopped.
    /// </summary>
    public void Nudge(TimeSpan amount) => Enqueue(new NudgeCommand(amount));

    /// <summary>Moves the playhead. While playing, sounding notes are released and controller state is chased; a count-in ends.</summary>
    public void Seek(Tick position)
    {
        Volatile.Write(ref _positionTick, position.Value);
        Volatile.Write(ref _countInUntil, 0);
        var plan = Volatile.Read(ref _latestPlan);
        Enqueue(new SeekCommand(position.Value, ChaseState.Compute(plan, position.Value), StoppedChase(plan, position.Value)));
    }

    /// <summary>Stops playback, releasing sounding notes and any held sustain pedal.</summary>
    public void Stop() => Enqueue(new StopCommand());

    /// <summary>Sets or clears the loop. A loop engages when the playhead is before its end.</summary>
    public void SetLoop(LoopRegion? loop)
    {
        lock (_wrapLock)
        {
            _latestLoop = loop;
            Enqueue(new LoopCommand(loop, WrapChase(_latestPlan, loop)));
        }
    }

    // Computed by the caller under _wrapLock, not on the playback thread.
    private ChaseMessage[] WrapChase(PlaybackPlan plan, LoopRegion? loop)
    {
        if (_wrapMemo is { } memo && ReferenceEquals(memo.Plan, plan) && Equals(memo.Loop, loop))
        {
            return memo.Chase;
        }

        var chase = loop is { } l ? ChaseState.AtWrap(plan, l) : [];
        _wrapMemo = (plan, loop, chase);
        return chase;
    }

    /// <summary>
    /// Releases every sounding note, then sends sustain off, All Notes Off, and All Sound Off on all
    /// sixteen channels of every output. Playback continues if it was running.
    /// </summary>
    public void Panic() => Enqueue(new PanicCommand());

    /// <summary>
    /// Applies queued commands and dispatches everything due. Call from one thread only.
    /// </summary>
    /// <returns>How long until there is more work, or <see cref="Timeout.InfiniteTimeSpan"/> when idle.</returns>
    public TimeSpan Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) != 0)
        {
            throw new InvalidOperationException("Pump must not be called concurrently.");
        }

        try
        {
            var now = _clock.Now;
            while (_commands.TryDequeue(out var command))
            {
                Execute(command, now);
                Interlocked.Increment(ref _commandsApplied);
            }

            while (_immediateSends.TryDequeue(out var immediate))
            {
                Record(Send(immediate.Output, immediate.Message, MidiTimestamp.Immediate), TimeSpan.Zero);
            }

            if (!_playing)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var nextImmediate = Advance(ref _immediate, scheduledClass: false, horizon: now, now);
            var nextScheduled = Advance(ref _scheduled, scheduledClass: true, horizon: now + _options.LookAhead, now);
            Volatile.Write(ref _positionTick, Math.Max(_immediate.AnchorTick, TickAtTime(in _immediate, now)));

            var wake = nextScheduled == Never ? nextImmediate : Min(nextImmediate, nextScheduled - _options.LookAhead);
            var wait = wake == Never ? _options.PositionUpdateInterval : Max(TimeSpan.Zero, wake - now);
            return Min(wait, _options.PositionUpdateInterval);
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    public void Dispose() => _workSignal.Dispose();

    private void Enqueue(Command command)
    {
        Interlocked.Increment(ref _commandsQueued);
        _commands.Enqueue(command);
        _workSignal.Set();
    }

    private void Execute(Command command, TimeSpan now)
    {
        switch (command)
        {
            case LoadCommand load:
                if (_playing)
                {
                    Reanchor(ref _immediate, load.Plan);
                    Reanchor(ref _scheduled, load.Plan);
                }

                _plan = load.Plan;
                _wrapChase = load.WrapChase;
                Interlocked.Increment(ref _loadsApplied);
                if (_playing)
                {
                    PublishAnchor(playing: true);
                }
                else
                {
                    ChaseWhileStopped(load.StoppedChase);
                }

                break;
            case OutputsCommand outputs:
                ReleaseAll(now);
                _outputs = outputs.Outputs;
                _slotScheduled = outputs.SlotScheduled;
                _sustain = outputs.Sustain;
                ResyncClicks();
                _stoppedChase = [];
                if (!_playing)
                {
                    ChaseWhileStopped(outputs.StoppedChase);
                }

                break;
            case MetronomeCommand metronome:
                _click = metronome.Click;
                if (!_clickEnabled && metronome.Enabled)
                {
                    _audioBeatsEnabledAt = now;
                }

                _clickEnabled = metronome.Enabled;
                _audioClick = metronome.AudioClick;
                ResyncClicks();
                break;
            case PlayCommand play:
                _clickGeneration++;
                var startAt = now + (play.CountIn?.Duration ?? TimeSpan.Zero);
                Locate(play.Tick, play.Chase, now, startAt);
                _countIn = play.CountIn?.Clicks ?? [];
                _countInStart = now;
                Volatile.Write(ref _countInUntil, play.CountIn is null ? 0 : startAt.Ticks);
                _countInIndex = 0;
                _playing = true;
                Volatile.Write(ref _state, (int)TransportState.Playing);
                PublishAnchor(playing: true);
                break;
            case SeekCommand seek:
                if (_playing)
                {
                    _clickGeneration++;
                    Locate(seek.Tick, seek.Chase, now, now);
                    PublishAnchor(playing: true);
                }
                else
                {
                    ChaseWhileStopped(seek.StoppedChase);
                }

                break;
            case StopCommand:
                _clickGeneration++;
                if (_playing)
                {
                    Volatile.Write(ref _positionTick, Math.Max(_immediate.AnchorTick, TickAtTime(in _immediate, now)));
                }

                ReleaseAll(now);
                ReleaseSustain();
                _playing = false;
                _countIn = [];
                Volatile.Write(ref _countInUntil, 0);
                Volatile.Write(ref _state, (int)TransportState.Stopped);
                PublishAnchor(playing: false);
                break;
            case LoopCommand loop:
                var loopChanged = _loop != loop.Loop;
                _loop = loop.Loop;
                _wrapChase = loop.WrapChase;
                if (loopChanged)
                {
                    // A cursor waiting on the old loop's end has nothing to wrap to now.
                    _immediate.WrapPending = false;
                    _scheduled.WrapPending = false;
                }

                // An unchanged loop publishes nothing, so the anchor before an edit's Load stays the previous one.
                if (_playing && loopChanged)
                {
                    PublishAnchor(playing: true);
                }

                break;
            case PanicCommand:
                ReleaseAll(now);
                SweepAllChannels();
                break;
            case NudgeCommand nudge when _playing:
                _immediate.AnchorTime -= nudge.Amount;
                _scheduled.AnchorTime -= nudge.Amount;
                _audioNudgeTicks = unchecked(_audioNudgeTicks + nudge.Amount.Ticks);
                PublishAnchor(playing: true);
                break;
        }

        PublishAudioMetronome();
    }

    /// <summary>
    /// While stopped, sends the program and controller state at the playhead so live input (thru,
    /// auditioning) hears each track's instrument without pressing play. Only channels whose state
    /// changed are resent; a changed channel's state is resent whole so bank select precedes its program.
    /// </summary>
    /// <remarks>
    /// <paramref name="stopped"/> comes from <see cref="StoppedChase"/>, computed by whoever gave the command, so this
    /// thread only compares it with the last one and sends: it allocates nothing (ADR 0044).
    /// </remarks>
    private void ChaseWhileStopped(StoppedChaseState stopped)
    {
        // A playhead moved since the command was given was moved by a later Seek or Play, which chases for itself.
        if (stopped.Position != Volatile.Read(ref _positionTick))
        {
            return;
        }

        var chase = stopped.Messages;
        var previous = _stoppedChase;
        _stoppedChase = chase;
        for (var start = 0; start < chase.Length;)
        {
            var end = GroupEnd(chase, start);
            var group = chase.AsSpan(start, end - start);
            if (!group.SequenceEqual(GroupOf(previous, chase[start])))
            {
                foreach (var message in group)
                {
                    SendChannel(message.Slot, message.Message, MidiTimestamp.Immediate);
                }
            }

            start = end;
        }

        static int GroupEnd(ChaseMessage[] messages, int start)
        {
            var end = start + 1;
            while (end < messages.Length && SameChannel(messages[end], messages[start]))
            {
                end++;
            }

            return end;
        }

        static ReadOnlySpan<ChaseMessage> GroupOf(ChaseMessage[] messages, in ChaseMessage key)
        {
            for (var start = 0; start < messages.Length; start = GroupEnd(messages, start))
            {
                if (SameChannel(messages[start], key))
                {
                    return messages.AsSpan(start, GroupEnd(messages, start) - start);
                }
            }

            return [];
        }

        static bool SameChannel(in ChaseMessage a, in ChaseMessage b) => a.Slot == b.Slot && a.Message.Channel == b.Message.Channel;
    }

    /// <summary>
    /// The state <see cref="ChaseWhileStopped"/> sends with the playhead at <paramref name="position"/>: events at the
    /// playhead included (a program change at the very start sets the track's instrument), pedals left alone (so a
    /// sustain held in the song does not latch notes played live), and no SysEx (that hardware parameter automation
    /// chases is left for play: it is not channel state). Grouped by output and channel, in order of first appearance.
    /// Computed by the caller, not on the playback thread.
    /// </summary>
    private static StoppedChaseState StoppedChase(PlaybackPlan plan, long position) =>
        new(position, [.. ChaseState.Compute(plan, position + 1)
            .Where(c => c.Payload is null && (c.Message.Kind != ChannelMessageKind.ControlChange || c.Message.Data1 is < 64 or > 69))
            .GroupBy(c => (c.Slot, c.Message.Channel.Index))
            .SelectMany(g => g)]);

    /// <summary>Moves both cursors so <paramref name="tick"/> plays at <paramref name="startAt"/> (later than now during a count-in).</summary>
    private void Locate(long tick, ChaseMessage[] chase, TimeSpan now, TimeSpan startAt)
    {
        _stoppedChase = [];
        ReleaseAll(now);
        ReleaseSustain();
        foreach (var message in chase)
        {
            SendChase(message, MidiTimestamp.Immediate);
        }

        _countIn = [];
        _immediate = Cursor.At(_plan, tick, startAt);
        _scheduled = Cursor.At(_plan, tick, startAt);
    }

    /// <summary>Publishes the immediate cursor's mapping for <see cref="TryGetTickAt"/>, keeping the previous one.</summary>
    private void PublishAnchor(bool playing)
    {
        Interlocked.Increment(ref _anchorVersion);
        _previousAnchor = _anchor;
        var engaged = _loop is { } loop && _immediate.Engages(loop) ? loop : null;
        _anchor = new TransportAnchor(playing, _immediate.AnchorTime, _immediate.AnchorSongTime, _plan.TempoMap, engaged?.Start.Value ?? 0, engaged?.End.Value ?? 0, _clickGeneration);
        Interlocked.Increment(ref _anchorVersion);
    }

    private void PublishAudioMetronome()
    {
        Interlocked.Increment(ref _audioMetronomeVersion);
        _audioMetronome = new AudioMetronomeSnapshot(
            _plan,
            _anchor,
            _audioClick,
            _clickEnabled,
            _audioBeatsEnabledAt,
            _countIn,
            _countInStart,
            _clickGeneration,
            _audioNudgeTicks);
        Interlocked.Increment(ref _audioMetronomeVersion);
    }

    private AudioMetronomeSnapshot ReadAudioMetronome()
    {
        var before = Volatile.Read(ref _audioMetronomeVersion);
        if ((before & 1) != 0)
        {
            return _audioMetronomeReaderCache;
        }

        var snapshot = _audioMetronome;
        Interlocked.MemoryBarrier();
        if (before != Volatile.Read(ref _audioMetronomeVersion))
        {
            return _audioMetronomeReaderCache;
        }

        _audioMetronomeReaderCache = snapshot;
        return snapshot;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b, TimeSpan c) => a > b ? (a > c ? a : c) : (b > c ? b : c);

    /// <summary>Points both cursors' next click at the first beat they have not yet passed.</summary>
    private void ResyncClicks()
    {
        if (!_playing)
        {
            return;
        }

        (_immediate.NextClick, _immediate.NextClickIsDownbeat) = _plan.NextBeat(Math.Max(_immediate.AnchorTick, _immediate.ProcessedThrough + 1));
        (_scheduled.NextClick, _scheduled.NextClickIsDownbeat) = _plan.NextBeat(Math.Max(_scheduled.AnchorTick, _scheduled.ProcessedThrough + 1));
    }

    /// <summary>Moves a cursor onto a new plan at the first tick it has not yet processed.</summary>
    private void Reanchor(ref Cursor cursor, PlaybackPlan next)
    {
        var firstUnprocessed = cursor.ProcessedThrough + 1;
        var time = TimeOf(in cursor, firstUnprocessed);
        var wrapPending = _loop is { } loop && cursor.Engages(loop) && firstUnprocessed == loop.End.Value;
        cursor = Cursor.At(next, firstUnprocessed, time);
        cursor.WrapPending = wrapPending;
    }

    /// <summary>True when the metronome sounds through an output of this delivery class.</summary>
    private bool ClicksIn(bool scheduledClass) =>
        _click is { } click && InClass(click.Slot, scheduledClass);

    private TimeSpan Advance(ref Cursor cursor, bool scheduledClass, TimeSpan horizon, TimeSpan now)
    {
        var events = _plan.Events;
        var wraps = 0;
        while (true)
        {
            while (cursor.Index < events.Length && !InClass(events[cursor.Index].Slot, scheduledClass))
            {
                cursor.Index++;
            }

            var loop = _loop;
            var looping = loop is not null && cursor.Engages(loop) && wraps < MaxLoopWrapsPerPump;
            var wrapTime = looping ? TimeOf(in cursor, loop!.End.Value) : Never;

            // A cursor waiting on the end wraps at its own anchor time; that is not a loop too short to measure.
            if (looping && wrapTime <= cursor.AnchorTime && cursor.AnchorTick < loop!.End.Value)
            {
                // A loop too short to measure in TimeSpan resolution would never advance time.
                looping = false;
                wrapTime = Never;
            }

            var hasEvent = cursor.Index < events.Length && !(looping && events[cursor.Index].Tick >= loop!.End.Value);
            var eventTime = hasEvent ? TimeOf(in cursor, events[cursor.Index].Tick) : Never;
            var release = NextRelease(scheduledClass, out var releaseTime);
            var clicks = ClicksIn(scheduledClass);
            var clickTime = clicks && _clickEnabled && !(looping && cursor.NextClick >= loop!.End.Value) ? TimeOf(in cursor, cursor.NextClick) : Never;
            var countInTime = clicks && _countInIndex < _countIn.Length ? _countInStart + _countIn[_countInIndex].Offset : Never;

            if (release >= 0 && releaseTime <= horizon && releaseTime <= eventTime && releaseTime <= wrapTime && releaseTime <= clickTime && releaseTime <= countInTime)
            {
                Release(release, now);
                continue;
            }

            if (countInTime <= horizon)
            {
                DispatchClick(_countIn[_countInIndex].Downbeat, countInTime, scheduledClass, now);
                _countInIndex++;
                continue;
            }

            if (wrapTime <= horizon && wrapTime <= eventTime && wrapTime <= clickTime)
            {
                ReleaseClass(scheduledClass, wrapTime, now);
                foreach (var message in _wrapChase)
                {
                    if (InClass(message.Slot, scheduledClass))
                    {
                        // Tracked like any dispatched pedal, so stopping lifts a sustain the wrap put down.
                        SendChase(message, scheduledClass ? MidiTimestamp.At(wrapTime) : MidiTimestamp.Immediate);
                        if (message.Payload is null)
                        {
                            TrackSustain(message.Slot, message.Message);
                        }
                    }
                }

                cursor = Cursor.At(_plan, loop!.Start.Value, wrapTime);
                if (!scheduledClass)
                {
                    Interlocked.Increment(ref _loopWraps);
                    PublishAnchor(playing: true);
                }

                wraps++;
                continue;
            }

            if (clickTime <= horizon && clickTime <= eventTime)
            {
                DispatchClick(cursor.NextClickIsDownbeat, clickTime, scheduledClass, now);
                (cursor.NextClick, cursor.NextClickIsDownbeat) = _plan.NextBeat(cursor.NextClick + 1);
                continue;
            }

            if (eventTime <= horizon)
            {
                Dispatch(in events[cursor.Index], eventTime, in cursor, scheduledClass, now);
                cursor.Index++;
                continue;
            }

            cursor.ProcessedThrough = TickAtTime(in cursor, horizon);
            return Min(Min(eventTime, Min(releaseTime, wrapTime)), Min(clickTime, countInTime));
        }
    }

    private void Dispatch(in PlanEvent e, TimeSpan due, in Cursor cursor, bool scheduledClass, TimeSpan now)
    {
        var output = _outputs[e.Slot];
        if (output is null)
        {
            return;
        }

        var lateness = now - due;
        var timestamp = scheduledClass ? MidiTimestamp.At(due) : MidiTimestamp.Immediate;
        SendResult result;
        if (e.IsNote)
        {
            if (lateness > _options.MaxNoteLateness)
            {
                Statistics.RecordSkippedLateNote();
                return;
            }

            if (_activeCount == _active.Length)
            {
                Statistics.RecordNoteOverflow();
                return;
            }

            result = Send(output, e.Message, timestamp);
            if (result == SendResult.Sent)
            {
                var releaseTick = e.Tick + e.DurationTicks;
                if (_loop is { } loop && e.Tick < loop.End.Value && releaseTick > loop.End.Value && cursor.AnchorTick < loop.End.Value)
                {
                    releaseTick = loop.End.Value;
                }

                var off = ChannelMessage.NoteOff(e.Message.Channel, e.Message.Note, e.ReleaseVelocity);
                _active[_activeCount++] = new ActiveNote(output, off, due, TimeOf(in cursor, releaseTick), scheduledClass);
            }
        }
        else if (e.PayloadIndex >= 0)
        {
            result = output.Send(_plan.Payload(e.PayloadIndex), timestamp);
        }
        else
        {
            result = Send(output, e.Message, timestamp);
            TrackSustain(e.Slot, e.Message);
        }

        Record(result, lateness);
    }

    private void DispatchClick(bool downbeat, TimeSpan due, bool scheduledClass, TimeSpan now)
    {
        var click = _click!;
        var output = _outputs[click.Slot]!;
        var lateness = now - due;
        if (lateness > _options.MaxNoteLateness)
        {
            Statistics.RecordSkippedLateNote();
            return;
        }

        if (_activeCount == _active.Length)
        {
            Statistics.RecordNoteOverflow();
            return;
        }

        var note = downbeat ? click.AccentNote : click.BeatNote;
        var on = ChannelMessage.NoteOn(click.Channel, note, downbeat ? click.AccentVelocity : click.BeatVelocity);
        var result = Send(output, on, scheduledClass ? MidiTimestamp.At(due) : MidiTimestamp.Immediate);
        if (result == SendResult.Sent)
        {
            _active[_activeCount++] = new ActiveNote(output, ChannelMessage.NoteOff(click.Channel, note, Velocity.DefaultRelease), due, due + click.Length, scheduledClass);
        }

        Record(result, lateness);
    }

    private int NextRelease(bool scheduledClass, out TimeSpan time)
    {
        var best = -1;
        time = Never;
        for (var i = 0; i < _activeCount; i++)
        {
            if (_active[i].Scheduled == scheduledClass && _active[i].ReleaseTime < time)
            {
                best = i;
                time = _active[i].ReleaseTime;
            }
        }

        return best;
    }

    private void Release(int index, TimeSpan now)
    {
        var note = _active[index];
        var timestamp = note.Scheduled ? MidiTimestamp.At(note.ReleaseTime) : MidiTimestamp.Immediate;
        Record(Send(note.Output, note.Off, timestamp), now - note.ReleaseTime);
        RemoveActive(index);
    }

    /// <summary>Releases every note of one class at <paramref name="time"/> (used at loop wrap).</summary>
    private void ReleaseClass(bool scheduledClass, TimeSpan time, TimeSpan now)
    {
        for (var i = _activeCount - 1; i >= 0; i--)
        {
            if (_active[i].Scheduled == scheduledClass)
            {
                _active[i] = _active[i] with { ReleaseTime = Min(_active[i].ReleaseTime, time) };
                Release(i, now);
            }
        }
    }

    /// <summary>
    /// Releases every sounding note now. A note whose start was handed to a scheduling endpoint but has
    /// not yet sounded is released at its start time, so its release can never arrive first.
    /// </summary>
    private void ReleaseAll(TimeSpan now)
    {
        for (var i = _activeCount - 1; i >= 0; i--)
        {
            var note = _active[i];
            var timestamp = note.Scheduled && note.StartTime > now ? MidiTimestamp.At(note.StartTime) : MidiTimestamp.Immediate;
            Record(Send(note.Output, note.Off, timestamp), TimeSpan.Zero);
            RemoveActive(i);
        }
    }

    private void RemoveActive(int index)
    {
        _active[index] = _active[--_activeCount];
        _active[_activeCount] = default;
    }

    private void TrackSustain(int slot, ChannelMessage message)
    {
        if (message.Kind == ChannelMessageKind.ControlChange && message.Data1 == ControllerNumber.SustainPedal.Value)
        {
            _sustain[(slot * ChannelsPerSlot) + message.Channel.Index] = message.Data2 >= 64;
        }
    }

    private void ReleaseSustain()
    {
        for (var i = 0; i < _sustain.Length; i++)
        {
            if (_sustain[i])
            {
                _sustain[i] = false;
                var off = ChannelMessage.ControlChange(MidiChannel.FromIndex(i % ChannelsPerSlot), ControllerNumber.SustainPedal, SevenBitValue.Min);
                SendChannel(i / ChannelsPerSlot, off, MidiTimestamp.Immediate);
            }
        }
    }

    private void SweepAllChannels()
    {
        Array.Clear(_sustain);
        for (var slot = 0; slot < _outputs.Length; slot++)
        {
            for (var channel = 0; channel < ChannelsPerSlot; channel++)
            {
                var ch = MidiChannel.FromIndex(channel);
                SendChannel(slot, ChannelMessage.ControlChange(ch, ControllerNumber.SustainPedal, SevenBitValue.Min), MidiTimestamp.Immediate);
                SendChannel(slot, ChannelMessage.ControlChange(ch, ControllerNumber.AllNotesOff, SevenBitValue.Min), MidiTimestamp.Immediate);
                SendChannel(slot, ChannelMessage.ControlChange(ch, ControllerNumber.AllSoundOff, SevenBitValue.Min), MidiTimestamp.Immediate);
            }
        }
    }

    private void SendChase(in ChaseMessage message, MidiTimestamp timestamp)
    {
        if (message.Payload is not { } payload)
        {
            SendChannel(message.Slot, message.Message, timestamp);
        }
        else if ((uint)message.Slot < (uint)_outputs.Length && _outputs[message.Slot] is { } output)
        {
            Record(output.Send(payload.Span, timestamp), TimeSpan.Zero);
        }
    }

    private void SendChannel(int slot, ChannelMessage message, MidiTimestamp timestamp)
    {
        if ((uint)slot < (uint)_outputs.Length && _outputs[slot] is { } output)
        {
            Record(Send(output, message, timestamp), TimeSpan.Zero);
        }
    }

    private static SendResult Send(IMidiOutput output, ChannelMessage message, MidiTimestamp timestamp)
    {
        Span<byte> buffer = stackalloc byte[3];
        var length = message.CopyTo(buffer);
        return output.Send(buffer[..length], timestamp);
    }

    private void Record(SendResult result, TimeSpan lateness)
    {
        if (result == SendResult.Sent)
        {
            Statistics.RecordDispatch(lateness);
        }
        else
        {
            Statistics.RecordDropped();
        }
    }

    private bool InClass(int slot, bool scheduledClass) =>
        (uint)slot < (uint)_outputs.Length && _outputs[slot] is not null && _slotScheduled[slot] == scheduledClass;

    private TimeSpan TimeOf(in Cursor cursor, long tick) =>
        cursor.AnchorTime + (_plan.TempoMap.TimeAt(new Tick(tick)) - cursor.AnchorSongTime);

    /// <summary>The last tick due at or before <paramref name="time"/>, or one before the anchor if none.</summary>
    private long TickAtTime(in Cursor cursor, TimeSpan time) =>
        time < cursor.AnchorTime
            ? cursor.AnchorTick - 1
            : _plan.TempoMap.TickAt(cursor.AnchorSongTime + (time - cursor.AnchorTime)).Value;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    /// <summary>A position in the plan, and the mapping from ticks to clock time for the current pass.</summary>
    private struct Cursor
    {
        public int Index;
        public long AnchorTick;
        public TimeSpan AnchorTime;
        public TimeSpan AnchorSongTime;
        public long ProcessedThrough;
        public long NextClick;
        public bool NextClickIsDownbeat;

        /// <summary>
        /// Set when a plan swap re-anchored a cursor that was in the loop exactly on the loop's end: it has processed
        /// everything before the end but not yet wrapped, so the loop stays engaged and it wraps at the end.
        /// </summary>
        public bool WrapPending;

        /// <summary>True when <paramref name="loop"/> engages for this cursor: it is before the end, or waiting on the end to wrap.</summary>
        public readonly bool Engages(LoopRegion loop) =>
            AnchorTick < loop.End.Value || (WrapPending && AnchorTick == loop.End.Value);

        public static Cursor At(PlaybackPlan plan, long tick, TimeSpan time)
        {
            var (click, downbeat) = plan.NextBeat(tick);
            return new()
            {
                Index = plan.FirstIndexAtOrAfter(tick),
                AnchorTick = tick,
                AnchorTime = time,
                AnchorSongTime = plan.TempoMap.TimeAt(new Tick(tick)),
                ProcessedThrough = tick - 1,
                NextClick = click,
                NextClickIsDownbeat = downbeat,
            };
        }
    }

    private readonly record struct ImmediateMessage(IMidiOutput Output, ChannelMessage Message);

    private readonly record struct ActiveNote(IMidiOutput Output, ChannelMessage Off, TimeSpan StartTime, TimeSpan ReleaseTime, bool Scheduled);

    private abstract record Command;

    /// <summary>A stopped chase and the playhead it was computed for.</summary>
    private sealed record StoppedChaseState(long Position, ChaseMessage[] Messages);

    private sealed record LoadCommand(PlaybackPlan Plan, ChaseMessage[] WrapChase, StoppedChaseState StoppedChase) : Command;

    // Everything the playback thread needs for new outputs, made by the caller so that thread allocates nothing.
    private sealed record OutputsCommand(IMidiOutput?[] Outputs, bool[] SlotScheduled, bool[] Sustain, StoppedChaseState StoppedChase) : Command;

    private sealed record PlayCommand(long Tick, ChaseMessage[] Chase, CountIn? CountIn) : Command;

    private sealed record MetronomeCommand(MetronomeClick? Click, bool Enabled, bool AudioClick) : Command;

    private sealed record SeekCommand(long Tick, ChaseMessage[] Chase, StoppedChaseState StoppedChase) : Command;

    private sealed record StopCommand : Command;

    private sealed record LoopCommand(LoopRegion? Loop, ChaseMessage[] WrapChase) : Command;

    private sealed record PanicCommand : Command;

    private sealed record NudgeCommand(TimeSpan Amount) : Command;

    private readonly record struct AudioMetronomeSnapshot(
        PlaybackPlan Plan,
        TransportAnchor Anchor,
        bool Configured,
        bool BeatsEnabled,
        TimeSpan BeatsEnabledAt,
        ImmutableArray<CountInClick> CountIn,
        TimeSpan CountInStart,
        long ClickGeneration,
        long NudgeTicks);
}

/// <summary>A song position heard at some moment: song time, the engaged loop's bounds (equal when none), and the tempo there.</summary>
public readonly record struct SongPosition(TimeSpan SongTime, TimeSpan LoopStart, TimeSpan LoopEnd, double BeatsPerMinute, long ClickGeneration = 0);
