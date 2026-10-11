using Bluestone.Domain.Midi;
using Bluestone.Midi.SysEx;
using CsCheck;

namespace Bluestone.Tests.Unit.Midi.SysEx;

public sealed class SysExInterpreterTests
{
    private static SysExInterpretation? Interpret(params byte[] bytes) => SysExInterpreter.Interpret(SysExMessage.Create(bytes));

    [Theory]
    [InlineData(new byte[] { 0xF0, 0x7D, 0x01, 0x02, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x00, 0x21, 0x09, 0x01, 0x02, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x43, 0x10, 0x49, 0x00, 0x00, 0x12, 0x01, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x43, 0x30, 0x4C, 0x08, 0x00, 0x07, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x00, 0x00, 0x41, 0x2F, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x11, 0x40, 0x00, 0x7F, 0x00, 0x00, 0x01, 0x40, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x7E, 0x7F, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0xF7 })]
    public void Interpret_LeavesUnknownMessagesOpaque(byte[] bytes) => Assert.Null(Interpret(bytes));

    [Theory]
    [InlineData(new byte[] { 0xF0, 0x43, 0x10 })]
    [InlineData(new byte[] { 0xF0, 0x43, 0x90, 0x4C, 0xF7 })]
    [InlineData(new byte[] { 0x90, 0x3C, 0x64 })]
    [InlineData(new byte[] { })]
    public void Interpret_IgnoresAnythingButOneCompleteMessage(byte[] bytes) => Assert.Null(SysExInterpreter.Interpret(bytes));

