using System.Buffers.Binary;
using Bluestone.Engine.Protocol;
using CsCheck;

namespace Bluestone.Tests.Unit.Engine;

/// <summary>
/// The control channel's audio messages (ADR 0044, migration step 6): each round-trips, every count, rate, size, and
/// level is checked when read, and anything malformed is a protocol error, never another failure.
/// </summary>
public sealed class EngineAudioMessageTests
{
    private static readonly Guid Strip = Guid.Parse("6b29fc40-ca47-1067-b31d-00dd010662da");

    private static readonly EngineMessage[] All =
    [
        new SetAudio(1, true, "CoreAudio:BuiltInSpeakerDevice ♪", 48_000, 128, 0, 256, false),
        new SetAudio(2, true, null, 0, 0, 1024, 256, true),
        new SetAudio(3, false, null, 0, 0, 0, 256, false),
        new AudioDeviceStatus(1, AudioDeviceState.Running, null, 48_000, 2, 128, 0, null, 256),
        new AudioDeviceStatus(2, AudioDeviceState.Fallback, "Studio is not available.", 44_100, 2, 256, 1024, "real-time (time constraint)"),
        new AudioDeviceStatus(3, AudioDeviceState.Waiting, "No device.", 0, 0, 0, 0, null),
        new AudioDeviceStatus(0, AudioDeviceState.Off, null, 0, 0, 0, 0, null),
        new LoadAudioGraph(9, "/var/run/bluestone-engine-1/graph-9.bsgr", 48_000, 256),
        new AudioGraphLoaded(9, true, 512, ["Vocal: tone.wav could not be opened.", "Synth: the engine could not reach its plugin."]),
        new AudioGraphLoaded(10, false, 0, []),
        new AudioStatus(new MeterSpec(0.5f, 0.25f, 0.1f, 0.05f), [new StripMeter(Strip, new MeterSpec(1f, 0f, 0.7f, 0f))], new AudioCounters(1000, 1, 2, 3, 4, 5, 6000, 70_000, 8, 256)),
        new AudioStatus(default, [], default),
        new TestRender(1, "/var/run/bluestone-engine-1/render.f32", 48_000, 128),
        new TestRender(3, "/var/run/bluestone-engine-1/render.f32", 4096, 256, [new TestNote(Strip, 1024, TimeSpan.FromMilliseconds(5).Ticks, 0x90, 69, 127), new TestNote(Strip, 3000, 0, 0x80, 69, 64)]),
        new SetAudio(4, true, null, 48_000, 128, 1024, 256, false, LiveQueuedFrames: 256, Host: "ASIO"),
        new AudioDeviceStatus(5, AudioDeviceState.Running, null, 48_000, 2, 128, 0, null, DeviceBufferFrames: EngineLimits.MaxDeviceBufferFrames),
        new AudioStatus(default, [], new AudioCounters(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 512, LiveQueue: true), new LiveLatencySpec(3, 50_000, 40_000, 60_000, 2)),
        new TestRendered(1, null),
        new TestRendered(2, "No audio device is open."),
        new EngineProblem(-1, "Studio is not available. Playing through the system default output until it returns.", EngineProblemArea.Audio),
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

        Assert.Equal(message.GetType(), read!.GetType());
        Assert.Equal(Encoded(message), Encoded(read));
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MessagesWithoutArrays_AreEqualAfterARoundTrip()
    {
        foreach (var message in All.Where(m => m is SetAudio or AudioDeviceStatus or LoadAudioGraph or TestRendered))
        {
            Assert.Equal(message, EngineChannel.Decode(Encoded(message)));
        }

        var status = (AudioStatus)EngineChannel.Decode(Encoded(All[10]));
        Assert.Equal(((AudioStatus)All[10]).Strips, status.Strips);
        Assert.Equal(((AudioStatus)All[10]).Counters, status.Counters);
    }

    public static TheoryData<EngineMessage, string> Malformed() => new()
    {
        { new SetAudio(-1, true, null, 48_000, 128, 0, 256, false), "cannot be negative" },
        { new SetAudio(1, true, string.Empty, 48_000, 128, 0, 256, false), "cannot be empty" },
        { new SetAudio(1, true, null, 48_000, 128, 0, 256, false, Host: string.Empty), "cannot be empty" },
        { new SetAudio(1, true, null, EngineLimits.MaxSampleRate + 1, 128, 0, 256, false), "outside 0" },
        { new SetAudio(1, true, null, 48_000, -1, 0, 256, false), "outside 0" },
        { new SetAudio(1, true, null, 48_000, 128, EngineLimits.MaxQueuedFrames + 1, 256, false), "outside 0" },
        { new SetAudio(1, true, null, 48_000, 128, 0, 0, false), "outside 1" },
        { new AudioDeviceStatus(1, (AudioDeviceState)9, null, 0, 0, 0, 0, null), "Unknown audio device state" },
        { new AudioDeviceStatus(1, AudioDeviceState.Running, null, 48_000, 2, -1, 0, null), "outside 0" },
        { new AudioDeviceStatus(1, AudioDeviceState.Running, null, 48_000, 2, 128, 0, null, DeviceBufferFrames: -1), "outside 0" },
        { new LoadAudioGraph(1, string.Empty, 48_000, 256), "cannot be empty" },
        { new LoadAudioGraph(1, "/g", 0, 256), "outside 1" },
        { new LoadAudioGraph(1, "/g", 48_000, 0), "outside 1" },
        { new AudioGraphLoaded(1, true, -1, []), "outside 0" },
        { new AudioGraphLoaded(1, true, 0, [string.Empty]), "cannot be empty" },
        { new AudioStatus(new MeterSpec(float.NaN, 0, 0, 0), [], default), "not a level" },
        { new AudioStatus(new MeterSpec(-1, 0, 0, 0), [], default), "not a level" },
        { new AudioStatus(default, [new StripMeter(Strip, new MeterSpec(0, float.PositiveInfinity, 0, 0))], default), "not a level" },
        { new AudioStatus(default, [], new AudioCounters(-1, 0, 0, 0, 0, 0, 0, 0, 0, 0)), "cannot be negative" },
        { new TestRender(1, "/r", 0, 128), "outside 1" },
        { new TestRender(1, "/r", 128, EngineLimits.MaxBufferFrames + 1), "outside 1" },
        { new EngineProblem(-1, "x", (EngineProblemArea)7), "Unknown problem area" },
        { new SetAudio(1, true, null, 48_000, 128, 1024, 256, false, -1), "outside 0" },
        { new TestRender(1, "/r", 128, 128, [new TestNote(Strip, 128, 0, 0x90, 60, 100)]), "outside 0" },
        { new TestRender(1, "/r", 128, 128, [new TestNote(Strip, 0, 0, 0xF8, 60, 100)]), "status" },
        { new TestRender(1, "/r", 128, 128, [new TestNote(Strip, 0, -1, 0x90, 60, 100)]), "cannot be negative" },
        { new AudioStatus(default, [], default, new LiveLatencySpec(-1, 0, 0, 0, 0)), "cannot be negative" },
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
    public void ACountBeyondTheLimit_IsAProtocolError_WithoutReadingFurther()
    {
        var body = new byte[2 + 16 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)EngineMessageType.AudioStatus);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2 + 16), EngineLimits.MaxMeters + 1);

