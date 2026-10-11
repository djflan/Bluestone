namespace Bluestone.Audio;

/// <summary>The song position a timeline reports for a moment, in seconds of song time.</summary>
/// <param name="SongSeconds">Song time heard at that moment (loop wraps already applied).</param>
/// <param name="LoopStartSeconds">Start of the engaged loop; equal to <paramref name="LoopEndSeconds"/> when no loop is engaged.</param>
/// <param name="LoopEndSeconds">End of the engaged loop (exclusive).</param>
/// <param name="Tempo">Beats per minute at that position.</param>
public readonly record struct TimelinePosition(double SongSeconds, double LoopStartSeconds = 0, double LoopEndSeconds = 0, double Tempo = 120);

/// <summary>
/// The transport the audio engine follows: the MIDI playback engine's anchors in the application. Called on the audio
/// thread once per device callback, so implementations must be lock-free and allocation-free.
/// </summary>
public interface IAudioTimeline
{
    /// <param name="timestamp">A moment in <see cref="System.Diagnostics.Stopwatch"/> ticks (when a block is heard).</param>
    /// <param name="position">The song position heard at that moment.</param>
    /// <returns>False when the transport is stopped.</returns>
    bool TryGetPosition(long timestamp, out TimelinePosition position);
}

/// <summary>
/// The audio engine's song position. The device's sample clock advances it: each block moves it by exactly the
/// block's frames, so audio is continuous. It is either driven directly (<see cref="Play"/>, <see cref="Stop"/>,
/// <see cref="Locate"/>, <see cref="SetLoop"/>; tests and the offline driver) or follows an <see cref="IAudioTimeline"/>:
/// once per block it compares its own position with the timeline's position at the moment the block will be heard,
/// and only relocates when they differ by more than the tolerance (a seek, a start, a stop). The small, slow
/// difference between the device clock and the timeline is measured (<see cref="TakeDrift"/>) so the timeline can be
/// moved towards the audio, never the other way round. See ADR 0032.
/// </summary>
public sealed class AudioTransport
{
    private readonly Lock _gate = new();
    private Request _request = new(1, false, 0, 0, 0, 0);
    private IAudioTimeline? _timeline;
    private int _toleranceFrames;

    // Audio thread.
    private int _appliedVersion;
    private long _appliedLocate;
    private bool _playing;
    private long _frame;
    private long _loopStart;
    private long _loopEnd;
    private bool _jumped = true;
    private double _tempo = 120;
    private int _compensation;

    // Published for other threads.
    private long _position;
    private int _isPlaying;
    private long _driftSum;
    private long _driftCount;

    /// <summary>The song frame the engine last rendered up to (any thread).</summary>
    public long Position => Volatile.Read(ref _position);

    public bool IsPlaying => Volatile.Read(ref _isPlaying) != 0;

    /// <summary>Starts playing, from <paramref name="fromFrame"/> or from the current position.</summary>
    public void Play(long? fromFrame = null) => Change(r => fromFrame is { } f ? r with { Playing = true, LocateVersion = r.LocateVersion + 1, LocateFrame = f } : r with { Playing = true });

    public void Stop() => Change(r => r with { Playing = false });

    public void Locate(long frame) => Change(r => r with { LocateVersion = r.LocateVersion + 1, LocateFrame = frame });

    /// <summary>Sets a loop in song frames; it engages while the position is before <paramref name="endFrame"/>. Equal bounds clear it.</summary>
    public void SetLoop(long startFrame, long endFrame) => Change(r => r with { LoopStart = startFrame, LoopEnd = Math.Max(startFrame, endFrame) });

    /// <summary>Follows a timeline, or stops following and obeys the methods above again.</summary>
    /// <param name="timeline">The timeline, or null.</param>
    /// <param name="toleranceFrames">A difference from the timeline larger than this relocates the audio.</param>
    public void Follow(IAudioTimeline? timeline, int toleranceFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(toleranceFrames);
        lock (_gate)
        {
            Volatile.Write(ref _toleranceFrames, toleranceFrames);
            Volatile.Write(ref _timeline, timeline);
        }
    }

    /// <summary>
    /// The average of (audio position − timeline position) in frames over the callbacks since the last call, and how
    /// many callbacks that covers. Positive means the audio is ahead of the timeline.
    /// </summary>
    public (double AverageFrames, long Callbacks) TakeDrift()
    {
        var count = Interlocked.Exchange(ref _driftCount, 0);
        var sum = Interlocked.Exchange(ref _driftSum, 0);
        return count == 0 ? (0, 0) : ((double)sum / count, count);
    }

