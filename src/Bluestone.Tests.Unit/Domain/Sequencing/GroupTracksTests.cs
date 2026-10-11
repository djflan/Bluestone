using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

/// <summary>Group membership and the mute and solo rule with groups (ADR 0030).</summary>
public sealed class GroupTracksTests
{
    private static Track Plain(string name, bool muted = false, bool soloed = false) => new(TrackId.New(), name, [], muted, soloed);

    private static Track Group(string name, bool muted = false, bool soloed = false) => new(TrackId.New(), name, [], muted, soloed, role: TrackRole.Group);

    private static Sequence Of(params Track[] tracks)
    {
        var empty = Sequence.CreateEmpty(Ppqn.Default);
        return new Sequence(empty.TempoMap, empty.MeterMap, tracks, []);
    }

    private static bool Plays(Sequence sequence, Track track) => GroupTracks.Playing(sequence)[sequence.Tracks.IndexOf(sequence.FindTrack(track.Id)!)];

    [Fact]
    public void Playing_WithoutGroups_IsTheOldRule_SoloOverridesMute()
    {
        var a = Plain("a");
        var muted = Plain("muted", muted: true);
        var soloedAndMuted = Plain("soloed", muted: true, soloed: true);

        var noSolo = Of(a, muted);
        var withSolo = Of(a, muted, soloedAndMuted);

        Assert.Equal([true, false], GroupTracks.Playing(noSolo));
        Assert.Equal([false, false, true], GroupTracks.Playing(withSolo));
    }

    [Fact]
    public void AMutedGroup_SilencesItsMembers_AtAnyDepth()
    {
        var outer = Group("outer", muted: true);
        var inner = Group("inner").WithGroup(outer.Id);
        var member = Plain("member").WithGroup(inner.Id);
        var outside = Plain("outside");
        var sequence = Of(outer, inner, member, outside);

        Assert.False(Plays(sequence, member));
        Assert.False(Plays(sequence, inner));
        Assert.True(Plays(sequence, outside));
        Assert.True(GroupTracks.IsMutedByGroup(sequence, member));
        Assert.False(GroupTracks.IsMutedByGroup(sequence, outside));
    }

    [Fact]
    public void SoloingAGroup_SolosItsMembers_AndSilencesEverythingElse()
    {
        var group = Group("group", soloed: true);
        var member = Plain("member").WithGroup(group.Id);
        var outside = Plain("outside");
        var sequence = Of(group, member, outside);

        Assert.True(Plays(sequence, group));
        Assert.True(Plays(sequence, member));
        Assert.False(Plays(sequence, outside));
    }

    [Fact]
    public void ASoloedMember_OfAMutedGroup_Plays_BecauseTheNearerStateWins()
    {
        var group = Group("group", muted: true);
        var soloed = Plain("soloed", soloed: true).WithGroup(group.Id);
        var other = Plain("other").WithGroup(group.Id);
        var sequence = Of(group, soloed, other);

        Assert.True(Plays(sequence, soloed));
        Assert.False(Plays(sequence, other));
        Assert.False(GroupTracks.IsMutedByGroup(sequence, soloed));
    }

    [Fact]
    public void AMutedMember_OfASoloedGroup_StaysSilent()
    {
        var group = Group("group", soloed: true);
        var muted = Plain("muted", muted: true).WithGroup(group.Id);
        var sequence = Of(group, muted);

        Assert.False(Plays(sequence, muted));
        Assert.False(GroupTracks.IsMutedByGroup(sequence, muted));
    }

    [Fact]
    public void AMutedGroupInsideASoloedOne_SilencesItsOwnMembers()
    {
        var outer = Group("outer", soloed: true);
        var inner = Group("inner", muted: true).WithGroup(outer.Id);
        var deep = Plain("deep").WithGroup(inner.Id);
        var shallow = Plain("shallow").WithGroup(outer.Id);
        var sequence = Of(outer, inner, deep, shallow);

        Assert.False(Plays(sequence, deep));
        Assert.True(Plays(sequence, shallow));
    }

    [Fact]
    public void AMissingGroup_OrALoopOfGroups_EndsTheWalk()
    {
        var orphan = Plain("orphan").WithGroup(TrackId.New());
        var a = Group("a", muted: true);
        var b = Group("b").WithGroup(a.Id);
        var looped = Of(a.WithGroup(b.Id), b, orphan);

        Assert.True(Plays(looped, orphan));
        Assert.Single(GroupTracks.Ancestors(looped, looped.Tracks[1]));
        Assert.False(Plays(looped, b));
    }

    [Fact]
    public void MembersAndDepth_FollowMembership()
    {
        var outer = Group("outer");
        var inner = Group("inner").WithGroup(outer.Id);
        var member = Plain("member").WithGroup(inner.Id);
        var sequence = Of(outer, inner, member);

        Assert.Equal([inner.Id], GroupTracks.Members(sequence, outer.Id).Select(t => t.Id));
        Assert.Equal(2, GroupTracks.Depth(sequence, member));
        Assert.True(GroupTracks.IsInside(sequence, member, outer.Id));
        Assert.False(GroupTracks.IsInside(sequence, outer, member.Id));
    }

    [Fact]
    public void Folding_IsKeptByEveryOtherEdit()
    {
        var group = Group("group").WithFolded(true);

        Assert.True(group.WithName("renamed").WithMuted(true).WithSoloed(true).IsFolded);
        Assert.False(group.WithFolded(false).IsFolded);
    }
}
