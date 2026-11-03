using System.Collections.Immutable;
using System.Runtime.InteropServices;
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

/// <summary>One dispatchable message in a plan. Blittable, so a plan image can hold an array of them (<see cref="PlanImage"/>).</summary>
internal readonly struct PlanEvent(long tick, int slot, ChannelMessage message, int payloadIndex, long durationTicks, Velocity releaseVelocity = default)
{
    public readonly long Tick = tick;
    public readonly int Slot = slot;

    /// <summary>The channel message, when <see cref="PayloadIndex"/> is negative.</summary>
    public readonly ChannelMessage Message = message;

    /// <summary>Index into <see cref="PlaybackPlan.Payload"/> for SysEx and other byte messages, or -1.</summary>
    public readonly int PayloadIndex = payloadIndex;

    /// <summary>For paired notes, the duration after which the engine releases the note; otherwise 0.</summary>
    public readonly long DurationTicks = durationTicks;

    /// <summary>For paired notes, the velocity of the release the engine sends.</summary>
    public readonly Velocity ReleaseVelocity = releaseVelocity;

    public bool IsNote => DurationTicks > 0;
}

/// <summary>Where one payload's bytes are in a plan's payload bytes.</summary>
internal readonly record struct PayloadRange(int Offset, int Length);

/// <summary>
/// An immutable, prepared snapshot of everything playback needs: messages in canonical dispatch order
/// (ADR 0003) with their output slots, plus the tempo map. Edits produce a new plan; the engine swaps
/// plans atomically, so the UI can never race the scheduler.
/// </summary>
/// <remarks>
/// The events and payload bytes are flat arrays of values with no references. A plan compiled in this process owns
/// them; a plan read from a <see cref="PlanImage"/> views them in the image's memory, so a large plan costs the
/// reader's garbage collector nothing (ADR 0044).
/// </remarks>
public sealed class PlaybackPlan
{
    private readonly PlanEvent[]? _events;
    private readonly ReadOnlyMemory<byte> _eventBytes;
    private readonly ReadOnlyMemory<byte> _payloadBytes;
    private readonly PayloadRange[]? _payloadRanges;
    private readonly ReadOnlyMemory<byte> _payloadRangeBytes;

    internal PlaybackPlan(TempoMap tempoMap, MeterMap meterMap, PlanEvent[] events, ByteBlock[] payloads, IReadOnlyList<PlanDiagnostic> diagnostics, ParameterChase[]? parameterChases = null)
    {
        TempoMap = tempoMap;
        MeterMap = meterMap;
        _events = events;
        var bytes = new byte[payloads.Sum(p => p.Length)];
        var ranges = new PayloadRange[payloads.Length];
        var offset = 0;
        for (var i = 0; i < payloads.Length; i++)
        {
            payloads[i].Span.CopyTo(bytes.AsSpan(offset));
            ranges[i] = new PayloadRange(offset, payloads[i].Length);
            offset += payloads[i].Length;
        }

        _payloadBytes = bytes;
        _payloadRanges = ranges;
        Diagnostics = diagnostics;
        ParameterChases = parameterChases ?? [];
    }

    /// <summary>A plan whose events and payloads are views of an image's memory (<see cref="PlanImage.Read"/>).</summary>
    internal PlaybackPlan(TempoMap tempoMap, MeterMap meterMap, ReadOnlyMemory<byte> eventBytes, ReadOnlyMemory<byte> payloadRangeBytes, ReadOnlyMemory<byte> payloadBytes, ParameterChase[] parameterChases)
    {
        TempoMap = tempoMap;
        MeterMap = meterMap;
        _eventBytes = eventBytes;
        _payloadRangeBytes = payloadRangeBytes;
        _payloadBytes = payloadBytes;
        Diagnostics = [];
        ParameterChases = parameterChases;
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

    /// <summary>What the compiler left out. A plan read from an image has none: they are for the UI, not the engine.</summary>
    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    /// <summary>The messages in dispatch order.</summary>
    internal ReadOnlySpan<PlanEvent> Events => _events ?? MemoryMarshal.Cast<byte, PlanEvent>(_eventBytes.Span);

    /// <summary>The number of SysEx and other byte payloads (see <see cref="PlanEvent.PayloadIndex"/>).</summary>
    internal int PayloadCount => PayloadRanges.Length;

    internal ReadOnlySpan<byte> PayloadBytes => _payloadBytes.Span;

    internal ReadOnlySpan<PayloadRange> PayloadRanges => _payloadRanges ?? MemoryMarshal.Cast<byte, PayloadRange>(_payloadRangeBytes.Span);

    /// <summary>The bytes of payload <paramref name="index"/>.</summary>
    internal ReadOnlySpan<byte> Payload(int index)
    {
        var range = PayloadRanges[index];
        return _payloadBytes.Span.Slice(range.Offset, range.Length);
    }

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