        Assert.Throws<EngineProtocolException>(() => EngineChannel.Decode(body));
    }

    [Fact]
    public void MessagesBeyondTheLimits_AreRefusedWhenWritten()
    {
        var frame = new byte[EngineLimits.MaxFrameBytes];

        Assert.Throws<ArgumentException>(() => EngineChannel.Encode(new AudioStatus(default, [.. Enumerable.Repeat(new StripMeter(Strip, default), EngineLimits.MaxMeters + 1)], default), frame));
        Assert.Throws<ArgumentException>(() => EngineChannel.Encode(new AudioGraphLoaded(1, true, 0, [.. Enumerable.Repeat("x", EngineLimits.MaxGraphProblems + 1)]), frame));
    }

    [Fact]
    public void AFullStatus_FitsInAFrame()
    {
        var frame = new byte[EngineLimits.MaxFrameBytes];

        var length = EngineChannel.Encode(new AudioStatus(default, [.. Enumerable.Repeat(new StripMeter(Strip, new MeterSpec(1, 1, 1, 1)), EngineLimits.MaxMeters)], default), frame);

        Assert.InRange(length, 1, EngineLimits.MaxFrameBytes);
    }

    [Fact]
    public void AnyBodyOfAnAudioMessageType_DecodesOrIsAProtocolError_NeverAnotherFailure() =>
        Gen.Select(Gen.UShort[(ushort)EngineMessageType.SetAudio, (ushort)EngineMessageType.TestRendered], Gen.Byte.Array[0, 96]).Sample((type, tail) =>
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