    [Theory]
    [InlineData(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 }, UniversalMessageKind.GeneralMidiOn, "GM System On")]
    [InlineData(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x02, 0xF7 }, UniversalMessageKind.GeneralMidiOff, "GM System Off")]
    [InlineData(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x03, 0xF7 }, UniversalMessageKind.GeneralMidi2On, "GM2 System On")]
    [InlineData(new byte[] { 0xF0, 0x7E, 0x10, 0x06, 0x01, 0xF7 }, UniversalMessageKind.IdentityRequest, "Identity Request")]
    [InlineData(new byte[] { 0xF0, 0x7E, 0x7F, 0x7C, 0x00, 0xF7 }, UniversalMessageKind.Other, "Universal Non-Real Time 7C 00")]
    [InlineData(new byte[] { 0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x7F, 0xF7 }, UniversalMessageKind.Other, "Universal Real Time 04 01")]
    public void Interpret_RecognizesUniversalMessages(byte[] bytes, UniversalMessageKind kind, string summary)
    {
        var message = Assert.IsType<UniversalMessage>(Interpret(bytes));

        Assert.Equal(kind, message.Kind);
        Assert.Equal(summary, message.Summary);
    }

    [Fact]
    public void Interpret_ReadsMasterVolumeLsbFirst()
    {
        var message = Assert.IsType<UniversalMessage>(Interpret(0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x00, 0x7F, 0xF7));

        Assert.Equal(UniversalMessageKind.MasterVolume, message.Kind);
        Assert.True(message.IsRealtime);
        Assert.Equal(0x7F << 7, message.Value!.Value.Value);
    }

    [Fact]
    public void Interpret_ReadsIdentityReply()
    {
        var message = Assert.IsType<UniversalMessage>(Interpret(0xF0, 0x7E, 0x10, 0x06, 0x02, 0x43, 0x00, 0x41, 0x34, 0x06, 0x00, 0x00, 0x00, 0x01, 0xF7));

        Assert.Equal(UniversalMessageKind.IdentityReply, message.Kind);
        Assert.Equal([0x43], message.Identity!.ManufacturerId);
        Assert.Equal(0x41 << 7, message.Identity.Family);
        Assert.Equal(0x34 | (0x06 << 7), message.Identity.Member);
        Assert.Equal([0x00, 0x00, 0x00, 0x01], message.Identity.Version);
        Assert.Equal("Identity Reply · manufacturer 43", message.Summary);
    }

    [Fact]
    public void Interpret_RecognizesXgSystemOnAndReset()
    {
        var on = Assert.IsType<XgParameterChange>(Interpret(0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7));
        var reset = Assert.IsType<XgParameterChange>(Interpret(0xF0, 0x43, 0x13, 0x4C, 0x00, 0x00, 0x7F, 0x00, 0xF7));

        Assert.True(on.IsSystemOn);
        Assert.Equal("XG System On", on.Summary);
        Assert.True(reset.IsAllParameterReset);
        Assert.Equal(3, reset.DeviceNumber);
        Assert.Equal(XgArea.System, reset.Address.Area);
    }

    [Theory]
    [InlineData(0x08, 0x01, 0x0B, XgArea.MultiPart, "XG Multi Part 2 parameter 0B")]
    [InlineData(0x02, 0x01, 0x00, XgArea.Reverb, "XG Reverb parameter 00")]
    [InlineData(0x02, 0x01, 0x20, XgArea.Chorus, "XG Chorus parameter 20")]
    [InlineData(0x02, 0x01, 0x40, XgArea.Variation, "XG Variation parameter 40")]
    [InlineData(0x03, 0x00, 0x00, XgArea.Insertion, "XG Insertion 1 parameter 00")]
    [InlineData(0x31, 0x24, 0x0B, XgArea.DrumSetup, "XG Drum Setup 2 note 36 parameter 0B")]
    [InlineData(0x00, 0x00, 0x04, XgArea.System, "XG System parameter 04")]
    [InlineData(0x0A, 0x00, 0x01, XgArea.Other, "XG parameter 0A 00 01")]
    public void Interpret_ClassifiesXgParameterChanges(byte high, byte mid, byte low, XgArea area, string summary)
    {
        var change = Assert.IsType<XgParameterChange>(Interpret(0xF0, 0x43, 0x10, 0x4C, high, mid, low, 0x40, 0xF7));

        Assert.Equal(area, change.Address.Area);
        Assert.Equal([0x40], change.Data);
        Assert.Equal(summary, change.Summary);
        Assert.Equal(area == XgArea.MultiPart ? mid + 1 : null, change.Part);
    }

    [Fact]
    public void Interpret_ChecksXgBulkDumpChecksum()
    {
        // Byte count 00 02, address 08 00 00, data 01 02; checksum makes count..checksum sum to 0 mod 128.
        byte[] body = [0x00, 0x02, 0x08, 0x00, 0x00, 0x01, 0x02];
        var checksum = GsDialect.Checksum(body);

        var good = Assert.IsType<XgBulkDump>(Interpret([0xF0, 0x43, 0x00, 0x4C, .. body, checksum, 0xF7]));
        var bad = Assert.IsType<XgBulkDump>(Interpret([0xF0, 0x43, 0x00, 0x4C, .. body, (byte)((checksum + 1) & 0x7F), 0xF7]));

        Assert.True(good.ChecksumValid);
        Assert.Equal([0x01, 0x02], good.Data);
        Assert.Equal(XgArea.MultiPart, good.Address.Area);
        Assert.Equal("XG Bulk Dump · Multi Part 1 parameter 00 · 2 bytes", good.Summary);
        Assert.False(bad.ChecksumValid);
        Assert.EndsWith("bad checksum", bad.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpret_LeavesXgBulkDumpWithWrongByteCountOpaque() =>
        Assert.Null(Interpret(0xF0, 0x43, 0x00, 0x4C, 0x00, 0x05, 0x08, 0x00, 0x00, 0x01, 0x02, 0x00, 0xF7));

    [Fact]
    public void Interpret_RecognizesGsResetAndChecksMalformedChecksums()
    {
        var reset = Assert.IsType<GsDataSet>(Interpret(0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7));
        var damaged = Assert.IsType<GsDataSet>(Interpret(0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x42, 0xF7));

        Assert.True(reset.IsGsReset);
        Assert.True(reset.ChecksumValid);
        Assert.Equal("GS Reset", reset.Summary);
        Assert.True(damaged.IsGsReset);
        Assert.False(damaged.ChecksumValid);
        Assert.Equal("GS Reset · bad checksum", damaged.Summary);
    }

    [Theory]
    [InlineData(0x10, 10)]
    [InlineData(0x11, 1)]
    [InlineData(0x19, 9)]
    [InlineData(0x1A, 11)]
    [InlineData(0x1F, 16)]
    [InlineData(0x20, 10)]
    public void GsAddress_MapsPartBlocksToPartNumbers(byte mid, int part) =>
        Assert.Equal(part, new GsAddress(0x40, mid, 0x15).Part);

    [Theory]
    [InlineData(0x40, 0x01, 0x30, GsArea.Reverb, "GS Reverb parameter 30")]
    [InlineData(0x40, 0x01, 0x38, GsArea.Chorus, "GS Chorus parameter 38")]
    [InlineData(0x40, 0x01, 0x10, GsArea.Common, "GS Common parameter 10")]
    [InlineData(0x40, 0x00, 0x04, GsArea.System, "GS System parameter 04")]
    [InlineData(0x40, 0x15, 0x02, GsArea.Part, "GS Part 5 parameter 02")]
    [InlineData(0x40, 0x21, 0x00, GsArea.Part, "GS Part 1 controller parameter 00")]
    [InlineData(0x41, 0x12, 0x24, GsArea.DrumSetup, "GS Drum Map 2 note 36 parameter 02")]
    [InlineData(0x00, 0x00, 0x7F, GsArea.SystemMode, "GS System Mode parameter 7F")]
    [InlineData(0x48, 0x00, 0x00, GsArea.Other, "GS parameter 48 00 00")]
    public void Interpret_ClassifiesGsDataSets(byte high, byte mid, byte low, GsArea area, string summary)
    {
        byte[] payload = [high, mid, low, 0x04];
        var data = Assert.IsType<GsDataSet>(Interpret([0xF0, 0x41, 0x10, 0x42, 0x12, .. payload, GsDialect.Checksum(payload), 0xF7]));

        Assert.Equal(area, data.Address.Area);
        Assert.True(data.ChecksumValid);
        Assert.Equal([0x04], data.Data);
        Assert.Equal(summary, data.Summary);
    }

    [Fact]
    public void GsChecksum_MatchesPublishedGsReset() => Assert.Equal(0x41, GsDialect.Checksum([0x40, 0x00, 0x7F, 0x00]));

    [Fact]
    public void Interpret_NeverThrowsForWellFormedMessages() =>
        Gen.Select(Gen.OneOfConst<byte>(0x7E, 0x7F, 0x43, 0x41, 0x00), Gen.Byte[0, 0x7F].Array[0, 40]).Sample(t =>
        {
            _ = SysExInterpreter.Interpret(SysExMessage.Create([0xF0, t.Item1, .. t.Item2, 0xF7]))?.Summary;
        });
}
