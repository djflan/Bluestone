using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;

namespace Bluestone.Playback;

/// <summary>
/// How the engine sounds the metronome: a short note on every beat, sent to one output slot.
/// Downbeats use <see cref="AccentNote"/>; other beats use <see cref="BeatNote"/>.
/// </summary>
/// <remarks>The defaults are General MIDI percussion on channel 10: high and low wood block.</remarks>
public sealed record MetronomeClick(int Slot)
{
    public MidiChannel Channel { get; init; } = MidiChannel.FromNumber(10);

    public NoteNumber AccentNote { get; init; } = new(76);

    public Velocity AccentVelocity { get; init; } = new(110);

    public NoteNumber BeatNote { get; init; } = new(77);

    public Velocity BeatVelocity { get; init; } = new(80);

    /// <summary>How long each click sounds before its release.</summary>
    public TimeSpan Length { get; init; } = TimeSpan.FromMilliseconds(40);
}

/// <summary>A count-in click, relative to the moment playback was requested.</summary>
public readonly record struct CountInClick(TimeSpan Offset, bool Downbeat);

/// <summary>
/// A pre-roll before playback starts: the transport waits <see cref="Duration"/>, sounding
/// <see cref="Clicks"/> on the metronome, then plays from the requested position.
/// </summary>
public sealed record CountIn(TimeSpan Duration, ImmutableArray<CountInClick> Clicks)
{
    /// <summary>
    /// Counts in <paramref name="bars"/> bars of the meter and tempo in effect at <paramref name="start"/>.
    /// </summary>
    public static CountIn Bars(TempoMap tempo, MeterMap meter, Tick start, int bars)
    {
        ArgumentNullException.ThrowIfNull(tempo);
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentOutOfRangeException.ThrowIfNegative(bars);
        var signature = meter.SignatureAt(start);
        var beatTicks = signature.TryGetTicksPerBeat(meter.Ppqn, out var span) ? span.Value : meter.Ppqn.TicksPerQuarterNote;
        var beat = TimeSpan.FromTicks(tempo.TempoAt(start).MicrosecondsPerQuarterNote * 10L * beatTicks / meter.Ppqn.TicksPerQuarterNote);
        var clicks = ImmutableArray.CreateBuilder<CountInClick>(bars * signature.Numerator);
        for (var i = 0; i < bars * signature.Numerator; i++)
        {
            clicks.Add(new CountInClick(beat * i, i % signature.Numerator == 0));
        }

        return new CountIn(beat * clicks.Count, clicks.MoveToImmutable());
    }
}
