using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Wire;
using Bluestone.Signal;

namespace Bluestone.Playback;

/// <summary>
/// One stream of events for one output: what the signal graph delivers to one external instrument part,
/// already processed by its device chains and channel mapping (ADR 0023).
/// </summary>
/// <param name="OutputSlot">Index into the outputs given to <see cref="PlaybackEngine.SetOutputs"/>.</param>
/// <param name="Events">The events in canonical order, keyed by origin track and index (see <see cref="SignalEvent"/>).</param>
public sealed record PlanPart(int OutputSlot, ImmutableArray<SignalEvent> Events)
{
    /// <summary>
    /// Channel events sent at tick 0 (whatever their own position), such as a voice selection. Give them
    /// negative sequence numbers so they precede the part's events in the same phase.
    /// </summary>
    public ImmutableArray<SignalEvent> InitialEvents { get; init; } = [];
}

/// <summary>The MIDI one hardware parameter change becomes, sent together (ADR 0031).</summary>
/// <param name="Position">When it is sent.</param>
/// <param name="Sequence">The ordering index of its first event within its origin; event <c>k</c> uses <c>Sequence + k</c>.</param>
/// <param name="Events">Controller and SysEx events, in the order they must be sent.</param>
public sealed record ParameterSend(Tick Position, int Sequence, ImmutableArray<TrackEvent> Events);

/// <summary>
/// Translated automation of one external instrument parameter (ADR 0031): what an instrument parameter lane
/// sends to one part. Its events are ordered with everything else by (tick, phase, origin, sequence, part).
/// </summary>
/// <param name="OutputSlot">Index into the outputs given to <see cref="PlaybackEngine.SetOutputs"/>.</param>
/// <param name="Origin">The index of the lane's track.</param>
/// <param name="Sends">The changes in position order.</param>
public sealed record ParameterPart(int OutputSlot, int Origin, ImmutableArray<ParameterSend> Sends)
{
    /// <summary>
    /// Whether the plan chases the parameter itself: on seek, play, and loop wrap, its latest change is sent
    /// whole (an RPN or NRPN sequence, or a SysEx message). Controller mappings leave this off, because
    /// controller chasing already covers them.
    /// </summary>
    public bool Chased { get; init; }
}

/// <summary>One change of a chased hardware parameter: when it was sent, which parameter, and what to resend.</summary>
internal sealed record ParameterChase(long Tick, int Key, ChaseMessage[] Messages);

/// <summary>Something the compiler left out of a plan, and why.</summary>
public sealed record PlanDiagnostic(TrackId Track, string Message);

/// <summary>One dispatchable message in a plan.</summary>
internal readonly struct PlanEvent(long tick, int slot, ChannelMessage message, int payloadIndex, long durationTicks, Velocity releaseVelocity = default)
{
    public readonly long Tick = tick;
    public readonly int Slot = slot;

    /// <summary>The channel message, when <see cref="PayloadIndex"/> is negative.</summary>
    public readonly ChannelMessage Message = message;

    /// <summary>Index into <see cref="PlaybackPlan.Payloads"/> for SysEx and other byte messages, or -1.</summary>
    public readonly int PayloadIndex = payloadIndex;

    /// <summary>For paired notes, the duration after which the engine releases the note; otherwise 0.</summary>
    public readonly long DurationTicks = durationTicks;

    /// <summary>For paired notes, the velocity of the release the engine sends.</summary>
    public readonly Velocity ReleaseVelocity = releaseVelocity;

    public bool IsNote => DurationTicks > 0;
}

/// <summary>
/// An immutable, prepared snapshot of everything playback needs: messages in canonical dispatch order
/// (ADR 0003) with their output slots, plus the tempo map. Edits produce a new plan; the engine swaps
/// plans atomically, so the UI can never race the scheduler.
/// </summary>
public sealed class PlaybackPlan
{
    internal PlaybackPlan(TempoMap tempoMap, MeterMap meterMap, PlanEvent[] events, ByteBlock[] payloads, IReadOnlyList<PlanDiagnostic> diagnostics, ParameterChase[]? parameterChases = null)
    {
        TempoMap = tempoMap;
        MeterMap = meterMap;
        Events = events;
        Payloads = payloads;
        Diagnostics = diagnostics;
        ParameterChases = parameterChases ?? [];
    }

    public static PlaybackPlan Empty(TempoMap tempoMap)
    {
        ArgumentNullException.ThrowIfNull(tempoMap);
        return new(tempoMap, MeterMap.Constant(tempoMap.Ppqn, TimeSignature.CommonTime), [], [], []);
    }

    public TempoMap TempoMap { get; }

    /// <summary>The meter map, used to place metronome clicks on beats.</summary>
    public MeterMap MeterMap { get; }

    public int EventCount => Events.Length;

    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    internal PlanEvent[] Events { get; }

    internal ByteBlock[] Payloads { get; }

    /// <summary>Changes of chased hardware parameters, in tick order (see <see cref="ParameterPart.Chased"/>).</summary>
    internal ParameterChase[] ParameterChases { get; }

    /// <summary>
    /// The first beat at or after <paramref name="tick"/>, and whether it is a bar's downbeat. A meter
    /// change that cuts a bar short starts a new bar, as in <see cref="MeterMap"/>.
    /// </summary>
    internal (long Tick, bool Downbeat) NextBeat(long tick)
    {
        var position = new Tick(Math.Max(0, tick));
        var bar = MeterMap.BarStart(position).Value;
        var beat = MeterMap.SignatureAt(position).TryGetTicksPerBeat(MeterMap.Ppqn, out var span) ? span.Value : MeterMap.Ppqn.TicksPerQuarterNote;
        var candidate = bar + (((position.Value - bar + beat - 1) / beat) * beat);
        var candidateBar = MeterMap.BarStart(new Tick(candidate)).Value;
        if (candidateBar > position.Value && candidateBar < candidate)
        {
            candidate = candidateBar;
        }

        return (candidate, candidate == candidateBar);
    }

    /// <summary>Index of the first event at or after <paramref name="tick"/>.</summary>
    internal int FirstIndexAtOrAfter(long tick)
    {
        int low = 0, high = Events.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Events[mid].Tick < tick)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
