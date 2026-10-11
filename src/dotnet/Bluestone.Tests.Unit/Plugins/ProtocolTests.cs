using System.Buffers.Binary;
using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using CsCheck;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class FrameCodecTests
{
    private static readonly PluginIdentity Gain = new("bluestone-reference", "bluestone.reference", "reference.gain", "Reference Gain", "Bluestone", PluginKind.AudioEffect, "1.0.0");
    private static readonly PluginInstanceId Instance = new(Guid.Parse("5b0d3a1e-6f7c-4c55-9d1a-0123456789ab"));
    private static readonly PluginStateData State = new("bluestone.reference-state", [1, 2, 3, 250]);

    public static TheoryData<ProtocolMessage> EveryMessage() =>
    [
        new Hello(ProtocolLimits.ProtocolVersion, "token-é", 4242, WorkerMode.InstanceHost),
        new HelloAck(ProtocolLimits.ProtocolVersion),
        new Ping(long.MaxValue),
        new Pong(-1),
        new CreateInstance(Instance, Gain, 48_000, new ExchangeOptions(256, 2, 2, 3, 64, 32), 7, "/data/dir", State),
        new CreateInstance(Instance, Gain, 44_100, new ExchangeOptions(64, 0, 1), 1, "d", null),
        new InstanceCreated(Instance, 12, [new ParameterDescriptor(0, "Gain", 0.5), new ParameterDescriptor(9, "Invert", 0, 1)], "/data/dir/file", 7, null),
        new InstanceCreated(Instance, 0, [], "p", 2, "bad state"),
        new DestroyInstance(Instance),
        new CaptureState(Instance),
        new StateResult(Instance, State),
        new RestoreState(Instance, new PluginStateData("f", [])),
        new RestoreStateResult(Instance, false, "corrupt"),
        new RestoreStateResult(Instance, true, null),
        new SetParameter(Instance, 3, 1.0),
        new GetParameters(Instance),
        new ParameterValues(Instance, [new ParameterValue(0, 0.25), new ParameterValue(1, 0)]),
        new Ack(),
        new ErrorReply(ErrorCode.PluginNotFound, "no such plugin"),
        new Shutdown(),
        new ScanModule("/plugins/a.bluestone-reference-plugin"),
        new ScanResult("/plugins/a.bluestone-reference-plugin", [Gain], null, null),
        new ScanResult("/plugins/b.txt", [], ScanFailureKind.Malformed, "not a module"),
        new InduceTestFault(TestFault.GarbageFrames),
        new RenderEvents(Gain, State, [new ParameterValue(0, 0.75)], 48_000, 512, 96_000, [new TimelineTempo(0, 120), new TimelineTempo(48_000, 90.5)], [new TimelineEvent(0, PluginEvent.NoteOn(0, 2, 60, 100)), new TimelineEvent(24_000, SysEx())], [new TimelineParameterChange(12_000, 0, 0.25)]),
        new RenderEvents(Gain, null, [], 44_100, 1, 0, [], [], []),
        new RenderedEvents([new TimelineEvent(5, PluginEvent.NoteOff(0, 15, 127, 64)), new TimelineEvent(ProtocolLimits.MaxRenderFrames, PluginEvent.PitchBend(0, 0, 16383))], 3, "bad state"),
        new RenderedEvents([], 0, null),
        new CreateInstance(Instance, Vst3, 48_000, new ExchangeOptions(256, 0, 2), 1, "d", null, "/Library/Audio/Plug-Ins/VST3/Synth.vst3"),
        new InstanceCreated(Instance, 0, [new ParameterDescriptor(42, "Cutoff", 0.3, 0, "Hz", ParameterTraits.Automatable | ParameterTraits.List), new ParameterDescriptor(7, "CC 1", 0, 0, "", ParameterTraits.MidiController)], "p", 1, null),
        new ScanResult("/plugins/x86.vst3", [], ScanFailureKind.Unsupported, "built for x86_64 only"),
        new RenderEvents(Vst3, null, [], 48_000, 64, 128, [], [], [], "/plugins/Synth.clap"),
        new OpenEditor(Instance, "Synth"),
        new CloseEditor(Instance),
        new PluginNotification(Instance, NotificationKind.ParameterEdited, 42, 0.75),
        new PluginNotification(Instance, NotificationKind.LatencyChanged, 0, 512),
        new PluginNotification(Instance, NotificationKind.StateDirty, 0, 0),
        new PluginNotification(Instance, NotificationKind.EditorClosed, 0, 0),
        new PluginNotification(Instance, NotificationKind.ParametersChanged, 0, 0),
    ];

    private static readonly PluginIdentity Vst3 = new("vst3", "Synth", "0123456789ABCDEF0123456789ABCDEF", "Synth", "Vendor", PluginKind.Instrument, "1.0");

    [Fact]
    public void Decode_ThirdPartyFields_AreKept()
    {
        var create = new CreateInstance(Instance, Vst3, 48_000, new ExchangeOptions(64, 0, 2), 1, "d", null, "/plugins/Synth.vst3");
        var created = new InstanceCreated(Instance, 0, [new ParameterDescriptor(42, "Cutoff", 0.3, 4, "Hz", ParameterTraits.Automatable | ParameterTraits.Bypass)], "p", 1, null);

        var decodedCreate = Assert.IsType<CreateInstance>(FrameCodec.Decode(FrameCodec.Encode(1, create)).Message);
        var parameter = Assert.Single(Assert.IsType<InstanceCreated>(FrameCodec.Decode(FrameCodec.Encode(1, created)).Message).Parameters);

        Assert.Equal("/plugins/Synth.vst3", decodedCreate.ModulePath);
        Assert.Equal((42u, "Cutoff", 0.3, 4, "Hz", ParameterTraits.Automatable | ParameterTraits.Bypass), (parameter.Id, parameter.Name, parameter.DefaultValue, parameter.StepCount, parameter.Units, parameter.Traits));
    }

    [Theory]
    [InlineData(NotificationKind.ParameterEdited, 1u, 1.5)]
    [InlineData(NotificationKind.ParameterEdited, 1u, double.NaN)]
    [InlineData(NotificationKind.LatencyChanged, 0u, 12.5)]
    [InlineData(NotificationKind.LatencyChanged, 0u, -1)]
    [InlineData(NotificationKind.LatencyChanged, 3u, 12)]
    [InlineData(NotificationKind.StateDirty, 0u, 1)]
    [InlineData(NotificationKind.EditorClosed, 9u, 0)]
    public void Decode_ANotificationWithValuesItsKindDoesNotAllow_Throws(NotificationKind kind, uint parameterId, double value)
    {
        var encoded = FrameCodec.Encode(0, new PluginNotification(Instance, kind, parameterId, value));

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(encoded));
    }

    [Fact]
    public void Decode_UnknownNotificationKindOrParameterTraits_Throws()
    {
        var notification = FrameCodec.Encode(0, new PluginNotification(Instance, NotificationKind.StateDirty, 0, 0));
        notification[ProtocolLimits.LengthPrefixBytes + ProtocolLimits.FrameHeaderBytes + 16] = 99;
        var created = FrameCodec.Encode(1, new InstanceCreated(Instance, 0, [new ParameterDescriptor(1, "P", 0)], "p", 1, null));
        var traitsOffset = ProtocolLimits.LengthPrefixBytes + ProtocolLimits.FrameHeaderBytes + 16 + 4 + 4 + 4 + (2 + 1) + 8 + 4 + 2;
        created[traitsOffset] = 0x80;

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(notification));
        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(created));
    }

    [Fact]
    public void ParameterDescriptor_UserFacing_LeavesOutHiddenReadOnlyAndMidiControllerStandIns()
    {
        Assert.True(new ParameterDescriptor(0, "Cutoff", 0, traits: ParameterTraits.Automatable | ParameterTraits.Bypass).IsUserFacing);
        Assert.False(new ParameterDescriptor(0, "Meter", 0, traits: ParameterTraits.ReadOnly).IsUserFacing);
        Assert.False(new ParameterDescriptor(0, "Internal", 0, traits: ParameterTraits.Hidden).IsUserFacing);
        Assert.False(new ParameterDescriptor(0, "MIDI CC 0|1", 0, traits: ParameterTraits.None | ParameterTraits.MidiController).IsUserFacing);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParameterDescriptor(0, "x", 0, traits: (ParameterTraits)0x80));
    }

    [Fact]
    public void Decode_RenderEvents_KeepsEveryField()
    {
        var message = new RenderEvents(Gain, State, [new ParameterValue(1, 0.5)], 48_000, 256, 10_000, [new TimelineTempo(0, 133.25)], [new TimelineEvent(9_999, SysEx()), new TimelineEvent(3, PluginEvent.ControlChange(0, 4, 7, 99))], [new TimelineParameterChange(77, 1, 1)]);

        var decoded = Assert.IsType<RenderEvents>(FrameCodec.Decode(FrameCodec.Encode(1, message)).Message);

        Assert.Equal(Gain, decoded.Plugin);
        Assert.Equal(State, decoded.State);
        Assert.Equal(message.Parameters, decoded.Parameters);
        Assert.Equal((48_000.0, 256, 10_000L), (decoded.SampleRate, decoded.BlockFrames, decoded.EndFrame));
        Assert.Equal(message.Tempo, decoded.Tempo);
        Assert.Equal(message.Events, decoded.Events);
        Assert.Equal(message.Changes, decoded.Changes);
    }

    [Fact]
    public void Decode_RejectsARenderRequestWithANegativeFrame()
    {
        var encoded = FrameCodec.Encode(1, new RenderedEvents([new TimelineEvent(7, PluginEvent.NoteOn(0, 0, 60, 1))], 0, null));
        BinaryPrimitives.WriteInt64LittleEndian(encoded.AsSpan(ProtocolLimits.LengthPrefixBytes + ProtocolLimits.FrameHeaderBytes + 4), -7);

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(encoded));
    }

    private static PluginEvent SysEx()
    {
        Assert.True(PluginEvent.TryCreateSystemExclusive(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7], out var sysEx));
        return sysEx;
    }

    [Theory]
    [MemberData(nameof(EveryMessage))]
    public void EveryMessage_RoundTrips(ProtocolMessage message)
    {
        var encoded = FrameCodec.Encode(77, message);

        var decoded = FrameCodec.Decode(encoded);

        Assert.Equal(77u, decoded.RequestId);
        Assert.Equal(message.GetType(), decoded.Message.GetType());
        Assert.Equal(encoded, FrameCodec.Encode(77, decoded.Message));
    }

    [Fact]
    public void Decode_CreateInstance_KeepsEveryField()
    {
        var message = new CreateInstance(Instance, Gain, 48_000, new ExchangeOptions(256, 2, 3, 4, 64, 32), 7, "/data/dir", State);

        var decoded = Assert.IsType<CreateInstance>(FrameCodec.Decode(FrameCodec.Encode(1, message)).Message);

        Assert.Equal(Instance, decoded.InstanceId);
        Assert.Equal(Gain, decoded.Plugin);
        Assert.Equal(48_000, decoded.SampleRate);
        Assert.Equal(message.Exchange, decoded.Exchange);
        Assert.Equal(7u, decoded.Generation);
        Assert.Equal("/data/dir", decoded.DataPlaneDirectory);
        Assert.Equal(State, decoded.State);
    }

    [Fact]
    public async Task ReadAsync_ReadsConsecutiveFrames_ThenNullAtACleanEnd()
    {
        using var stream = new MemoryStream([.. FrameCodec.Encode(1, new Ping(5)), .. FrameCodec.Encode(2, new Ack())]);

        var first = await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        var second = await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        var end = await FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(new Ping(5), first!.Value.Message);
        Assert.Equal(2u, second!.Value.RequestId);
        Assert.Null(end);
    }

    [Fact]
    public async Task ReadAsync_StreamEndingInsideAFrame_ThrowsProtocolException()
    {
        var frame = FrameCodec.Encode(1, new ScanModule("/a/b"));
        for (var length = 1; length < frame.Length; length++)
        {
            using var stream = new MemoryStream(frame[..length]);

            await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ReadAsync_OversizedLength_ThrowsWithoutAllocatingTheBody()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken));

        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(5u)]
    [InlineData((uint)ProtocolLimits.MaxFrameBytes + 1)]
    [InlineData(uint.MaxValue)]
    public void Decode_OutOfRangeLength_Throws(uint length)
    {
        var frame = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, length);

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_UnknownMessageType_Throws()
    {
        var frame = FrameCodec.Encode(1, new Ack());
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 999);

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_TrailingBytes_Throws()
    {
        var frame = FrameCodec.Encode(1, new Ping(1));
        byte[] longer = [.. frame, 0];
        BinaryPrimitives.WriteInt32LittleEndian(longer, frame.Length - 4 + 1);

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(longer));
    }

    [Fact]
    public void Decode_ParameterValueOutsideZeroToOne_Throws()
    {
        var frame = FrameCodec.Encode(1, new SetParameter(Instance, 1, 0.5));
        BinaryPrimitives.WriteDoubleLittleEndian(frame.AsSpan(frame.Length - 8), 1.5);

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_HugeElementCount_ThrowsWithoutAllocatingIt()
    {
        var frame = FrameCodec.Encode(1, new ParameterValues(Instance, [new ParameterValue(0, 0.5)]));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4 + 6 + 16), 4000);

        Assert.Throws<ProtocolException>(() => FrameCodec.Decode(frame));
    }

    [Fact]
    public void Decode_ArbitraryBytes_ThrowsOnlyProtocolException() =>
        Gen.Byte.Array[0, 300].Sample(bytes =>
        {
            try
            {
                FrameCodec.Decode(bytes);
            }
            catch (ProtocolException)
            {
            }
        }, iter: 20_000, threads: 1);

    [Fact]
    public void Decode_ValidFramesWithCorruptedBytes_ThrowsOnlyProtocolException()
    {
        var frames = EveryMessage().Select(row => FrameCodec.Encode(3, row.Data)).ToArray();
        Gen.Select(Gen.Int[0, frames.Length - 1], Gen.Int[0, 1000], Gen.Byte, Gen.Int[0, 1000]).Sample((which, position, value, cut) =>
        {
            var frame = frames[which].ToArray();
            frame[position % frame.Length] = value;
            var truncated = frame[..(cut % (frame.Length + 1))];
            foreach (var candidate in new[] { frame, truncated })
            {
                try
                {
                    FrameCodec.Decode(candidate);
                }
                catch (ProtocolException)
                {
                }
            }
        }, iter: 20_000, threads: 1);
    }

    [Fact]
    public async Task ReadAsync_ArbitraryBytes_ThrowsOnlyProtocolException() =>
        await Gen.Byte.Array[0, 300].SampleAsync(async bytes =>
        {
            using var stream = new MemoryStream(bytes);
            try
            {
                while (await FrameCodec.ReadAsync(stream, CancellationToken.None) is not null)
                {
                }
            }
            catch (ProtocolException)
            {
            }
        }, iter: 2_000, threads: 1);

    [Fact]
    public void Encode_StringLongerThanTheLimit_Throws() =>
        Assert.Throws<ProtocolException>(() => FrameCodec.Encode(1, new ScanModule(new string('x', ProtocolLimits.MaxStringBytes + 1))));
}

