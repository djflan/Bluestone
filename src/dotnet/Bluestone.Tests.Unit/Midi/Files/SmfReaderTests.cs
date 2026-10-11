using Bluestone.Domain.Midi;
using Bluestone.Midi.Files;
using static Bluestone.Tests.Unit.Midi.Files.SmfBytes;

namespace Bluestone.Tests.Unit.Midi.Files;

public sealed class SmfReaderTests
{
    [Fact]
    public void Read_ParsesRunningStatusAndNoteOnZeroVelocity()
    {
        var bytes = File(0, 96, MTrk(
            0x00, 0x90, 0x3C, 0x64,   // note on C4
            0x60, 0x3C, 0x00,         // running status: note on vel 0 (release) after 96 ticks
            0x00, 0x40, 0x50));       // running status: note on E4

        var result = SmfReader.Read(bytes);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(SmfFormat.SingleTrack, result.File.Format);
        Assert.Equal(96, result.File.Division.TicksPerQuarterNote);
        var events = result.File.Tracks.Single().Events.Cast<SmfChannelEvent>().ToList();
        Assert.Equal([0L, 96L, 96L], events.Select(e => e.Tick));
        Assert.True(events[1].Message.IsNoteOff);
        Assert.Equal(new NoteNumber(0x40), events[2].Message.Note);
    }

    [Fact]
    public void Read_ParsesMetaSysExAndEscapeEvents()
    {
        var bytes = File(0, 480, MTrk(
            0x00, 0xFF, 0x03, 0x03, (byte)'A', (byte)'b', (byte)'c',
            0x00, 0xF0, 0x05, 0x7E, 0x7F, 0x09, 0x01, 0xF7,
            0x10, 0xF7, 0x01, 0xFA));

        var events = SmfReader.Read(bytes).File.Tracks.Single().Events;

        var meta = Assert.IsType<SmfMetaEvent>(events[0]);
        Assert.Equal(SmfMetaType.TrackName, meta.Type);
        var sysEx = Assert.IsType<SmfSysExEvent>(events[1]);
        Assert.True(sysEx.IsComplete);
        Assert.Equal([0x7E, 0x7F, 0x09, 0x01, 0xF7], sysEx.Data.ToArray());
        var escape = Assert.IsType<SmfEscapeEvent>(events[2]);
        Assert.Equal(16, escape.Tick);
    }

    [Fact]
    public void Read_ParsesSmpteDivision()
    {
        var division = SmfReader.Read(File(0, 0xE728, MTrk())).File.Division; // -25 fps, 40 ticks per frame

        Assert.True(division.IsSmpte);
        Assert.Equal((25, 40), (division.FramesPerSecond, division.TicksPerFrame));
    }

