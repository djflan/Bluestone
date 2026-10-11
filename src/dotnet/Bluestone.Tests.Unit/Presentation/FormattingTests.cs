using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;
using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(60, "C4")]
    [InlineData(0, "C-1")]
    [InlineData(127, "G9")]
    [InlineData(61, "C♯4")]
    public void NoteName_UsesScientificPitch(int note, string expected) =>
        Assert.Equal(expected, Formatting.NoteName(new NoteNumber(note)));

    [Fact]
    public void PositionAndTime_AreFixedWidth()
    {
        Assert.Equal("12.3.005", Formatting.Position(new BarBeatTick(12, 3, 5)));
        Assert.Equal("1:02.050", Formatting.Time(TimeSpan.FromMilliseconds(62_050)));
    }

    [Theory]
    [InlineData(new byte[] { 0x90, 0x3C, 0x64 }, "Note On · Ch 1 · C4 · vel 100")]
    [InlineData(new byte[] { 0x80, 0x3C, 0x40 }, "Note Off · Ch 1 · C4")]
    [InlineData(new byte[] { 0x99, 0x24, 0x00 }, "Note Off · Ch 10 · C2")]
    [InlineData(new byte[] { 0xB2, 0x07, 0x64 }, "CC 7 · Ch 3 · 100")]
    [InlineData(new byte[] { 0xC0, 0x21 }, "Program 34 · Ch 1")]
    [InlineData(new byte[] { 0xE0, 0x00, 0x40 }, "Pitch Bend · Ch 1 · 0")]
    [InlineData(new byte[] { 0xF8 }, "F8")]
    public void Message_DescribesMidi(byte[] bytes, string expected) =>
        Assert.Equal(expected, Formatting.Message(bytes));

    [Fact]
    public void Message_NeverRevealsSysExPayload()
    {
        var known = Formatting.Message([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7]);
        var unknown = Formatting.Message([0xF0, 0x7D, 0x4C, 0x4D, 0xF7]);

        Assert.Equal("SysEx · XG System On · 9 bytes", known);
        Assert.DoesNotContain("4C", known, StringComparison.Ordinal);
        Assert.Equal("SysEx · 5 bytes · manufacturer 7D", unknown);
        Assert.DoesNotContain("4C", unknown, StringComparison.Ordinal);
    }
}
