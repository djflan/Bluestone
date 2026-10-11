using Bluestone.Audio.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Audio;

public sealed class PluginScheduleTests
{
    [Fact]
    public void HasSameContent_ComparesEventsAndParameters_NotInstances()
    {
        var a = new PluginSchedule([new(0, PluginEvent.NoteOn(0, 0, 60, 100)), new(100, PluginEvent.NoteOff(0, 0, 60))], [new(50, 1, 0.5)]);
        var same = new PluginSchedule([new(0, PluginEvent.NoteOn(0, 0, 60, 100)), new(100, PluginEvent.NoteOff(0, 0, 60))], [new(50, 1, 0.5)]);
        var louder = new PluginSchedule([new(0, PluginEvent.NoteOn(0, 0, 60, 101)), new(100, PluginEvent.NoteOff(0, 0, 60))], [new(50, 1, 0.5)]);
        var moved = new PluginSchedule([new(0, PluginEvent.NoteOn(0, 0, 60, 100)), new(100, PluginEvent.NoteOff(0, 0, 60))], [new(51, 1, 0.5)]);

        Assert.True(a.HasSameContent(same));
        Assert.False(a.HasSameContent(louder));
        Assert.False(a.HasSameContent(moved));
    }

    [Fact]
    public void Sounds_FollowsTheLastNoteOnOrOffBeforeTheFrame_PerChannelAndNote()
    {
        var schedule = new PluginSchedule(
            [
                new(0, PluginEvent.NoteOn(0, 0, 60, 100)),
                new(10, PluginEvent.NoteOn(0, 1, 60, 100)),
                new(100, PluginEvent.NoteOff(0, 0, 60)),
                new(100, PluginEvent.NoteOn(0, 0, 60, 90)),
                new(200, PluginEvent.NoteOn(0, 0, 60, 0)),
                new(300, PluginEvent.ControlChange(0, 0, 7, 100)),
            ],
            []);
        var c0 = PluginSchedule.SlotOf(PluginEvent.NoteOn(0, 0, 60, 1));
        var c1 = PluginSchedule.SlotOf(PluginEvent.NoteOn(0, 1, 60, 1));

        Assert.False(schedule.Sounds(c0, 0));
        Assert.True(schedule.Sounds(c0, 1));
        Assert.True(schedule.Sounds(c0, 101));
        Assert.True(schedule.Sounds(c0, 200));
        Assert.False(schedule.Sounds(c0, 201));
        Assert.True(schedule.Sounds(c1, 1_000));
        Assert.False(schedule.Sounds(PluginSchedule.SlotOf(PluginEvent.NoteOn(0, 0, 61, 1)), 1_000));
    }
}
