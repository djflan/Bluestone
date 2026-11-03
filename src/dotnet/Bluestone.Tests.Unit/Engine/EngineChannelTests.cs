using System.Buffers.Binary;
using Bluestone.Engine.Protocol;
using CsCheck;

namespace Bluestone.Tests.Unit.Engine;

/// <summary>The engine's control channel (ADR 0044): every message round-trips, and anything malformed is refused as a protocol error.</summary>
public sealed class EngineChannelTests
{
    public static TheoryData<EngineMessage> Messages() =>
    [
        new Hello(EngineLimits.Version, "0123456789ABCDEF", 4242, Restarted: true),
        new Welcome(EngineLimits.Version),
        new Ready(),
        new Heartbeat(17, new EngineTelemetry(3, TimeSpan.FromMilliseconds(2.5), 123_456_789, 98_765_432, 0)),
        new Ping(-5),
        new Pong(long.MaxValue),
        new Shutdown(),
        new Fault("Plugin node threw: Ünïcode ♪"),
        new TestCommand(TestAction.Hang),
    ];

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

        Assert.Equal(message, await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManyMessages_FromSeveralThreads_ArriveWhole()
    {
        using var stream = new MemoryStream();
        using (var writer = new EngineChannel(stream))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(async () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    await writer.WriteAsync(new Ping((t * 1000) + i), TestContext.Current.CancellationToken);
                }
            }, TestContext.Current.CancellationToken)));
        }

        stream.Position = 0;
        using var reader = new EngineChannel(stream);
        var ids = new List<long>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken) is Ping ping)
        {
            ids.Add(ping.Id);
        }

        Assert.Equal(Enumerable.Range(0, 8).SelectMany(t => Enumerable.Range(0, 100).Select(i => (long)((t * 1000) + i))).Order(), ids.Order());
    }

    [Theory]
    [InlineData(new byte[] { 0x99, 0x00 }, "Unknown message type")]
    [InlineData(new byte[] { 0x03, 0x00, 0x01 }, "left over")]
    [InlineData(new byte[] { 0x05, 0x00, 0x01, 0x02 }, "ends before")]
    [InlineData(new byte[] { 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02 }, "not a boolean")]
    [InlineData(new byte[] { 0x08, 0x00, 0x02, 0x00, 0xC3, 0x28 }, "not UTF-8")]
    [InlineData(new byte[] { 0x09, 0x00, 0x09 }, "Unknown test action")]
    public void MalformedBodies_AreProtocolErrors(byte[] body, string why)
    {
        var error = Assert.Throws<EngineProtocolException>(() => EngineChannel.Decode(body));
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTelemetry_IsAProtocolError()
    {
        var body = new byte[64];
        var length = EngineChannel.Encode(new Heartbeat(1, new EngineTelemetry(1, TimeSpan.Zero, 0, 0, 0)), body);
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(2 + 8 + 4), -1);

        Assert.Throws<EngineProtocolException>(() => EngineChannel.Decode(body.AsSpan(0, length)));
    }

    [Fact]
    public async Task AnOversizedOrTruncatedFrame_IsAProtocolError()
    {
        using var oversized = new MemoryStream(BitConverter.GetBytes(EngineLimits.MaxFrameBytes + 1));
        using var reader = new EngineChannel(oversized);
        await Assert.ThrowsAsync<EngineProtocolException>(() => reader.ReadAsync(TestContext.Current.CancellationToken));

        using var truncated = new MemoryStream([10, 0, 0, 0, 0x05, 0x00, 0x01]);
        using var partial = new EngineChannel(truncated);
        await Assert.ThrowsAsync<EngineProtocolException>(() => partial.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnyBody_DecodesOrIsAProtocolError_NeverAnotherFailure() =>
        Gen.Byte.Array[0, 64].Sample(body =>
        {
            try
            {
                _ = EngineChannel.Decode(body);
            }
            catch (EngineProtocolException)
            {
            }
        }, iter: 20_000);

    [Fact]
    public void AStringLongerThanTheLimit_IsRefusedWhenWritten() =>
        Assert.Throws<ArgumentException>(() => EngineChannel.Encode(new Fault(new string('x', EngineLimits.MaxStringBytes + 1)), new byte[EngineLimits.MaxFrameBytes]));
}
