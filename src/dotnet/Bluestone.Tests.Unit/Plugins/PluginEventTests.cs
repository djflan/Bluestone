using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class PluginEventTests
{
    [Fact]
    public void ChannelEvents_HoldTheirFields()
    {
        var note = PluginEvent.NoteOn(12, 9, 36, 100);

        Assert.Equal((12, PluginEventKind.NoteOn, (byte)9, (byte)36, (byte)100), (note.SampleOffset, note.Kind, note.Channel, note.Data1, note.Data2));
        Assert.True(note.SystemExclusiveData.IsEmpty);
        Assert.Equal(PluginEvent.NoteOn(12, 9, 48, 100), note.WithData1(48));
        Assert.Equal(PluginEvent.NoteOn(0, 9, 36, 100), note.WithSampleOffset(0));
    }

    [Fact]
    public void PitchBend_SplitsFourteenBits()
    {
        var bend = PluginEvent.PitchBend(0, 0, 0x2001);

        Assert.Equal((byte)0x01, bend.Data1);
        Assert.Equal((byte)0x40, bend.Data2);
    }

    [Fact]
    public void SystemExclusive_UpToTheInlineLimit_IsHeld_AndLongerIsRefused()
    {
        var longest = Enumerable.Repeat((byte)0x10, PluginEvent.MaxSystemExclusiveBytes).ToArray();

        Assert.True(PluginEvent.TryCreateSystemExclusive(1, longest, out var held));
        Assert.Equal(longest, held.SystemExclusiveData.ToArray());
        Assert.False(PluginEvent.TryCreateSystemExclusive(1, new byte[PluginEvent.MaxSystemExclusiveBytes + 1], out _));
    }

    [Fact]
    public void Equality_ComparesSystemExclusivePayloads()
    {
        PluginEvent.TryCreateSystemExclusive(0, [0xF0, 1, 0xF7], out var a);
        PluginEvent.TryCreateSystemExclusive(0, [0xF0, 1, 0xF7], out var b);
        PluginEvent.TryCreateSystemExclusive(0, [0xF0, 2, 0xF7], out var c);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void OutOfRangeValues_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginEvent.NoteOn(0, 16, 60, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginEvent.NoteOn(0, 0, 128, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginEvent.ControlChange(-1, 0, 7, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginEvent.PitchBend(0, 0, 16384));
    }
}
