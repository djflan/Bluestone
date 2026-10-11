using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Midi.Wire;

/// <summary>
/// Turns MIDI 1.0 channel messages, from files or live input, into Bluestone's channel events. The
/// inverse of <see cref="Midi1Encoder"/>: decoding then encoding gives back the same messages, except
/// that a note-on with velocity 0 comes back as the equivalent note-off.
/// </summary>
public static class Midi1Decoder
{
    /// <summary>
    /// The event for one message, or <see langword="null"/> for a sounding note-on, which the caller
    /// pairs with its release into a <see cref="NoteEvent"/>. A note-on with velocity 0 is a release.
    /// </summary>
    public static ChannelEvent? Decode(Tick position, ChannelMessage message)
    {
        var channel = message.Channel;
        return message.Kind switch
        {
            _ when message.IsNoteOn => null,
            ChannelMessageKind.NoteOn => new NoteOffEvent(EventId.New(), position, channel, message.Note, Velocity.Off),
            ChannelMessageKind.NoteOff => new NoteOffEvent(EventId.New(), position, channel, message.Note, message.Velocity),
            ChannelMessageKind.ControlChange => new ControllerEvent(position, channel, message.Controller, ControlValue.FromSevenBit(message.Data2)),
            ChannelMessageKind.ProgramChange => new ProgramEvent(position, channel, new ProgramSelection(message.Program)),
            ChannelMessageKind.PitchBend => new PitchBendEvent(position, channel, ControlValue.FromFourteenBit(message.PitchBendValue.Value)),
            ChannelMessageKind.ChannelPressure => new ChannelPressureEvent(position, channel, ControlValue.FromSevenBit(message.Data1)),
            _ => new PolyPressureEvent(EventId.New(), position, channel, message.Note, ControlValue.FromSevenBit(message.Data2)),
        };
    }

    /// <summary>
    /// Folds bank select MSB (CC 0) and LSB (CC 32) into the program change they belong to: one
    /// on the same channel at the same tick, with the program event keeping its identity. Bank
    /// selects anywhere else stay controller events, so the MIDI 1.0 output is unchanged.
    /// </summary>
    /// <returns>The events in canonical order (position, then phase, then supplied order).</returns>
    public static IReadOnlyList<TrackEvent> CombineProgramSelections(IEnumerable<TrackEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.OrderBy(e => e.Position).ThenBy(e => e.Phase).ToList();
        var merged = new TrackEvent?[ordered.Count];
        var msb = new Dictionary<MidiChannel, int>();
        var lsb = new Dictionary<MidiChannel, int>();
        var tick = Tick.Zero;

        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            merged[i] = e;
            if (e.Position != tick)
            {
                tick = e.Position;
                msb.Clear();
                lsb.Clear();
            }

            switch (e)
            {
                case ControllerEvent c when c.Controller == ControllerNumber.BankSelectMsb:
                    msb[c.Channel] = i;
                    break;
                case ControllerEvent c when c.Controller == ControllerNumber.BankSelectLsb:
                    lsb[c.Channel] = i;
                    break;
                case ProgramEvent { Selection: { BankMsb: null, BankLsb: null } selection } program:
                    var bankMsb = Take(msb, program.Channel);
                    var bankLsb = Take(lsb, program.Channel);
                    if (bankMsb is not null || bankLsb is not null)
                    {
                        merged[i] = program with { Selection = selection with { BankMsb = bankMsb, BankLsb = bankLsb } };
                    }

                    break;
            }
        }

        return [.. merged.OfType<TrackEvent>()];

        SevenBitValue? Take(Dictionary<MidiChannel, int> pending, MidiChannel channel)
        {
            if (!pending.Remove(channel, out var index))
            {
                return null;
            }

            var value = ((ControllerEvent)ordered[index]).Value.ToSevenBit();
            merged[index] = null;
            return new SevenBitValue(value);
        }
    }
}
