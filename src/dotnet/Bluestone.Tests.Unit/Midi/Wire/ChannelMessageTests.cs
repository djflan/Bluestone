using Bluestone.Domain.Midi;
using Bluestone.Midi.Wire;
using CsCheck;

namespace Bluestone.Tests.Unit.Midi.Wire;

public sealed class ChannelMessageTests
{
    private static readonly MidiChannel Ten = MidiChannel.FromNumber(10);

    [Fact]
    public void Factories_EncodeStatusAndData()
    {
        AssertBytes(ChannelMessage.NoteOn(Ten, new NoteNumber(36), new Velocity(100)), 0x99, 36, 100);
        AssertBytes(ChannelMessage.NoteOff(Ten, new NoteNumber(36), new Velocity(64)), 0x89, 36, 64);
        AssertBytes(ChannelMessage.PolyPressure(Ten, new NoteNumber(36), new SevenBitValue(5)), 0xA9, 36, 5);
        AssertBytes(ChannelMessage.ControlChange(Ten, ControllerNumber.ChannelVolume, new SevenBitValue(90)), 0xB9, 7, 90);
        AssertBytes(ChannelMessage.ProgramChange(Ten, new ProgramNumber(25)), 0xC9, 25);
        AssertBytes(ChannelMessage.ChannelPressure(Ten, new SevenBitValue(70)), 0xD9, 70);
        AssertBytes(ChannelMessage.PitchBend(Ten, FourteenBitValue.Center), 0xE9, 0x00, 0x40);
    }

    [Fact]
    public void PitchBend_SendsLsbBeforeMsb()
    {
        var message = ChannelMessage.PitchBend(MidiChannel.FromIndex(0), new FourteenBitValue(0x1234));

        Assert.Equal(0x1234 & 0x7F, message.Data1);
        Assert.Equal(0x1234 >> 7, message.Data2);
        Assert.Equal(new FourteenBitValue(0x1234), message.PitchBendValue);
    }

    [Fact]
    public void NoteOnWithZeroVelocity_IsNoteOff()
    {
        Assert.True(ChannelMessage.TryCreate(0x90, 60, 0, out var message));

        Assert.True(message.IsNoteOff);
        Assert.False(message.IsNoteOn);
        Assert.Equal(ChannelMessageKind.NoteOn, message.Kind);
    }

    [Theory]
    [InlineData(0x7F, 0, 0)]
    [InlineData(0xF0, 0, 0)]
    [InlineData(0xFF, 0, 0)]
    [InlineData(0x90, 0x80, 0)]
    [InlineData(0x90, 0, 0x80)]
    [InlineData(0xC0, 0x80, 0)]
    public void TryCreate_RejectsMalformedBytes(int status, int data1, int data2) =>
        Assert.False(ChannelMessage.TryCreate((byte)status, (byte)data1, (byte)data2, out _));

    [Fact]
    public void TryCreate_IgnoresSecondDataByteForTwoByteMessages()
    {
        Assert.True(ChannelMessage.TryCreate(0xC3, 5, 0xFF, out var message));

        Assert.Equal(2, message.Length);
        Assert.Equal(0, message.Data2);
        Assert.Equal(ChannelMessage.ProgramChange(MidiChannel.FromIndex(3), new ProgramNumber(5)), message);
    }

    [Fact]
    public void TryCreate_RoundTripsEveryValidMessage() =>
        Gen.Select(Gen.Byte[0x80, 0xEF], Gen.Byte[0, 127], Gen.Byte[0, 127]).Sample((status, d1, d2) =>
        {
            if (!ChannelMessage.TryCreate(status, d1, d2, out var message))
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[3];
            var length = message.CopyTo(buffer);
            return ChannelMessage.TryCreate(buffer[0], buffer[1], length == 3 ? buffer[2] : (byte)0, out var parsed)
                && parsed == message
                && length == message.Length;
        });

    [Fact]
    public void CopyTo_RejectsShortDestination() =>
        Assert.Throws<ArgumentException>(() => ChannelMessage.NoteOn(Ten, NoteNumber.MiddleC, Velocity.Max).CopyTo(new byte[2]));

    private static void AssertBytes(ChannelMessage message, params int[] expected)
    {
        var buffer = new byte[3];
        var length = message.CopyTo(buffer);
        Assert.Equal(expected.Select(b => (byte)b), buffer[..length]);
    }
}
