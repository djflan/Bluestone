using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Wire;

namespace Bluestone.Tests.Unit.Midi.Wire;

public sealed class Midi1EncoderTests
{
    private static readonly MidiChannel Ten = MidiChannel.FromNumber(10);

    private static string Bytes(ChannelMessage m)
    {
        var buffer = new byte[3];
        return Convert.ToHexString(buffer, 0, m.CopyTo(buffer));
    }

    [Fact]
    public void NoteOnAndOff_EncodeAttackAndReleaseVelocity()
    {
        var note = new NoteEvent(EventId.New(), Tick.Zero, new TickSpan(10), Ten, NoteNumber.MiddleC, new Velocity(100), new Velocity(30));

        Assert.Equal("993C64", Bytes(Midi1Encoder.NoteOn(note)));
        Assert.Equal("893C1E", Bytes(Midi1Encoder.NoteOff(note)));
    }

    [Fact]
    public void ProgramSelection_SendsBankMsbThenLsbThenProgram()
    {
        var selection = new ProgramSelection(new ProgramNumber(17), new SevenBitValue(0), new SevenBitValue(64));

        Assert.Equal(["B90000", "B92040", "C911"], Midi1Encoder.ProgramSelection(selection, Ten).Select(Bytes));
    }

    [Fact]
    public void ProgramSelection_SendsOnlyTheBankPartsGiven()
    {
        Assert.Equal(["C900"], Midi1Encoder.ProgramSelection(new ProgramSelection(new ProgramNumber(0)), Ten).Select(Bytes));
        Assert.Equal(["B9007F", "C900"], Midi1Encoder.ProgramSelection(new ProgramSelection(new ProgramNumber(0), BankMsb: SevenBitValue.Max), Ten).Select(Bytes));
    }

    [Fact]
    public void Encode_ReducesValuesToMidiOneResolution()
    {
        var fine = new ControlValue(0x8123_4567);

        Assert.Equal(["B90740"], Midi1Encoder.Encode(new ControllerEvent(Tick.Zero, Ten, ControllerNumber.ChannelVolume, fine)).Select(Bytes));
        Assert.Equal(["E94840"], Midi1Encoder.Encode(new PitchBendEvent(Tick.Zero, Ten, fine)).Select(Bytes));
        Assert.Equal(["D940"], Midi1Encoder.Encode(new ChannelPressureEvent(Tick.Zero, Ten, fine)).Select(Bytes));
        Assert.Equal(["A93C40"], Midi1Encoder.Encode(new PolyPressureEvent(EventId.New(), Tick.Zero, Ten, NoteNumber.MiddleC, fine)).Select(Bytes));
    }

    [Fact]
    public void Encode_RefusesPairedNotes() =>
        Assert.Throws<ArgumentException>(() => Midi1Encoder.Encode(new NoteEvent(Tick.Zero, new TickSpan(1), Ten, NoteNumber.MiddleC, Velocity.Max)));
}
