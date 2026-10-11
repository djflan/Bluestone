using System.Buffers;
using Bluestone.Domain.Midi;
using Bluestone.Midi.Files;
using Bluestone.Midi.Wire;
using CsCheck;
using static Bluestone.Tests.Unit.Midi.Files.SmfBytes;

namespace Bluestone.Tests.Unit.Midi.Files;

public sealed class SmfWriterTests
{
    private static readonly MidiChannel One = MidiChannel.FromIndex(0);

    [Fact]
    public void Write_UsesRunningStatusByDefault()
    {
        var file = new SmfFile(SmfFormat.SingleTrack, SmfDivision.Metrical(96), [new SmfTrack([
            new SmfChannelEvent(0, ChannelMessage.NoteOn(One, new NoteNumber(60), new Velocity(100))),
            new SmfChannelEvent(96, ChannelMessage.NoteOn(One, new NoteNumber(64), new Velocity(90))),
        ], 96)]);

        Assert.Equal(File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x60, 0x40, 0x5A)), SmfWriter.Write(file));
        Assert.Equal(
            File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x60, 0x90, 0x40, 0x5A)),
            SmfWriter.Write(file, new SmfWriteOptions { UseRunningStatus = false }));
    }

    [Fact]
    public void Write_ResetsRunningStatusAfterSystemEvents()
    {
        var file = new SmfFile(SmfFormat.SingleTrack, SmfDivision.Metrical(96), [new SmfTrack([
            new SmfChannelEvent(0, ChannelMessage.NoteOn(One, new NoteNumber(60), new Velocity(100))),
            new SmfMetaEvent(0, SmfMetaType.Text, ByteBlock.Empty),
            new SmfChannelEvent(0, ChannelMessage.NoteOn(One, new NoteNumber(64), new Velocity(90))),
        ], 0)]);

        Assert.Equal(File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x00, 0xFF, 0x01, 0x00, 0x00, 0x90, 0x40, 0x5A)), SmfWriter.Write(file));
    }

    [Theory]
    [InlineData(0L, new byte[] { 0x00 })]
    [InlineData(0x7FL, new byte[] { 0x7F })]
    [InlineData(0x80L, new byte[] { 0x81, 0x00 })]
    [InlineData(0x3FFFL, new byte[] { 0xFF, 0x7F })]
    [InlineData(0x4000L, new byte[] { 0x81, 0x80, 0x00 })]
    [InlineData(0x0FFFFFFFL, new byte[] { 0xFF, 0xFF, 0xFF, 0x7F })]
    public void VariableLength_EncodesAndDecodes(long value, byte[] expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        SmfWriter.WriteVariableLength(buffer, value);

        Assert.Equal(expected, buffer.WrittenSpan.ToArray());
        var index = 0;
        Assert.True(SmfReader.TryReadVariableLength(expected, ref index, out var decoded));
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Write_SplitsOversizedDeltasWithoutChangingTiming()
    {
        const long farAway = 0x0FFFFFFFL * 2 + 5;
        var file = new SmfFile(SmfFormat.SingleTrack, SmfDivision.Metrical(96), [new SmfTrack([
            new SmfChannelEvent(farAway, ChannelMessage.NoteOn(One, NoteNumber.MiddleC, Velocity.Max)),
        ], farAway)]);

        var track = SmfReader.Read(SmfWriter.Write(file)).File.Tracks.Single();

        Assert.Equal(farAway, track.Events.OfType<SmfChannelEvent>().Single().Tick);
        Assert.Equal(2, track.Events.OfType<SmfMetaEvent>().Count());
    }

    [Fact]
    public void Write_RejectsUnrepresentableFiles()
    {
        var twoTracks = new SmfFile(SmfFormat.SingleTrack, SmfDivision.Metrical(96), [new SmfTrack([], 0), new SmfTrack([], 0)]);
        var badChunk = new SmfFile(SmfFormat.MultiTrack, SmfDivision.Metrical(96), [new SmfUnknownChunk("TOOLONG", ByteBlock.Empty)]);

        Assert.Throws<ArgumentException>(() => SmfWriter.Write(twoTracks));
        Assert.Throws<ArgumentException>(() => SmfWriter.Write(badChunk));
    }

    [Fact]
    public void Write_EncodesSmpteDivision() =>
        Assert.Equal(File(0, 0xE728, MTrk()), SmfWriter.Write(new SmfFile(SmfFormat.SingleTrack, SmfDivision.Smpte(25, 40), [new SmfTrack([], 0)])));

    [Fact]
    public void SmfTrack_RejectsDecreasingTicks() =>
        Assert.Throws<ArgumentException>(() => new SmfTrack([new SmfMetaEvent(10, 1, ByteBlock.Empty), new SmfMetaEvent(5, 1, ByteBlock.Empty)], 10));

    internal static readonly Gen<SmfEvent> GenEvent =
        Gen.Frequency(
            (6, Gen.Select(Gen.Byte[0x80, 0xEF], Gen.Byte[0, 127], Gen.Byte[0, 127]).Select(x =>
            {
                ChannelMessage.TryCreate(x.Item1, x.Item2, x.Item3, out var message);
                return (SmfEvent)new SmfChannelEvent(0, message);
            })),
            (1, Gen.Select(Gen.Byte[0, 0x7E], Gen.Byte.Array[0, 20]).Where(x => x.Item1 != SmfMetaType.EndOfTrack)
                .Select(x => (SmfEvent)new SmfMetaEvent(0, x.Item1, ByteBlock.Copy(x.Item2)))),
            (1, Gen.Byte[0, 127].Array[0, 20].Select(data => (SmfEvent)new SmfSysExEvent(0, ByteBlock.Copy([.. data, 0xF7])))),
            (1, Gen.Byte.Array[0, 10].Select(data => (SmfEvent)new SmfEscapeEvent(0, ByteBlock.Copy(data)))));

    internal static readonly Gen<SmfTrack> GenTrack =
        Gen.Select(GenEvent, Gen.Long[0, 5000]).Array[0, 40].Select(events =>
        {
            long tick = 0;
            var placed = events.Select(x =>
            {
                tick += x.Item2;
                return x.Item1 with { Tick = tick };
            }).ToList();
            return new SmfTrack(placed, tick);
        });

    [Fact]
    public void WriteThenRead_ReproducesTheFileExactly() =>
        Gen.Select(GenTrack.Array[1, 4], Gen.Bool).Sample((tracks, runningStatus) =>
        {
            var file = new SmfFile(SmfFormat.MultiTrack, SmfDivision.Metrical(480), [.. tracks]);
            var options = new SmfWriteOptions { UseRunningStatus = runningStatus };
            var bytes = SmfWriter.Write(file, options);
            var read = SmfReader.Read(bytes);
            return read.Diagnostics.All(d => d.Code == SmfDiagnosticCodes.RunningStatusAfterSystemEvent)
                && read.File.Tracks.Zip(tracks).All(pair => pair.First.Events.SequenceEqual(pair.Second.Events) && pair.First.EndTick == pair.Second.EndTick)
                && SmfWriter.Write(read.File, options).AsSpan().SequenceEqual(bytes);
        });
}
