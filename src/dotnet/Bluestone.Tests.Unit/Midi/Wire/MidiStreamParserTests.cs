using Bluestone.Midi.Wire;
using CsCheck;

namespace Bluestone.Tests.Unit.Midi.Wire;

public sealed class MidiStreamParserTests
{
    private static List<string> Parse(MidiStreamParser parser, params byte[][] chunks)
    {
        var messages = new List<string>();
        foreach (var chunk in chunks)
        {
            parser.Feed(chunk, m => messages.Add(Convert.ToHexString(m)));
        }

        return messages;
    }

    private static List<string> Parse(params byte[][] chunks) => Parse(new MidiStreamParser(), chunks);

    [Fact]
    public void CompleteMessages_PassThrough() =>
        Assert.Equal(["903C64", "C005", "F8"], Parse([0x90, 0x3C, 0x64, 0xC0, 0x05, 0xF8]));

    [Fact]
    public void RunningStatus_IsExpanded() =>
        Assert.Equal(["903C64", "903E00", "904050"], Parse([0x90, 0x3C, 0x64, 0x3E, 0x00, 0x40, 0x50]));

    [Fact]
    public void MessagesSplitAcrossChunks_AreReassembled() =>
        Assert.Equal(["B00764", "F0431000F7"], Parse([0xB0], [0x07], [0x64, 0xF0, 0x43], [0x10, 0x00], [0xF7]));

    [Fact]
    public void RealtimeInsideMessages_IsDeliveredImmediately() =>
        Assert.Equal(["F8", "903C64", "F8", "F0437EF7"], Parse([0x90, 0xF8, 0x3C, 0x64, 0xF0, 0x43, 0xF8, 0x7E, 0xF7]));

    [Fact]
    public void SystemCommon_IsParsedAndCancelsRunningStatus()
    {
        var parser = new MidiStreamParser();

        Assert.Equal(["903C64", "F20010", "F6"], Parse(parser, [0x90, 0x3C, 0x64, 0xF2, 0x00, 0x10, 0xF6, 0x3E, 0x00]));
        Assert.Equal(2, parser.DroppedMessages);
    }

    [Fact]
    public void InterruptedSysEx_IsDropped()
    {
        var parser = new MidiStreamParser();

        Assert.Equal(["903C64"], Parse(parser, [0xF0, 0x43, 0x10, 0x90, 0x3C, 0x64]));
        Assert.Equal(1, parser.DroppedMessages);
    }

    [Fact]
    public void OversizedSysEx_IsDroppedButParsingRecovers()
    {
        var parser = new MidiStreamParser(maxSysExLength: 4);

        Assert.Equal(["C001"], Parse(parser, [0xF0, 1, 2, 3, 4, 5, 0xF7, 0xC0, 0x01]));
        Assert.Equal(1, parser.DroppedMessages);
    }

    [Fact]
    public void UndefinedAndStrayBytes_AreDropped()
    {
        var parser = new MidiStreamParser();

        Assert.Equal(["F8"], Parse(parser, [0x40, 0xF4, 0xF9, 0xF7, 0xF8]));
        Assert.Equal(3, parser.DroppedMessages);
    }

    [Fact]
    public void AnyInput_YieldsOnlyValidMessages() =>
        Gen.Byte.Array[0, 200].Sample(bytes =>
        {
            var ok = true;
            new MidiStreamParser(64).Feed(bytes, m => ok &= MidiWire.Classify(m) != MidiMessageClass.Invalid);
            return ok;
        }, iter: 5000);
}
