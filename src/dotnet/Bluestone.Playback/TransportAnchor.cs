using Bluestone.Domain.Time;

namespace Bluestone.Playback;

/// <summary>
/// A published tick-to-time mapping of the transport: <see cref="SongTime"/> plays at clock time <see cref="Time"/>, at
/// <see cref="Tempo"/>. <see cref="LoopStart"/> and <see cref="LoopEnd"/> are the loop playback will wrap at, or both zero.
/// </summary>
public readonly record struct TransportAnchor(bool Playing, TimeSpan Time, TimeSpan SongTime, TempoMap? Tempo, long LoopStart, long LoopEnd)
{
    /// <summary>
    /// The song position playing at <paramref name="time"/>, following loops; an input just before the current anchor
    /// (a loop wrap or seek) belongs to <paramref name="previous"/>. Allocation-free.
    /// </summary>
    /// <returns>False when the transport is stopped, or the time maps before the start of the song.</returns>
    public static bool TryGetTickAt(in TransportAnchor current, in TransportAnchor previous, TimeSpan time, out Tick tick)
    {
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

    /// <summary>The song time heard at <paramref name="time"/> from <paramref name="anchor"/>, with loop wraps applied. Allocation-free.</summary>
    /// <returns>False when the transport is stopped.</returns>
    public static bool TryGetSongTimeAt(in TransportAnchor anchor, TimeSpan time, out SongPosition position)
    {
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
}

/// <summary>
/// The transport as the playback thread last published it, read consistently: what the engine process reports to
/// Bluestone (ADR 0044). <see cref="Version"/> changes whenever the anchors do.
/// </summary>
public readonly record struct TransportSnapshot(
    TransportState State,
    Tick Position,
    long LoopWraps,
    TimeSpan CountInUntil,
    TransportAnchor Current,
    TransportAnchor Previous,
    int Version);
