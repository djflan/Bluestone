using Bluestone.Midi.Wire;

namespace Bluestone.Tests.Unit.Midi.Wire;

public sealed class MidiWireTests
{
    [Theory]
    [InlineData(MidiMessageClass.Channel, 0x90, 0x3C, 0x64)]
    [InlineData(MidiMessageClass.Channel, 0xC5, 0x10)]
    [InlineData(MidiMessageClass.Channel, 0xD0, 0x7F)]
    [InlineData(MidiMessageClass.SystemExclusive, 0xF0, 0x43, 0x10, 0xF7)]
    [InlineData(MidiMessageClass.SystemExclusive, 0xF0, 0xF7)]
    [InlineData(MidiMessageClass.SystemCommon, 0xF1, 0x20)]
    [InlineData(MidiMessageClass.SystemCommon, 0xF2, 0x00, 0x10)]
    [InlineData(MidiMessageClass.SystemCommon, 0xF3, 0x01)]
    [InlineData(MidiMessageClass.SystemCommon, 0xF6)]
    [InlineData(MidiMessageClass.SystemRealtime, 0xF8)]
    [InlineData(MidiMessageClass.SystemRealtime, 0xFA)]
    [InlineData(MidiMessageClass.SystemRealtime, 0xFC)]
    [InlineData(MidiMessageClass.SystemRealtime, 0xFF)]
    public void Classify_AcceptsCompleteMessages(MidiMessageClass expected, params int[] bytes) =>
        Assert.Equal(expected, MidiWire.Classify(bytes.Select(b => (byte)b).ToArray()));

    [Theory]
    [InlineData]
    [InlineData(0x3C, 0x64)]
    [InlineData(0x90, 0x3C)]
    [InlineData(0x90, 0x3C, 0x64, 0x00)]
    [InlineData(0x90, 0x3C, 0x80)]
    [InlineData(0xC5, 0x10, 0x00)]
    [InlineData(0xF0, 0x43, 0x10)]
    [InlineData(0xF0, 0x43, 0x90, 0xF7)]
    [InlineData(0xF7)]
    [InlineData(0xF4)]
    [InlineData(0xF9)]
    [InlineData(0xF8, 0x00)]
    [InlineData(0xF2, 0x00)]
    public void Classify_RejectsAnythingElse(params int[] bytes) =>
        Assert.Equal(MidiMessageClass.Invalid, MidiWire.Classify(bytes.Select(b => (byte)b).ToArray()));
}
