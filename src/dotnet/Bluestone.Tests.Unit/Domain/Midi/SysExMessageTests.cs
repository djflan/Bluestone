using Bluestone.Domain.Midi;

namespace Bluestone.Tests.Unit.Domain.Midi;

public sealed class SysExMessageTests
{
    private static readonly byte[] XgSystemOn = [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7];

    [Fact]
    public void Create_AcceptsWellFormedMessage()
    {
        var message = SysExMessage.Create(XgSystemOn);

        Assert.Equal(XgSystemOn, message.Bytes.ToArray());
        Assert.Equal([0x43], message.ManufacturerId.ToArray());
    }

    [Fact]
    public void ManufacturerId_UsesThreeBytesForExtendedIds() =>
        Assert.Equal([0x00, 0x20, 0x29], SysExMessage.Create([0xF0, 0x00, 0x20, 0x29, 0x01, 0xF7]).ManufacturerId.ToArray());

    [Theory]
    [InlineData(new byte[] { 0xF0 })]
    [InlineData(new byte[] { 0x43, 0x10, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x43, 0x10 })]
    [InlineData(new byte[] { 0xF0, 0x43, 0x90, 0xF7 })]
    [InlineData(new byte[] { 0xF0, 0x43, 0xF7, 0xF7 })]
    public void Create_RejectsMalformedMessages(byte[] bytes) =>
        Assert.Throws<FormatException>(() => SysExMessage.Create(bytes));

    [Fact]
    public void Create_RejectsOversizedMessages()
    {
        var bytes = new byte[SysExMessage.MaxLength + 1];
        bytes[0] = 0xF0;
        bytes[^1] = 0xF7;

        Assert.False(SysExMessage.TryCreate(bytes, out _, out var error));
        Assert.Contains("exceed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_CopiesInput()
    {
        var bytes = (byte[])XgSystemOn.Clone();
        var message = SysExMessage.Create(bytes);
        bytes[1] = 0x41;

        Assert.Equal(0x43, message.Bytes[1]);
    }

    [Fact]
    public void ToString_DoesNotRevealPayload()
    {
        var text = SysExMessage.Create(XgSystemOn).ToString();

        Assert.Equal("SysEx (9 bytes, manufacturer 43)", text);
        Assert.DoesNotContain("4C", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(SysExMessage.Create(XgSystemOn), SysExMessage.Create(XgSystemOn));
        Assert.Equal(ByteBlock.Copy([1, 2, 3]), ByteBlock.Copy([1, 2, 3]));
        Assert.NotEqual(ByteBlock.Copy([1, 2, 3]), ByteBlock.Copy([1, 2]));
        Assert.Equal(ByteBlock.Copy([1, 2, 3]).GetHashCode(), ByteBlock.Copy([1, 2, 3]).GetHashCode());
    }
}
