using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Midi.Wire;

/// <summary>
/// The MIDI 1.0 encoding of Bluestone's semantic operations. Anything that needs more than one MIDI 1.0
/// message, or a MIDI-1-specific convention (7- and 14-bit values, bank select pairs), is decided
/// here rather than in the domain or profiles. <see cref="Midi1Decoder"/> is the inverse.
/// </summary>
public static class Midi1Encoder
{
    /// <summary>
    /// The messages for a channel event other than a paired note, in send order. Values are reduced
    /// to MIDI 1.0 resolution: 7 bits, or 14 bits for pitch bend.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="e"/> is a <see cref="NoteEvent"/>, whose start and release are sent at different times; use <see cref="NoteOn"/> and <see cref="NoteOff"/>.</exception>
    public static ImmutableArray<ChannelMessage> Encode(ChannelEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e switch
        {
            ControllerEvent c => [ChannelMessage.ControlChange(c.Channel, c.Controller, new SevenBitValue(c.Value.ToSevenBit()))],
            ProgramEvent p => ProgramSelection(p.Selection, p.Channel),
            PitchBendEvent b => [ChannelMessage.PitchBend(b.Channel, new FourteenBitValue(b.Value.ToFourteenBit()))],
            ChannelPressureEvent p => [ChannelMessage.ChannelPressure(p.Channel, new SevenBitValue(p.Pressure.ToSevenBit()))],
            PolyPressureEvent p => [ChannelMessage.PolyPressure(p.Channel, p.Note, new SevenBitValue(p.Pressure.ToSevenBit()))],
            NoteOffEvent n => [ChannelMessage.NoteOff(n.Channel, n.Note, n.ReleaseVelocity)],
            NoteEvent => throw new ArgumentException("A note's start and release are sent at different times; encode them with NoteOn and NoteOff.", nameof(e)),
            _ => throw new NotSupportedException($"Event type {e.GetType().Name} has no MIDI 1.0 encoding."),
        };
    }

    /// <summary>The note-on that starts <paramref name="note"/>.</summary>
    public static ChannelMessage NoteOn(NoteEvent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return ChannelMessage.NoteOn(note.Channel, note.Note, note.Velocity);
    }

    /// <summary>The note-off that releases <paramref name="note"/>, with its release velocity.</summary>
    public static ChannelMessage NoteOff(NoteEvent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return ChannelMessage.NoteOff(note.Channel, note.Note, note.ReleaseVelocity);
    }

    /// <summary>
    /// Bank select MSB (CC 0) and LSB (CC 32) when the selection names them, then the program change,
    /// in that order: a bank takes effect only at the next program change.
    /// </summary>
    public static ImmutableArray<ChannelMessage> ProgramSelection(ProgramSelection selection, MidiChannel channel)
    {
        var messages = ImmutableArray.CreateBuilder<ChannelMessage>(3);
        if (selection.BankMsb is { } msb)
        {
            messages.Add(ChannelMessage.ControlChange(channel, ControllerNumber.BankSelectMsb, msb));
        }

        if (selection.BankLsb is { } lsb)
        {
            messages.Add(ChannelMessage.ControlChange(channel, ControllerNumber.BankSelectLsb, lsb));
        }

        messages.Add(ChannelMessage.ProgramChange(channel, selection.Program));
        return messages.ToImmutable();
    }
}