public sealed class HandshakeTests
{
    [Fact]
    public void ValidateHello_MatchingVersionTokenAndMode_Passes() =>
        Handshake.ValidateHello(Handshake.CreateHello("abc", WorkerMode.Scanner), "abc", WorkerMode.Scanner);

    [Fact]
    public void ValidateHello_VersionMismatch_IsRejected()
    {
        var hello = new Hello(ProtocolLimits.ProtocolVersion + 1, "abc", 1, WorkerMode.InstanceHost);

        var error = Assert.Throws<ProtocolException>(() => Handshake.ValidateHello(hello, "abc", WorkerMode.InstanceHost));

        Assert.Contains("version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateHello_WrongToken_IsRejected() =>
        Assert.Throws<ProtocolException>(() => Handshake.ValidateHello(Handshake.CreateHello("abc", WorkerMode.InstanceHost), "abd", WorkerMode.InstanceHost));

    [Fact]
    public void ValidateHello_WrongMode_IsRejected() =>
        Assert.Throws<ProtocolException>(() => Handshake.ValidateHello(Handshake.CreateHello("abc", WorkerMode.Scanner), "abc", WorkerMode.InstanceHost));

    [Fact]
    public void ValidateHello_AnotherMessage_IsRejected() =>
        Assert.Throws<ProtocolException>(() => Handshake.ValidateHello(new Ping(1), "abc", WorkerMode.InstanceHost));

    [Fact]
    public void ValidateAck_VersionMismatchOrError_IsRejected()
    {
        Assert.Throws<ProtocolException>(() => Handshake.ValidateAck(new HelloAck(ProtocolLimits.ProtocolVersion + 1)));
        Assert.Throws<ProtocolException>(() => Handshake.ValidateAck(new ErrorReply(ErrorCode.InvalidRequest, "no")));
        Handshake.ValidateAck(new HelloAck(ProtocolLimits.ProtocolVersion));
    }

    [Fact]
    public void CreateToken_IsRandomPerLaunch() =>
        Assert.NotEqual(Handshake.CreateToken(), Handshake.CreateToken());
}

public sealed class PluginIdentityTests
{
    [Fact]
    public void PluginIdentity_AndInstanceId_AreDifferentThings()
    {
        var identity = new PluginIdentity("bluestone-reference", "m", "p", "P", "V", PluginKind.Instrument, "1");

        Assert.Equal(identity, new PluginIdentity("bluestone-reference", "m", "p", "P", "V", PluginKind.Instrument, "1"));
        Assert.NotEqual(PluginInstanceId.New(), PluginInstanceId.New());
    }

    [Fact]
    public void ParameterDescriptor_DefaultOutsideZeroToOne_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParameterDescriptor(0, "x", 1.01));

    [Fact]
    public void PluginStateData_ComparesByContent() =>
        Assert.Equal(new PluginStateData("f", [1, 2]), new PluginStateData("f", ImmutableArray.Create<byte>(1, 2)));
}
