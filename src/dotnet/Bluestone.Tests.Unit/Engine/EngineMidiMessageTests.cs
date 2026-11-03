using System.Buffers.Binary;
using Bluestone.Engine.Protocol;
using CsCheck;

namespace Bluestone.Tests.Unit.Engine;

/// <summary>
/// The control channel's MIDI messages (ADR 0044, migration step 5): each round-trips, every count, handle, and MIDI
/// byte is checked when read, and anything malformed is a protocol error, never another failure.
/// </summary>
public sealed class EngineMidiMessageTests
{
    private static readonly Guid Track = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static readonly EngineMessage[] All =
    [
        new OpenOutput(3, "coremidi", "12345", "Synth ♪", Forwarded: false, Scheduled: false),
        new OpenOutput(4, "loopback", "monitor", "Bluestone Monitor", Forwarded: true, Scheduled: true),
        new CloseOutput(3),
        new SetSlots([3, -1, 4]),
        new SetSlots([]),
        new LoadPlan(7, "/var/run/bluestone-engine-1/plan-7.bspl"),
        new PlanLoaded(7),
        new TransportPlay(-1, null),
        new TransportPlay(1920, new CountInSpec(TimeSpan.FromSeconds(2).Ticks, [new CountInClickSpec(0, true), new CountInClickSpec(TimeSpan.FromMilliseconds(500).Ticks, false)])),
        new TransportStop(),
        new TransportSeek(960),
        new SetLoop(true, 0, 3840),
        new SetLoop(false, 0, 0),
        new SetMetronome(new MetronomeSpec(2, 9, 76, 110, 77, 80, TimeSpan.FromMilliseconds(40).Ticks), Enabled: true),
        new SetMetronome(null, Enabled: false),
        new Panic(),
        new Nudge(-1234),
        new OutputSend(3, new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 }),
        new SetRoute(9, Track, 3, 32640, [new RouteStageSpec("bluestone.midi.transpose", -1, [new RouteParameter(1, 0x8000_0000)]), new RouteStageSpec(string.Empty, 9, [])]),
        new RemoveRoute(9),
        new RouteInput(9, 123_456_789, 0x90, 60, 100),
        new SetThru(Track),
        new SetThru(null),
        new SetInputs([new InputSpec(5, "coremidi", "678", "Keys"), new InputSpec(6, "winmm", "in:0", "USB MIDI")]),
        new TransportStatus(true, 1000, 3, 0, new AnchorSpec(true, 10, 20, 7, 0, 3840), new AnchorSpec(false, 0, 0, -1, 0, 0)),
        new TimingStatus(100, 1, 2, 3, 4, 5000, [90, 10, 0, 0, 0, 0, 0, 0, 0], [new FineBucket(0, 90), new FineBucket(2000, 10)]),
        new MidiBatch([new MidiEvent(MidiEventKind.Output, 4, 0, new byte[] { 0x90, 60, 100 }), new MidiEvent(MidiEventKind.Input, 5, 42, new byte[] { 0x80, 60, 64 })]),
        new EngineProblem(-1, "The engine cannot open Keys."),
    ];

    public static TheoryData<EngineMessage> Messages() => new(All);

    [Theory]
    [MemberData(nameof(Messages))]
    public async Task EveryMessage_RoundTrips(EngineMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new EngineChannel(stream))
        {
            await writer.WriteAsync(message, TestContext.Current.CancellationToken);
        }

        stream.Position = 0;
        using var reader = new EngineChannel(stream);
        var read = await reader.ReadAsync(TestContext.Current.CancellationToken);

        // Messages with arrays compare by what they encode to.
        Assert.Equal(message.GetType(), read!.GetType());
        Assert.Equal(Encoded(message), Encoded(read));
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    public static TheoryData<EngineMessage, string> Malformed() => new()
    {
        { new CloseOutput(-2), "outside 0" },
        { new SetSlots([-2]), "outside -1" },
        { new SetLoop(true, 960, 960), "end after it starts" },
        { new TransportSeek(-1), "cannot be negative" },
        { new TransportPlay(-2, null), "Cannot play from" },
        { new SetRoute(1, Track, 1, 0, []), "outside 1" },
        { new SetRoute(1, Track, 1, 960, [new RouteStageSpec("bluestone.midi.transpose", 3, [])]), "not both or neither" },
        { new SetRoute(1, Track, 1, 960, [new RouteStageSpec(string.Empty, -1, [])]), "not both or neither" },
        { new RouteInput(1, 0, 0xF8, 0, 0), "status" },
        { new RouteInput(1, 0, 0x90, 128, 0), "above 127" },
        { new SetMetronome(new MetronomeSpec(0, 16, 76, 110, 77, 80, 1), true), "above 15" },
        { new OpenOutput(1, string.Empty, "k", "n", false, false), "cannot be empty" },
        { new LoadPlan(1, string.Empty), "cannot be empty" },
        { new TimingStatus(-1, 0, 0, 0, 0, 0, [], []), "cannot be negative" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void OutOfRangeValues_AreProtocolErrors(EngineMessage message, string why)
    {
        var body = new byte[EngineLimits.MaxFrameBytes];
        var length = EngineChannel.Encode(message, body);

        var error = Assert.Throws<EngineProtocolException>(() => EngineChannel.Decode(body.AsSpan(0, length)));
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownMidiEventKind_IsAProtocolError()
    {
        var body = Encoded(new MidiBatch([new MidiEvent(MidiEventKind.Input, 1, 0, new byte[] { 0x90, 60, 1 })]));
        body[2 + 4] = 9;

        Assert.Contains("Unknown MIDI event kind", Assert.Throws<EngineProtocolException>(() => EngineChannel.Decode(body)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountBeyondTheLimit_IsAProtocolError_WithoutReadingFurther()
    {
        var body = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)EngineMessageType.SetSlots);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), EngineLimits.MaxSlots + 1);

        Assert.Throws<EngineProtocolException>(() => EngineChannel.Decode(body));
    }

    [Fact]
    public void MessagesBeyondTheLimits_AreRefusedWhenWritten()
    {
        var frame = new byte[EngineLimits.MaxFrameBytes];

        Assert.Throws<ArgumentException>(() => EngineChannel.Encode(new SetSlots([.. new int[EngineLimits.MaxSlots + 1]]), frame));
        Assert.Throws<ArgumentException>(() => EngineChannel.Encode(new OutputSend(1, new byte[EngineLimits.MaxMidiMessageBytes + 1]), frame));
        Assert.Throws<ArgumentException>(() => EngineChannel.Encode(new OutputSend(1, Array.Empty<byte>()), frame));
    }

    [Fact]
    public void AnyBodyOfAMidiMessageType_DecodesOrIsAProtocolError_NeverAnotherFailure() =>
        Gen.Select(Gen.UShort[(ushort)EngineMessageType.OpenOutput, (ushort)EngineMessageType.EngineProblem], Gen.Byte.Array[0, 96]).Sample((type, tail) =>
        {
            var body = new byte[2 + tail.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(body, type);
            tail.CopyTo(body, 2);
            try
            {
                _ = EngineChannel.Decode(body);
            }
            catch (EngineProtocolException)
            {
            }
        }, iter: 50_000);

    [Fact]
    public void ValidMessagesWithOneByteChanged_DecodeOrAreProtocolErrors() =>
        Gen.Select(Gen.Int[0, All.Length - 1], Gen.Int[0, int.MaxValue], Gen.Byte).Sample((index, at, value) =>
        {
            var body = Encoded(All[index]);
            body[at % body.Length] = value;
            try
            {
                _ = EngineChannel.Decode(body);
            }
            catch (EngineProtocolException)
            {
            }
        }, iter: 50_000);

    private static byte[] Encoded(EngineMessage message)
    {
        var body = new byte[EngineLimits.MaxFrameBytes];
        return body.AsSpan(0, EngineChannel.Encode(message, body)).ToArray();
    }
}