    [Theory]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 5, 0, 0, 0, 1, 0, 96 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 3, 0, 1, 0, 96 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0, 0 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0xE7, 0 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0xE6, 40 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 200, 0, 0, 0, 1, 0, 96 })]
    public void Read_RejectsUnreadableHeaders(byte[] bytes) =>
        Assert.Throws<SmfFormatException>(() => SmfReader.Read(bytes));

    [Fact]
    public void Read_ReportsMissingEndOfTrack()
    {
        var result = SmfReader.Read(File(0, 96, [0x00, 0x90, 0x3C, 0x64]));

        Assert.Single(result.File.Tracks.Single().Events);
        AssertCode(result, SmfDiagnosticCodes.MissingEndOfTrack);
    }

    [Fact]
    public void Read_KeepsEventsBeforeTruncation()
    {
        byte[] body = [0x00, 0x90, 0x3C, 0x64, 0x10, 0x90, 0x3C];
        var bytes = (byte[])[.. Header(0, 1, 96), .. Chunk("MTrk", body, declaredLength: 100)];

        var result = SmfReader.Read(bytes);

        Assert.Single(result.File.Tracks.Single().Events);
        AssertCode(result, SmfDiagnosticCodes.TruncatedChunk);
        AssertCode(result, SmfDiagnosticCodes.MalformedEvent);
    }

    [Fact]
    public void Read_IgnoresDataAfterEndOfTrack()
    {
        var result = SmfReader.Read(File(0, 96, [.. EndOfTrack, 0x00, 0x90, 0x3C, 0x64]));

        Assert.Empty(result.File.Tracks.Single().Events);
        AssertCode(result, SmfDiagnosticCodes.DataAfterEndOfTrack);
    }

    [Fact]
    public void Read_PreservesUnknownChunks()
    {
        var bytes = (byte[])[.. File(1, 96, MTrk()), .. Chunk("XFIH", [1, 2, 3])];

        var result = SmfReader.Read(bytes);

        var unknown = Assert.IsType<SmfUnknownChunk>(result.File.Chunks[1]);
        Assert.Equal("XFIH", unknown.Type);
        Assert.Equal([1, 2, 3], unknown.Data.ToArray());
        AssertCode(result, SmfDiagnosticCodes.UnknownChunk, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Read_ReportsTrailingBytesAndTrackCountMismatch()
    {
        var bytes = (byte[])[.. Header(1, 3, 96), .. Chunk("MTrk", MTrk()), 0x00, 0x01];

        var result = SmfReader.Read(bytes);

        AssertCode(result, SmfDiagnosticCodes.TrailingBytes);
        AssertCode(result, SmfDiagnosticCodes.TrackCountMismatch);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0xF1, 0x00 })]                   // system common status
    [InlineData(new byte[] { 0x00, 0xF8 })]                         // realtime status
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x00 })]       // five-byte delta
    [InlineData(new byte[] { 0x00, 0x90, 0x3C, 0x80 })]             // data byte above 127
    [InlineData(new byte[] { 0x00, 0xFF, 0x80, 0x00 })]             // meta type above 127
    [InlineData(new byte[] { 0x00, 0xFF, 0x01, 0x10, 0x41 })]       // meta longer than track
    [InlineData(new byte[] { 0x00, 0xF0, 0x8F, 0xFF, 0xFF, 0xFF })] // unterminated length
    public void Read_StopsTrackAtMalformedEvent(byte[] body)
    {
        var result = SmfReader.Read(File(0, 96, [0x00, 0x90, 0x3C, 0x64, .. body]));

        Assert.Single(result.File.Tracks.Single().Events);
        AssertCode(result, SmfDiagnosticCodes.MalformedEvent);
    }

    [Fact]
    public void Read_StopsTrackAtDataByteWithoutStatus()
    {
        var result = SmfReader.Read(File(0, 96, MTrk(0x00, 0x3C, 0x64)));

        Assert.Empty(result.File.Tracks.Single().Events);
        AssertCode(result, SmfDiagnosticCodes.MalformedEvent);
    }

    [Fact]
    public void Read_AllowsRunningStatusAcrossMetaEventsWithNotice()
    {
        var result = SmfReader.Read(File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x00, 0xFF, 0x01, 0x00, 0x10, 0x3C, 0x00)));

        Assert.Equal(3, result.File.Tracks.Single().Events.Length);
        AssertCode(result, SmfDiagnosticCodes.RunningStatusAfterSystemEvent, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Read_FoldsRepeatedDiagnostics()
    {
        var result = SmfReader.Read(File(1, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x00, 0xFF, 0x01, 0x00, 0x10, 0x3C, 0x00, 0x00, 0xFF, 0x01, 0x00, 0x10, 0x3C, 0x00)));

        Assert.Equal(2, Assert.Single(result.Diagnostics).Count);
    }

    [Fact]
    public void Read_EnforcesLimits()
    {
        var twoTracks = File(1, 96, MTrk(), MTrk());
        Assert.Throws<SmfFormatException>(() => SmfReader.Read(twoTracks, new SmfReadOptions { MaxTracks = 1 }));
        Assert.Throws<SmfFormatException>(() => SmfReader.Read(twoTracks, new SmfReadOptions { MaxFileBytes = twoTracks.Length - 1 }));
        Assert.Throws<SmfFormatException>(() => SmfReader.Read(new MemoryStream(twoTracks), new SmfReadOptions { MaxFileBytes = twoTracks.Length - 1 }));
        Assert.Throws<SmfFormatException>(() => SmfReader.Read(File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x00, 0x3C, 0x00)), new SmfReadOptions { MaxEvents = 2 }));

        var bigMeta = SmfReader.Read(File(0, 96, MTrk(0x00, 0xFF, 0x01, 0x05, 1, 2, 3, 4, 5)), new SmfReadOptions { MaxEventDataBytes = 4 });
        AssertCode(bigMeta, SmfDiagnosticCodes.MalformedEvent);
    }

    [Fact]
    public void Read_FromStreamMatchesSpan()
    {
        var bytes = File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64));

        Assert.Equal(SmfWriter.Write(SmfReader.Read(bytes).File), SmfWriter.Write(SmfReader.Read(new MemoryStream(bytes)).File));
    }

    [Fact]
    public void Read_DoesNotModifySource()
    {
        var bytes = File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64));
        var copy = (byte[])bytes.Clone();

        SmfReader.Read(bytes);

        Assert.Equal(copy, bytes);
    }

    internal static void AssertCode(SmfReadResult result, string code, SmfDiagnosticSeverity severity = SmfDiagnosticSeverity.Warning) =>
        AssertCode(result.Diagnostics, code, severity);

    internal static void AssertCode(IEnumerable<SmfDiagnostic> diagnostics, string code, SmfDiagnosticSeverity severity = SmfDiagnosticSeverity.Warning)
    {
        var diagnostic = Assert.Single(diagnostics, d => d.Code == code);
        Assert.Equal(severity, diagnostic.Severity);
    }
}
