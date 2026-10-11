using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Playback;
using static Bluestone.Tests.Unit.Playback.PlaybackFixture;

namespace Bluestone.Tests.Unit.Playback;

public sealed class PlaybackPlanCompilerTests
{
    // Each route is written into the routing model the way the track inspector writes it, then compiled
    // through the signal graph like the controller does.
    private static PlaybackPlan Compile(Sequence sequence, params (Track Track, Output Output)[] routes) =>
        PlanDump.Compile(routes.Aggregate(Project.CreateNew("t") with { Sequence = sequence }, (project, route) =>
            TrackOutputs.Write(project, route.Track.Id, new TrackOutput { Endpoint = PlanDump.SlotReference(route.Output.Slot), Channel = route.Output.Channel, Transpose = route.Output.Transpose })));

    private static Sequence With(params Track[] tracks) => tracks.Aggregate(Sequence.CreateEmpty(Resolution), (s, t) => s.WithTrack(t));

    [Fact]
    public void MutedTracksAreSkipped_SoloOverridesMute()
    {
        var muted = Track.FromEvents(TrackId.New(), "m", [Note(0, 10)], isMuted: true);
        var plain = Track.FromEvents(TrackId.New(), "p", [Note(0, 10)]);
        var soloMuted = Track.FromEvents(TrackId.New(), "s", [Note(0, 10)], isMuted: true, isSoloed: true);

        Assert.Equal(1, Compile(With(muted, plain), (muted, new(0)), (plain, new(0))).EventCount);
        Assert.Equal(1, Compile(With(muted, plain, soloMuted), (muted, new(0)), (plain, new(0)), (soloMuted, new(0))).EventCount);
    }

    [Fact]
    public void UnboundTracks_AreReported()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 10)]);

        var plan = Compile(With(track));

        Assert.Equal(0, plan.EventCount);
        Assert.Equal(track.Id, Assert.Single(plan.Diagnostics).Track);
    }

    [Fact]
    public void ChannelOverride_ReaddressesEveryChannelMessage()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 10), Cc(0, 7, 1, channel: 3)]);

        var plan = Compile(With(track), (track, new(0, MidiChannel.FromNumber(10))));

        Assert.All(plan.Events, e => Assert.Equal(9, e.Message.Channel.Index));
    }

    [Fact]
    public void Events_AreOrderedByTickPhaseTrackThenPosition()
    {
        var a = Track.FromEvents(TrackId.New(), "a", [Note(0, 10), Cc(0, 7, 1)]);
        var b = Track.FromEvents(TrackId.New(), "b", [Cc(0, 7, 2), Program(0, 1)]);

        var plan = Compile(With(a, b), (a, new(0)), (b, new(1)));

        Assert.Equal(["C0 1 1", "B0 0 7", "B0 1 7", "90 0 3C"], plan.Events.Select(e => $"{e.Message.Status:X2} {e.Slot} {e.Message.Data1:X}"));
    }

    [Fact]
    public void RawEvents_AreSentWhenCompleteOrJoinedWhenSplit()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [
            Raw(0, 0xF8),
            Raw(10, 0xF0, 0x43, 0x10),
            Raw(12, 0x4C, 0x00, 0xF7),
            Raw(20, 0x4C),
        ]);

        var plan = Compile(With(track), (track, new(0)));

        Assert.Equal([0L, 10L], plan.Events.Select(e => e.Tick));
        Assert.Equal([0xF0, 0x43, 0x10, 0x4C, 0x00, 0xF7], plan.Payloads[plan.Events[1].PayloadIndex].ToArray());
        Assert.Single(plan.Diagnostics);
    }

    [Fact]
    public void UnterminatedSplitSysEx_IsReportedNotSent()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Raw(0, 0xF0, 0x43), Raw(1, 0x10)]);

        var plan = Compile(With(track), (track, new(0)));

        Assert.Equal(0, plan.EventCount);
        Assert.Single(plan.Diagnostics);
    }

    [Fact]
    public void MetaEvents_AreNotTransmitted()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [new MetaEvent(EventId.New(), Tick.Zero, 1, ByteBlock.Copy("x"u8))]);

        Assert.Equal(0, Compile(With(track), (track, new(0))).EventCount);
    }

    private static AutomationLane VolumeRamp(int channel = 0) => new(
        AutomationLaneId.New(),
        AutomationTarget.ForController(MidiChannel.FromIndex(channel), ControllerNumber.ChannelVolume),
        [new AutomationPoint(Tick.Zero, ControlValue.Min), new AutomationPoint(new Tick(1000), ControlValue.FromSevenBit(127))]);

    private static Track WithLanes(Track track, params AutomationLane[] lanes) => track.WithAutomation(lanes);

    [Fact]
    public void Automation_IsSentInPlaceOfClipEventsOnItsTarget()
    {
        var track = WithLanes(Track.FromEvents(TrackId.New(), "t", [Note(0, 10), Cc(100, 7, 127), Cc(100, 11, 5)]), VolumeRamp());

        var plan = Compile(With(track), (track, new(0)));

        var volume = plan.Events.Where(e => e.Message.Status == 0xB0 && e.Message.Data1 == 7).ToList();
        // Sampled every 15 ticks at 500 PPQN, and only when the 7-bit value changes.
        Assert.Equal(68, volume.Count);
        Assert.Equal((0L, 0), (volume[0].Tick, (int)volume[0].Message.Data2));
        Assert.Equal((1000L, 127), (volume[^1].Tick, (int)volume[^1].Message.Data2));
        Assert.True(volume.Zip(volume.Skip(1)).All(p => p.Second.Message.Data2 > p.First.Message.Data2 && p.Second.Tick - p.First.Tick <= 15));
        Assert.Single(plan.Events, e => e.Message.Status == 0xB0 && e.Message.Data1 == 11);
        Assert.Contains("1 events in clips were replaced", Assert.Single(plan.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Automation_IsChasedOnSeek()
    {
        var track = WithLanes(Track.FromEvents(TrackId.New(), "t", [Note(0, 10)]), VolumeRamp());
        var plan = Compile(With(track), (track, new(0)));

        var chased = ChaseState.Compute(plan, 500).Single(m => m.Message.Data1 == 7);

        Assert.InRange(chased.Message.Data2, 61, 63);
        Assert.Equal(0, ChaseState.Compute(plan, 1).Single(m => m.Message.Data1 == 7).Message.Data2);
    }

    [Fact]
    public void Automation_FollowsTheChannelOverrideButNotTransposition()
    {
        var track = WithLanes(Track.FromEvents(TrackId.New(), "t", [Note(0, 10)]), VolumeRamp());

        var plan = Compile(With(track), (track, new(0, MidiChannel.FromNumber(5), Transpose: 12)));

        Assert.All(plan.Events.Where(e => e.Message.Status is >= 0xB0 and <= 0xBF), e => Assert.Equal((4, 7), (e.Message.Channel.Index, (int)e.Message.Data1)));
        Assert.Equal(0x48, plan.Events.Single(e => e.IsNote).Message.Data1);
    }

    [Fact]
    public void Automation_OnSilentTracks_IsNotSent()
    {
        var track = WithLanes(Track.FromEvents(TrackId.New(), "t", [], isMuted: true), VolumeRamp());

        Assert.Equal(0, Compile(With(track), (track, new(0))).EventCount);
    }

    private static RawMidiEvent Raw(long tick, params byte[] bytes) => new(EventId.New(), new Tick(tick), ByteBlock.Copy(bytes));

    private sealed record Output(int Slot, MidiChannel? Channel = null, int Transpose = 0);
}