    /// <summary>
    /// Applies requests, or synchronises with the timeline for a block heard at <paramref name="heardTimestamp"/>. Audio
    /// thread. When following a timeline, the block renders <paramref name="compensationFrames"/> ahead of the timeline's
    /// position (the graph's latency, ADR 0035), so the master puts out the timeline's position when it is heard; a change
    /// of that amount relocates.
    /// </summary>
    internal void BeginBlock(long heardTimestamp, int sampleRate, int compensationFrames = 0)
    {
        var timeline = Volatile.Read(ref _timeline);
        if (timeline is null)
        {
            ApplyRequest();
            return;
        }

        if (!timeline.TryGetPosition(heardTimestamp, out var position))
        {
            if (_playing)
            {
                _playing = false;
                _jumped = true;
            }

            return;
        }

        var expected = (long)Math.Round(position.SongSeconds * sampleRate);
        _loopStart = (long)Math.Round(position.LoopStartSeconds * sampleRate);
        _loopEnd = (long)Math.Round(position.LoopEndSeconds * sampleRate);
        _tempo = position.Tempo;
        expected = Ahead(expected, compensationFrames);
        if (!_playing || compensationFrames != _compensation)
        {
            _playing = true;
            _compensation = compensationFrames;
            _frame = expected;
            _jumped = true;
            return;
        }

        var difference = _frame - expected;
        if (Math.Abs(difference) > Volatile.Read(ref _toleranceFrames))
        {
            _frame = expected;
            _jumped = true;
            return;
        }

        Interlocked.Add(ref _driftSum, difference);
        Interlocked.Increment(ref _driftCount);
    }

    /// <summary>The context for the next block, with the wrap when the loop end falls inside it. Audio thread.</summary>
    internal AudioRenderContext Next(int frames, int sampleRate)
    {
        var wrapOffset = 0;
        if (_playing && _loopEnd > _loopStart && _frame < _loopEnd && _frame + frames > _loopEnd)
        {
            wrapOffset = (int)(_loopEnd - _frame);
        }

        var looping = _loopEnd > _loopStart;
        var context = new AudioRenderContext(frames, sampleRate, _playing, _frame, _jumped, _tempo, wrapOffset, wrapOffset > 0 ? _loopStart : 0, LoopStart: looping ? _loopStart : 0, LoopEnd: looping ? _loopEnd : 0);
        _jumped = false;
        return context;
    }

    /// <summary>Moves past a rendered block, wrapping at the loop end. Audio thread.</summary>
    internal void Advance(in AudioRenderContext context)
    {
        if (_playing)
        {
            var engaged = _loopEnd > _loopStart && _frame < _loopEnd;
            _frame = context.EndFrame;
            if (engaged && _frame >= _loopEnd)
            {
                // At the boundary, or a loop shorter than a block: back into the loop.
                var length = _loopEnd - _loopStart;
                _frame = _loopStart + ((_frame - _loopEnd) % length);
                _jumped = true;
            }
        }

        Volatile.Write(ref _position, _frame);
        Volatile.Write(ref _isPlaying, _playing ? 1 : 0);
    }

    // A position moved ahead by some frames, wrapping into the loop when it is engaged.
    private long Ahead(long frame, int frames)
    {
        var ahead = frame + frames;
        if (_loopEnd > _loopStart && frame < _loopEnd && ahead >= _loopEnd)
        {
            ahead = _loopStart + ((ahead - _loopEnd) % (_loopEnd - _loopStart));
        }

        return ahead;
    }

    private void ApplyRequest()
    {
        var request = Volatile.Read(ref _request);
        if (request.Version == _appliedVersion)
        {
            return;
        }

        _appliedVersion = request.Version;
        if (request.LocateVersion != _appliedLocate)
        {
            _appliedLocate = request.LocateVersion;
            _frame = request.LocateFrame;
            _jumped = true;
        }

        if (request.Playing != _playing)
        {
            _playing = request.Playing;
            _jumped = true;
        }

        _loopStart = request.LoopStart;
        _loopEnd = request.LoopEnd;
    }

    private void Change(Func<Request, Request> change)
    {
        lock (_gate)
        {
            var next = change(_request);
            Volatile.Write(ref _request, next with { Version = _request.Version + 1 });
        }
    }

    private sealed record Request(int Version, bool Playing, long LocateVersion, long LocateFrame, long LoopStart, long LoopEnd);
}
