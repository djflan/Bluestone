using System.Collections.Concurrent;
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
public sealed class PlaybackEngine : IDisposable
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
    private TimedClick[] _countIn = [];
    private int _countInIndex;

    // The immediate cursor's tick-to-time mapping, published for input timestamping (seqlock).
    private int _anchorVersion;
    private Anchor _anchor;
    private Anchor _previousAnchor;
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
            Enqueue(new LoadCommand(plan, WrapChase(plan, _latestLoop)));
        }
    }

    /// <summary>Sets the output for each slot used by plan bindings. Sounding notes are released first.</summary>
    public void SetOutputs(IReadOnlyList<IMidiOutput?> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        Enqueue(new OutputsCommand([.. outputs]));
    }

    /// <summary>
    /// Starts (or restarts) playback from <paramref name="from"/>, or from <see cref="Position"/>, chasing
    /// controller state. With <paramref name="countIn"/>, the transport first waits out the count-in,
    /// sounding its clicks on the metronome (if one is set), then plays.
    /// </summary>
    public void Play(Tick? from = null, CountIn? countIn = null)
    {
        var tick = from ?? Position;
        Volatile.Write(ref _positionTick, tick.Value);
        Volatile.Write(ref _countInUntil, countIn is null ? 0 : (_clock.Now + countIn.Duration).Ticks);

        Enqueue(new PlayCommand(tick.Value, ChaseState.Compute(Volatile.Read(ref _latestPlan), tick.Value), countIn));
    }

    /// <summary>How many times playback has wrapped from the loop's end to its start, ever. Compare two readings to tell whether a pass looped.</summary>
    public long LoopWraps => Interlocked.Read(ref _loopWraps);

    /// <summary>True while a count-in requested by <see cref="Play"/> is still running.</summary>
    public bool IsCountingIn => State == TransportState.Playing && _clock.Now.Ticks < Volatile.Read(ref _countInUntil);

    /// <summary>
    /// Sets the metronome's sound and output slot, or <see langword="null"/> for none. Clicks sound on
    /// every beat while playing when <paramref name="enabled"/>; count-in clicks sound whenever a
    /// metronome is set.
    /// </summary>
    public void SetMetronome(MetronomeClick? click, bool enabled) => Enqueue(new MetronomeCommand(click, enabled));

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
        Anchor current, previous;
        var spinner = default(SpinWait);
        while (true)
        {
            var version = Volatile.Read(ref _anchorVersion);
            if ((version & 1) == 0)
            {
                current = _anchor;
                previous = _previousAnchor;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _anchorVersion) == version)
                {
                    break;
                }
            }

            spinner.SpinOnce();
        }

        if (!current.Playing || current.Tempo is null)
        {
            tick = Tick.Zero;
            return false;
        }

        // An input that arrived just before a loop wrap or seek belongs to the pass it was played in.
        var anchor = time < current.Time && previous.Playing && previous.Tempo is not null && time >= previous.Time ? previous : current;
        var songTime = anchor.SongTime + (time - anchor.Time);
        if (songTime < TimeSpan.Zero)
        {
            tick = Tick.Zero;
            return false;
        }

        var value = anchor.Tempo!.TickAt(songTime).Value;

        // An input stamped after a loop wrap that the playback thread has not processed yet belongs
        // to the next pass, at the start of the loop.
        if (anchor.LoopEnd > anchor.LoopStart && value >= anchor.LoopEnd)
        {
            value = anchor.LoopStart + ((value - anchor.LoopEnd) % (anchor.LoopEnd - anchor.LoopStart));
        }

        tick = new Tick(value);
        return true;
    }

    /// <summary>
    /// The song time heard at a clock time, for the audio engine (ADR 0032): like <see cref="TryGetTickAt"/>, but in song
    /// time (not rounded to ticks), with loop wraps applied, and before the start position during a count-in (negative
    /// before the song's start). Lock-free and allocation-free; safe on an audio thread.
    /// </summary>
    /// <returns>False when the transport is stopped.</returns>
    public bool TryGetSongTimeAt(TimeSpan time, out SongPosition position)
    {
        Anchor anchor;
        var spinner = default(SpinWait);
        while (true)
        {
            var version = Volatile.Read(ref _anchorVersion);
            if ((version & 1) == 0)
            {
                anchor = _anchor;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _anchorVersion) == version)
                {
                    break;
                }
            }

            spinner.SpinOnce();
        }

        if (!anchor.Playing || anchor.Tempo is not { } tempo)
        {
            position = default;
            return false;
        }

        var song = anchor.SongTime + (time - anchor.Time);
        TimeSpan loopStart = TimeSpan.Zero, loopEnd = TimeSpan.Zero;
        if (anchor.LoopEnd > anchor.LoopStart)
        {
            loopStart = tempo.TimeAt(new Tick(anchor.LoopStart));
            loopEnd = tempo.TimeAt(new Tick(anchor.LoopEnd));
            if (song >= loopEnd && loopEnd > loopStart)
            {
                song = loopStart + TimeSpan.FromTicks((song - loopEnd).Ticks % (loopEnd - loopStart).Ticks);
            }
        }

        var bpm = (song > TimeSpan.Zero ? tempo.TempoAt(tempo.TickAt(song)) : tempo.Changes[0].Tempo).BeatsPerMinute;
        position = new SongPosition(song, loopStart, loopEnd, bpm);
        return true;
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
        Enqueue(new SeekCommand(position.Value, ChaseState.Compute(Volatile.Read(ref _latestPlan), position.Value)));
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
                if (_playing)
                {
                    PublishAnchor(playing: true);
                }
                else
                {
                    ChaseWhileStopped();
                }

                break;
            case OutputsCommand outputs:
                ReleaseAll(now);
                _outputs = outputs.Outputs;
                _slotScheduled = [.. outputs.Outputs.Select(o => o?.Endpoint.Capabilities.HasFlag(EndpointCapabilities.ScheduledDelivery) == true)];
                _sustain = new bool[outputs.Outputs.Length * ChannelsPerSlot];
                ResyncClicks();
                _stoppedChase = [];
                if (!_playing)
                {
                    ChaseWhileStopped();
                }

                break;
            case MetronomeCommand metronome:
                _click = metronome.Click;
                _clickEnabled = metronome.Enabled;
                ResyncClicks();
                break;
            case PlayCommand play:
                var startAt = now + (play.CountIn?.Duration ?? TimeSpan.Zero);
                Locate(play.Tick, play.Chase, now, startAt);
                _countIn = play.CountIn is { } countIn ? [.. countIn.Clicks.Select(c => new TimedClick(now + c.Offset, c.Downbeat))] : [];
                _countInIndex = 0;
                _playing = true;
                Volatile.Write(ref _state, (int)TransportState.Playing);
                PublishAnchor(playing: true);
                break;
            case SeekCommand seek:
                if (_playing)
                {
                    Locate(seek.Tick, seek.Chase, now, now);
                    PublishAnchor(playing: true);
                }
                else
                {
                    ChaseWhileStopped();
                }

                break;
            case StopCommand:
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
                _loop = loop.Loop;
                _wrapChase = loop.WrapChase;
                if (_playing)
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
                PublishAnchor(playing: true);
                break;
        }
    }

    /// <summary>
    /// While stopped, sends the program and controller state at the playhead so live input (thru,
    /// auditioning) hears each track's instrument without pressing play. Only channels whose state
    /// changed are resent; a changed channel's state is resent whole so bank select precedes its program.
    /// </summary>
    private void ChaseWhileStopped()
    {
        // Include events at the playhead: a program change at the very start sets the track's instrument.
        // Pedals are left alone so a sustain held in the song does not latch notes played live.
        // SysEx that hardware parameter automation chases is left for play: it is not channel state.
        var chase = ChaseState.Compute(_plan, Volatile.Read(ref _positionTick) + 1)
            .Where(c => c.PayloadIndex < 0 && (c.Message.Kind != ChannelMessageKind.ControlChange || c.Message.Data1 is < 64 or > 69))
            .ToArray();
        var previous = _stoppedChase.ToLookup(c => (c.Slot, c.Message.Channel.Index));
        _stoppedChase = chase;
        foreach (var channel in chase.GroupBy(c => (c.Slot, c.Message.Channel.Index)))
        {
            if (channel.SequenceEqual(previous[channel.Key]))
            {
                continue;
            }

            foreach (var message in channel)
            {
                SendChannel(message.Slot, message.Message, MidiTimestamp.Immediate);
            }
        }
    }

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
        var engaged = _loop is { } loop && _immediate.AnchorTick < loop.End.Value ? loop : null;
        _anchor = new Anchor(playing, _immediate.AnchorTime, _immediate.AnchorSongTime, _plan.TempoMap, engaged?.Start.Value ?? 0, engaged?.End.Value ?? 0);
        Interlocked.Increment(ref _anchorVersion);
    }

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
        cursor = Cursor.At(next, firstUnprocessed, time);
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
            var looping = loop is not null && cursor.AnchorTick < loop.End.Value && wraps < MaxLoopWrapsPerPump;
            var wrapTime = looping ? TimeOf(in cursor, loop!.End.Value) : Never;
            if (looping && wrapTime <= cursor.AnchorTime)
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
            var countInTime = clicks && _countInIndex < _countIn.Length ? _countIn[_countInIndex].Time : Never;

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
                        if (message.PayloadIndex < 0)
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
            result = output.Send(_plan.Payloads[e.PayloadIndex].Span, timestamp);
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
        if (message.PayloadIndex < 0)
        {
            SendChannel(message.Slot, message.Message, timestamp);
        }
        else if ((uint)message.Slot < (uint)_outputs.Length && _outputs[message.Slot] is { } output && message.PayloadIndex < _plan.Payloads.Length)
        {
            Record(output.Send(_plan.Payloads[message.PayloadIndex].Span, timestamp), TimeSpan.Zero);
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

    /// <summary>A published tick-to-time mapping: <see cref="SongTime"/> plays at clock time <see cref="Time"/>.</summary>
    /// <remarks><see cref="LoopStart"/> and <see cref="LoopEnd"/> are the loop the cursor will wrap at, or both zero.</remarks>
    private readonly record struct Anchor(bool Playing, TimeSpan Time, TimeSpan SongTime, TempoMap? Tempo, long LoopStart, long LoopEnd);

    private readonly record struct TimedClick(TimeSpan Time, bool Downbeat);

    private readonly record struct ImmediateMessage(IMidiOutput Output, ChannelMessage Message);

    private readonly record struct ActiveNote(IMidiOutput Output, ChannelMessage Off, TimeSpan StartTime, TimeSpan ReleaseTime, bool Scheduled);

    private abstract record Command;

    private sealed record LoadCommand(PlaybackPlan Plan, ChaseMessage[] WrapChase) : Command;

    private sealed record OutputsCommand(IMidiOutput?[] Outputs) : Command;

    private sealed record PlayCommand(long Tick, ChaseMessage[] Chase, CountIn? CountIn) : Command;

    private sealed record MetronomeCommand(MetronomeClick? Click, bool Enabled) : Command;

    private sealed record SeekCommand(long Tick, ChaseMessage[] Chase) : Command;

    private sealed record StopCommand : Command;

    private sealed record LoopCommand(LoopRegion? Loop, ChaseMessage[] WrapChase) : Command;

    private sealed record PanicCommand : Command;

    private sealed record NudgeCommand(TimeSpan Amount) : Command;
}

/// <summary>A song position heard at some moment: song time, the engaged loop's bounds (equal when none), and the tempo there.</summary>
public readonly record struct SongPosition(TimeSpan SongTime, TimeSpan LoopStart, TimeSpan LoopEnd, double BeatsPerMinute);
