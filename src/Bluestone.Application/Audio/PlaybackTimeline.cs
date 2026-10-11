using System.Diagnostics;
using Bluestone.Audio;
using Bluestone.Midi.Timing;
using Bluestone.Playback;

namespace Bluestone.Application.Audio;

/// <summary>
/// The MIDI playback engine's transport as the audio engine's timeline (ADR 0032): the song time heard at a moment,
/// read lock-free from the engine's published anchors. Stopwatch timestamps are converted to the engine's clock, which
/// must count from the same origin as <see cref="SystemMonotonicClock"/> (it does, and so does a test's virtual clock
/// that is advanced alongside a virtual audio driver).
/// </summary>
public sealed class PlaybackTimeline(PlaybackEngine engine) : IAudioTimeline
{
    private readonly PlaybackEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public bool TryGetPosition(long timestamp, out TimelinePosition position)
    {
        if (!_engine.TryGetSongTimeAt(Stopwatch.GetElapsedTime(0, timestamp), out var song))
        {
            position = default;
            return false;
        }

        position = new TimelinePosition(song.SongTime.TotalSeconds, song.LoopStart.TotalSeconds, song.LoopEnd.TotalSeconds, song.BeatsPerMinute);
        return true;
    }
}
