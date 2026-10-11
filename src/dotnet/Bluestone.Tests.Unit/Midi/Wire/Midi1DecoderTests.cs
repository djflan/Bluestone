using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Wire;

namespace Bluestone.Tests.Unit.Midi.Wire;

public sealed class Midi1DecoderTests
{
    private static readonly MidiChannel One = MidiChannel.FromIndex(0);
    private static readonly MidiChannel Two = MidiChannel.FromIndex(1);

    private static ChannelMessage Message(byte status, byte data1, byte data2 = 0)
    {
        Assert.True(ChannelMessage.TryCreate(status, data1, data2, out var message));
        return message;
    }

    private static ControllerEvent Cc(long at, ControllerNumber controller, int value, MidiChannel? channel = null) =>
        new(new Tick(at), channel ?? One, controller, ControlValue.FromSevenBit(value));

    private static ProgramEvent Program(long at, int program, MidiChannel? channel = null) =>
        new(new Tick(at), channel ?? One, new ProgramSelection(new ProgramNumber(program)));

    [Theory]
    [InlineData(0x80, 0x3C, 0x40)]
    [InlineData(0xB3, 0x07, 0x64)]
    [InlineData(0xC3, 0x11, 0x00)]
    [InlineData(0xE3, 0x00, 0x40)]
    [InlineData(0xE3, 0x7F, 0x7F)]
    [InlineData(0xD3, 0x30, 0x00)]
    [InlineData(0xA3, 0x3C, 0x20)]
    public void DecodeThenEncode_GivesBackTheMessage(byte status, byte data1, byte data2)
    {
        var message = Message(status, data1, data2);

        var decoded = Midi1Decoder.Decode(Tick.Zero, message)!;

        Assert.Equal(message, Assert.Single(Midi1Encoder.Encode(decoded)));
    }

    [Fact]
    public void Decode_LeavesSoundingNotesToBePairedAndReadsVelocityZeroAsARelease()
    {
        Assert.Null(Midi1Decoder.Decode(Tick.Zero, Message(0x90, 0x3C, 0x64)));

        var release = Assert.IsType<NoteOffEvent>(Midi1Decoder.Decode(Tick.Zero, Message(0x90, 0x3C, 0x00)));

        Assert.Equal((NoteNumber.MiddleC, Velocity.Off), (release.Note, release.ReleaseVelocity));
    }

    [Fact]
    public void CombineProgramSelections_FoldsSameTickBankSelectIntoTheProgram()
    {
        var program = Program(10, 17);

        var result = Midi1Decoder.CombineProgramSelections([Cc(10, ControllerNumber.BankSelectMsb, 0), Cc(10, ControllerNumber.BankSelectLsb, 64), program]);

        var merged = Assert.IsType<ProgramEvent>(Assert.Single(result));
        Assert.Equal(program.Id, merged.Id);
        Assert.Equal(new ProgramSelection(new ProgramNumber(17), new SevenBitValue(0), new SevenBitValue(64)), merged.Selection);
    }

    [Fact]
    public void CombineProgramSelections_TakesOnlyTheBankPartsPresent()
    {
        var result = Midi1Decoder.CombineProgramSelections([Program(0, 1), Cc(0, ControllerNumber.BankSelectLsb, 3)]);

        Assert.Equal(new ProgramSelection(new ProgramNumber(1), null, new SevenBitValue(3)), Assert.IsType<ProgramEvent>(Assert.Single(result)).Selection);
    }

    [Fact]
    public void CombineProgramSelections_LeavesBankSelectsElsewhereAsControllers()
    {
        TrackEvent[] events =
        [
            Cc(0, ControllerNumber.BankSelectMsb, 1),
            Cc(5, ControllerNumber.BankSelectMsb, 3, Two),
            Program(5, 2),
            Cc(9, ControllerNumber.BankSelectLsb, 4),
        ];

        var result = Midi1Decoder.CombineProgramSelections(events);

        Assert.Equal(events.Select(e => e.Id), result.Select(e => e.Id));
        Assert.Equal(new ProgramSelection(new ProgramNumber(2)), Assert.Single(result.OfType<ProgramEvent>()).Selection);
    }

    [Fact]
    public void CombineProgramSelections_UsesTheLastBankSelectAndKeepsEarlierOnes()
    {
        var first = Cc(0, ControllerNumber.BankSelectMsb, 1);

        var result = Midi1Decoder.CombineProgramSelections([first, Cc(0, ControllerNumber.BankSelectMsb, 2), Program(0, 0)]);

        Assert.Equal(first.Id, Assert.Single(result.OfType<ControllerEvent>()).Id);
        Assert.Equal(new SevenBitValue(2), Assert.Single(result.OfType<ProgramEvent>()).Selection.BankMsb);
    }

    [Fact]
    public void CombineProgramSelections_NeverAddsBanksToAProgramThatHasOne()
    {
        var withBank = new ProgramEvent(Tick.Zero, One, new ProgramSelection(new ProgramNumber(0), BankMsb: SevenBitValue.Max));

        var result = Midi1Decoder.CombineProgramSelections([Cc(0, ControllerNumber.BankSelectMsb, 1), withBank]);

        Assert.Equal(2, result.Count);
        Assert.Equal(withBank, result[1]);
    }
}
