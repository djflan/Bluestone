using System.Diagnostics.CodeAnalysis;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;

namespace Bluestone.Playback;

/// <summary>
/// What the rest of Bluestone uses of MIDI playback: the transport, its plan and outputs, and the mapping from clock time
/// to song position. <see cref="PlaybackEngine"/> is the in-process implementation; with the engine process (ADR 0044)
/// a client of that process stands in for it and mirrors the transport it reports.
/// </summary>
public interface IPlaybackTransport
{
    /// <summary>The monotonic clock that times are given in.</summary>
    IMonotonicClock Clock { get; }

    TransportState State { get; }

    /// <summary>The playhead as last known, or the last requested position.</summary>
    Tick Position { get; }

    /// <summary>How many times playback has wrapped from the loop's end to its start, ever.</summary>
    long LoopWraps { get; }

    /// <summary>True while a count-in requested by <see cref="Play"/> is still running.</summary>
    bool IsCountingIn { get; }

    /// <summary>The dispatch timing figures so far.</summary>
    TimingSnapshot SnapshotStatistics();

    /// <summary>The song position playing at a clock time (see <see cref="PlaybackEngine.TryGetTickAt"/>). Any thread.</summary>
    bool TryGetTickAt(TimeSpan time, out Tick tick);

    /// <summary>The song time heard at a clock time (see <see cref="PlaybackEngine.TryGetSongTimeAt"/>). Any thread.</summary>
    bool TryGetSongTimeAt(TimeSpan time, out SongPosition position);

    void Load(PlaybackPlan plan);

    void SetOutputs(IReadOnlyList<IMidiOutput?> outputs);

    void Play(Tick? from = null, CountIn? countIn = null);

    [SuppressMessage("Naming", "CA1716", Justification = "PlaybackEngine's name; Bluestone is C# only.")]
    void Stop();

    void Seek(Tick position);

    [SuppressMessage("Naming", "CA1716", Justification = "PlaybackEngine's name; Bluestone is C# only.")]
    void SetLoop(LoopRegion? loop);

    void SetMetronome(MetronomeClick? click, bool enabled);

    void Panic();

    /// <summary>Sends a channel message to <paramref name="output"/> as soon as possible (thru, auditioning). Any thread.</summary>
    void SendNow(IMidiOutput output, ChannelMessage message);

    void Nudge(TimeSpan amount);
}
