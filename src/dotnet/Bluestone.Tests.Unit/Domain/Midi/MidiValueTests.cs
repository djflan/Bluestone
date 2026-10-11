using Bluestone.Domain.Midi;
using CsCheck;

namespace Bluestone.Tests.Unit.Domain.Midi;

public sealed class MidiValueTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(128)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void SevenBitTypes_RejectOutOfRange(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NoteNumber(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Velocity(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SevenBitValue(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControllerNumber(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgramNumber(value));
    }

    [Fact]
    public void SevenBitTypes_AcceptFullRange() =>
        Gen.Int[0, 127].Sample(value =>
            new NoteNumber(value).Value == value
            && new Velocity(value).Value == value
            && new SevenBitValue(value).Value == value
            && new ControllerNumber(value).Value == value
            && new ProgramNumber(value).Value == value);

    [Fact]
    public void MidiChannel_MapsBetweenIndexAndNumber()
    {
        Assert.Equal(0, MidiChannel.FromNumber(1).Index);
        Assert.Equal(16, MidiChannel.FromIndex(15).Number);
        Assert.Equal(MidiChannel.FromIndex(9), MidiChannel.FromNumber(10));
        Assert.Equal("10", MidiChannel.FromIndex(9).ToString());
        Assert.Equal(16, MidiChannel.All.Count());
        Assert.Equal(default, MidiChannel.FromNumber(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void MidiChannel_FromNumber_RejectsOutOfRange(int number) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiChannel.FromNumber(number));

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void MidiChannel_FromIndex_RejectsOutOfRange(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiChannel.FromIndex(index));

    [Fact]
    public void ProgramNumber_DisplaysOneBased()
    {
        Assert.Equal(1, new ProgramNumber(0).Number);
        Assert.Equal(new ProgramNumber(127), ProgramNumber.FromNumber(128));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgramNumber.FromNumber(0));
    }

    [Fact]
    public void NoteNumber_TryTranspose_StaysInRange()
    {
        Assert.True(NoteNumber.MiddleC.TryTranspose(12, out var up));
        Assert.Equal(new NoteNumber(72), up);
        Assert.False(new NoteNumber(120).TryTranspose(8, out _));
        Assert.False(new NoteNumber(3).TryTranspose(-4, out _));
        Assert.True(new NoteNumber(3).TryTranspose(-3, out var bottom));
        Assert.Equal(new NoteNumber(0), bottom);
    }

    [Fact]
    public void Velocity_ZeroIsNotAudible()
    {
        Assert.False(Velocity.Off.IsAudible);
        Assert.True(new Velocity(1).IsAudible);
    }

    [Fact]
    public void FourteenBitValue_SplitsAndJoinsBytes() =>
        Gen.Int[0, FourteenBitValue.MaxValue].Sample(value =>
        {
            var v = new FourteenBitValue(value);
            return FourteenBitValue.FromBytes(v.Msb, v.Lsb) == v && v.Msb <= 127 && v.Lsb <= 127;
        });

    [Fact]
    public void FourteenBitValue_OffsetFromCenter_IsRelativeToCenter()
    {
        Assert.Equal(0, FourteenBitValue.Center.OffsetFromCenter);
        Assert.Equal(new FourteenBitValue(0), FourteenBitValue.FromOffsetFromCenter(-8192));
        Assert.Equal(new FourteenBitValue(16383), FourteenBitValue.FromOffsetFromCenter(8191));
        Assert.Equal(0x40, FourteenBitValue.Center.Msb);
        Assert.Equal(0x00, FourteenBitValue.Center.Lsb);
        Assert.Throws<ArgumentOutOfRangeException>(() => FourteenBitValue.FromOffsetFromCenter(8192));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FourteenBitValue(16384));
        Assert.Throws<ArgumentOutOfRangeException>(() => FourteenBitValue.FromBytes(128, 0));
    }

    [Fact]
    public void ControllerNumber_ClassifiesSpecialControllers()
    {
        Assert.True(ControllerNumber.BankSelectMsb.IsBankSelect);
        Assert.True(ControllerNumber.BankSelectLsb.IsBankSelect);
        Assert.False(ControllerNumber.ModulationWheel.IsBankSelect);
        Assert.True(ControllerNumber.AllNotesOff.IsChannelMode);
        Assert.False(ControllerNumber.SustainPedal.IsChannelMode);
        Assert.True(ControllerNumber.RpnMsb.IsParameterNumberSelector);
        Assert.True(ControllerNumber.NrpnLsb.IsParameterNumberSelector);
        Assert.False(ControllerNumber.DataEntryMsb.IsParameterNumberSelector);
    }
}
