using Bluestone.Application.Editing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Application;

/// <summary>Commands that put events into clips, record takes as clips, and move or copy clips.</summary>
public sealed class ClipCommandsTests
{
    private static readonly Ppqn Ppqn = new(480);
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, long length = 240) =>
        new(new Tick(at), new TickSpan(length), One, NoteNumber.MiddleC, new Velocity(100));

    // Events are at content positions; the clip has no trim.
    private static NoteClip Clip(long start, long length, params TrackEvent[] events) =>
        new(ClipId.New(), new Tick(start), new TickSpan(length), TickSpan.Zero, new EventList(events));

    private static Project With(params Clip[] clips) =>
        Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn).WithTrack(new Track(TrackId.New(), "t", clips)) };

    private static Track TrackOf(Project project) => project.Sequence.Tracks[0];

    private static List<(long Start, long End)> Bounds(Project project) =>
        [.. TrackOf(project).Clips.Select(c => (c.Start.Value, c.End.Value))];

    [Fact]
    public void EditEvents_GrowsTheClipToBarLines_ButNotIntoTheNextClip()
    {
        var first = Clip(0, 1920);
        var project = With(first, Clip(3840, 1920));
        var id = TrackOf(project).Id;

        var grown = ProjectCommands.AddEvent(id, first.Id, Note(2000)).Apply(project);
        Assert.Equal([(0L, 3840L), (3840L, 5760L)], Bounds(grown));

        var clamped = ProjectCommands.AddEvent(id, first.Id, Note(3000, length: 2000)).Apply(project);
        Assert.Equal([(0L, 3840L), (3840L, 5760L)], Bounds(clamped));
        // The note is kept whole, but plays only up to the clip's end.
        var note = Assert.IsType<NoteEvent>(Assert.Single(TrackOf(clamped).ArrangedEvents));
        Assert.Equal(840, note.Duration.Value);
        Assert.Equal(2000, ((NoteEvent)((NoteClip)TrackOf(clamped).Clips[0]).Content.Items[0]).Duration.Value);
    }

    [Fact]
    public void EditEvents_WithANewClipId_CreatesAClipInTheGap()
    {
        var project = With(Clip(0, 1000));
        var id = TrackOf(project).Id;
        var clip = ClipId.New();

        var created = ProjectCommands.AddEvent(id, clip, Note(2000)).Apply(project);

        var added = Assert.IsType<NoteClip>(TrackOf(created).FindClip(clip));
        Assert.Equal((1920L, 3840L), (added.Start.Value, added.End.Value));
        Assert.Equal([2000L], TrackOf(created).ArrangedEvents.Select(e => e.Position.Value));

        // Starting right after another clip, the new clip begins where that one ends.
        var squeezed = ProjectCommands.AddEvent(id, ClipId.New(), Note(1100)).Apply(project);
        Assert.Equal([(0L, 1000L), (1000L, 1920L)], Bounds(squeezed));
    }

    [Fact]
    public void EditEvents_WithANewClipId_WhereAClipIs_AddsToThatClip()
    {
        var existing = Clip(0, 1920);
        var project = With(existing);

        var edited = ProjectCommands.AddEvent(TrackOf(project).Id, ClipId.New(), Note(100)).Apply(project);

        Assert.Equal(existing.Id, Assert.Single(TrackOf(edited).Clips).Id);
        Assert.Single(TrackOf(edited).ArrangedEvents);
    }

    [Fact]
    public void RemoveEvents_FindsThemInAnyClip()
    {
        var a = Note(0);
        var b = Note(0);
        var project = With(Clip(0, 480, a), Clip(480, 480, b));

        var removed = ProjectCommands.RemoveEvents(TrackOf(project).Id, [a.Id, b.Id]).Apply(project);

        Assert.Empty(TrackOf(removed).ArrangedEvents);
        Assert.Equal(2, TrackOf(removed).Clips.Length);
        Assert.Same(project, ProjectCommands.RemoveEvents(TrackOf(project).Id, [EventId.New()]).Apply(project));
    }

    [Fact]
    public void EditEvents_BetweenClips_GoToTheEditedClipOnlyNextToIt()
    {
        var edited = Clip(0, 1920);
        var project = With(edited, Clip(3840, 1920));
        var id = TrackOf(project).Id;

        // Past the next clip: a new clip in that gap, not hidden content of the edited clip.
        var far = ProjectCommands.AddEvent(id, edited.Id, Note(8000)).Apply(project);
        Assert.Equal([(0L, 1920L), (3840L, 5760L), (7680L, 9600L)], Bounds(far));
        Assert.Equal([8000L], TrackOf(far).ArrangedEvents.Select(e => e.Position.Value));
        Assert.NotEqual(edited.Id, TrackOf(far).Clips[2].Id);
    }

    [Fact]
    public void EditEvents_MovingAnEventIntoAnotherClip_MovesItThere()
    {
        var note = Note(100);
        var from = Clip(0, 1920, note);
        var into = Clip(3840, 1920);
        var project = With(from, into);

        var moved = ProjectCommands.ReplaceEvent(TrackOf(project).Id, from.Id, note with { Position = new Tick(4000) }).Apply(project);

        Assert.Empty(((NoteClip)TrackOf(moved).Clips[0]).Content.Items);
        Assert.Equal(note.Id, Assert.Single(((NoteClip)TrackOf(moved).Clips[1]).Content.Items).Id);
        Assert.Equal([4000L], TrackOf(moved).ArrangedEvents.Select(e => e.Position.Value));
    }

    [Fact]
    public void EditEvents_InSpaceAClipGrowsInto_GoToThatClip()
    {
        // The first note grows its clip to the bar line, over where the second starts.
        var clip = Clip(0, 1000);
        var project = With(clip);

        var edited = ProjectCommands.AddEvents(TrackOf(project).Id, ClipId.New(), "Paste", [Note(500, length: 1000), Note(1200)]).Apply(project);

        Assert.Equal([(0L, 1920L)], Bounds(edited));
        Assert.Equal(1000, ((NoteEvent)TrackOf(edited).ArrangedEvents[0]).Duration.Value);
        Assert.Equal(2, TrackOf(edited).ArrangedEvents.Length);
    }

    [Fact]
    public void EditEvents_OnHiddenContent_KeepItHiddenInItsClip()
    {
        // A trimmed clip still holds a note at 4000, under the next clip.
        var hidden = Note(4000);
        var trimmed = Clip(0, 1920, hidden);
        var next = Clip(3840, 1920);
        var project = With(trimmed, next);

        var edited = ProjectCommands.ReplaceEvent(TrackOf(project).Id, trimmed.Id, hidden with { Velocity = new Velocity(5) }).Apply(project);

        Assert.Equal(hidden.Id, Assert.Single(((NoteClip)TrackOf(edited).Clips[0]).Content.Items).Id);
        Assert.Empty(TrackOf(edited).ArrangedEvents);
    }

    [Fact]
    public void EditEvents_ThatDoNotMoveEvents_LeaveATrimmedClipTrimmed()
    {
        // Nothing after the clip would stop it growing; a velocity change still must not un-trim it.
        var hidden = Note(4000);
        var cut = Note(1800, length: 1000);
        var trimmed = Clip(0, 1920, hidden, cut);
        var project = With(trimmed);
        var id = TrackOf(project).Id;

        var edited = ProjectCommands.ReplaceEvents(id, trimmed.Id, "Change Velocity", [hidden with { Velocity = new Velocity(5) }, cut with { Velocity = new Velocity(5) }]).Apply(project);
        Assert.Equal([(0L, 1920L)], Bounds(edited));

        // Making the cut note longer is a change the user wants to hear, so the clip grows for it.
        var longer = ProjectCommands.ReplaceEvent(id, trimmed.Id, cut with { Duration = new TickSpan(1500) }).Apply(project);
        Assert.Equal([(0L, 3840L)], Bounds(longer));
    }

    [Fact]
    public void EditEvents_WithoutAnEditedClip_NameTheFirstNewClip()
    {
        var existing = Clip(0, 1920);
        var project = With(existing);
        var id = ClipId.New();

        // The paste starts inside the existing clip and continues into free space.
        var edited = ProjectCommands.AddEvents(TrackOf(project).Id, id, "Paste", [Note(100), Note(5000)]).Apply(project);

        Assert.Equal([existing.Id, id], TrackOf(edited).Clips.Select(c => c.Id));
    }

    [Fact]
    public void Record_Overdub_AddsEachEventToTheClipWhereItStarts()
    {
        var a = Note(100);
        var b = Note(100);
        var project = With(Clip(0, 1920, a), Clip(1920, 1920, b), Clip(5760, 1920));
        var take = Note(1800, length: 400);
        var late = Note(4000);

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [take, late], null).Apply(project);

        // Nothing is joined or trimmed. The late event starts in a gap the take's first clip does not
        // border, so it gets a clip of its own there.
        Assert.Equal([(0L, 1920L), (1920L, 3840L), (3840L, 5760L), (5760L, 7680L)], Bounds(recorded));
        Assert.Equal([a.Id, take.Id, b.Id, late.Id], TrackOf(recorded).ArrangedEvents.Select(e => e.Id));
        Assert.Equal(400, ((NoteEvent)((NoteClip)TrackOf(recorded).Clips[0]).Content.Items[1]).Duration.Value);
    }

    [Fact]
    public void Record_Overdub_IntoSpace_MakesABarAlignedClip()
    {
        var project = With();

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [Note(2000)], null).Apply(project);

        Assert.Equal([(1920L, 3840L)], Bounds(recorded));
    }

    [Fact]
    public void Record_Replace_RemovesWhatPlaysInTheRange_ButKeepsMetaEvents()
    {
        var before = Note(100);
        var inside = Note(2000);
        var meta = new MetaEvent(EventId.New(), new Tick(2500), 0x06, ByteBlock.Copy("Chorus"u8));
        var project = With(Clip(0, 3840, before, inside, meta));
        var pickup = Note(1900, length: 200);
        var take = Note(2100);

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [pickup, take], new TickRange(new Tick(1920), new Tick(3840))).Apply(project);

        Assert.Equal([(0L, 3840L)], Bounds(recorded));
        Assert.Equal([before.Id, pickup.Id, take.Id, meta.Id], TrackOf(recorded).ArrangedEvents.Select(e => e.Id));
    }

    [Fact]
    public void Record_ReplaceWithNothing_StillEmptiesTheRange()
    {
        var project = With(Clip(0, 3840, Note(100), Note(2000)));

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [], new TickRange(new Tick(1920), new Tick(3840))).Apply(project);

        Assert.Equal([(0L, 3840L)], Bounds(recorded));
        Assert.Single(TrackOf(recorded).ArrangedEvents);
    }

    [Fact]
    public void RenameClip_TrimsAndBoundsTheName()
    {
        var clip = Clip(0, 100);
        var project = With(clip);
        var id = TrackOf(project).Id;

        var named = ClipCommands.RenameClip(id, clip.Id, "  Verse  ").Apply(project);
        Assert.Equal("Verse", TrackOf(named).Clips[0].Name);
        Assert.Equal("Rename Clip", ClipCommands.RenameClip(id, clip.Id, "x").Label);
        Assert.Same(named, ClipCommands.RenameClip(id, clip.Id, "Verse").Apply(named));
        Assert.Equal(Bluestone.Domain.Sequencing.Clip.MaxNameLength, TrackOf(ClipCommands.RenameClip(id, clip.Id, new string('a', 300)).Apply(project)).Clips[0].Name.Length);
        Assert.Empty(TrackOf(ClipCommands.RenameClip(id, clip.Id, " ").Apply(named)).Clips[0].Name);
    }

    [Fact]
    public void MoveClips_StopsAtTickZeroAndMakesRoom()
    {
        var first = Clip(1000, 500);
        var second = Clip(2000, 500);
        var project = With(first, second);
        var id = TrackOf(project).Id;

        var moved = ClipCommands.MoveClips(id, [first.Id], -5000).Apply(project);
        Assert.Equal([(0L, 500L), (2000L, 2500L)], Bounds(moved));

        var onto = ClipCommands.MoveClips(id, [first.Id], 800).Apply(project);
        Assert.Equal([(1800L, 2300L), (2300L, 2500L)], Bounds(onto));
        Assert.Equal("Move Clip", ClipCommands.MoveClips(id, [first.Id], 1).Label);
    }

    [Fact]
    public void MoveClips_TogetherDoNotClearEachOther()
    {
        var first = Clip(0, 500);
        var second = Clip(500, 500);
        var project = With(first, second);

        var moved = ClipCommands.MoveClips(TrackOf(project).Id, [first.Id, second.Id], 250).Apply(project);

        Assert.Equal([(250L, 750L), (750L, 1250L)], Bounds(moved));
        Assert.Equal([first.Id, second.Id], TrackOf(moved).Clips.Select(c => c.Id));
    }

    [Fact]
    public void CopyClips_AddsCopiesWithNewIds()
    {
        var note = Note(0);
        var clip = Clip(0, 500, note);
        var project = With(clip);

        var copied = ClipCommands.CopyClips(TrackOf(project).Id, [clip.Id], 1000).Apply(project);

        Assert.Equal([(0L, 500L), (1000L, 1500L)], Bounds(copied));
        Assert.NotEqual(clip.Id, TrackOf(copied).Clips[1].Id);
        Assert.NotEqual(note.Id, TrackOf(copied).ArrangedEvents[1].Id);
        Assert.Equal("Copy Clips", ClipCommands.CopyClips(TrackOf(project).Id, [clip.Id, ClipId.New()], 1).Label);
        Assert.Same(project, ClipCommands.CopyClips(TrackOf(project).Id, [clip.Id], 0).Apply(project));
    }

    [Fact]
    public void MoveClips_ToAnotherTrack_MakesRoomThere()
    {
        var moving = Clip(0, 1000, Note(0));
        var project = With(moving);
        var other = new Track(TrackId.New(), "u", [Clip(500, 1000)]);
        project = project with { Sequence = project.Sequence.WithTrack(other) };

        var moved = ClipCommands.MoveClips(TrackOf(project).Id, [moving.Id], 0, other.Id).Apply(project);

        Assert.Empty(moved.Sequence.Tracks[0].Clips);
        Assert.Equal([(0L, 1000L), (1000L, 1500L)], moved.Sequence.Tracks[1].Clips.Select(c => (c.Start.Value, c.End.Value)));
        Assert.Equal(moving.Id, moved.Sequence.Tracks[1].Clips[0].Id);
    }

    [Fact]
    public void SplitClips_SplitsOnlyClipsTheTickIsInside()
    {
        var a = Clip(0, 1000);
        var b = Clip(1000, 1000);
        var project = With(a, b);

        var split = ClipCommands.SplitClips(TrackOf(project).Id, [a.Id, b.Id], new Tick(500)).Apply(project);

        Assert.Equal([(0L, 500L), (500L, 1000L), (1000L, 2000L)], Bounds(split));
        Assert.Same(project, ClipCommands.SplitClips(TrackOf(project).Id, [a.Id], new Tick(1000)).Apply(project));
    }

    [Fact]
    public void ResizeClip_StopsAtNeighboursAndKeepsATick()
    {
        var a = Clip(0, 1000);
        var b = Clip(2000, 1000);
        var project = With(a, b);
        var id = TrackOf(project).Id;

        Assert.Equal([(0L, 2000L), (2000L, 3000L)], Bounds(ClipCommands.ResizeClip(id, a.Id, Tick.Zero, new Tick(9000)).Apply(project)));
        Assert.Equal([(0L, 1000L), (1000L, 3000L)], Bounds(ClipCommands.ResizeClip(id, b.Id, Tick.Zero, new Tick(3000)).Apply(project)));
        Assert.Equal([(0L, 1L), (2000L, 3000L)], Bounds(ClipCommands.ResizeClip(id, a.Id, Tick.Zero, Tick.Zero).Apply(project)));
        Assert.Same(project, ClipCommands.ResizeClip(id, a.Id, Tick.Zero, new Tick(1000)).Apply(project));
    }

    [Fact]
    public void DuplicateClips_RepeatsTheSelectionAfterItself()
    {
        var a = Clip(0, 1000);
        var b = Clip(1500, 500);
        var project = With(a, b);

        var duplicated = ClipCommands.DuplicateClips(TrackOf(project).Id, [a.Id, b.Id]).Apply(project);

        Assert.Equal([(0L, 1000L), (1500L, 2000L), (2000L, 3000L), (3500L, 4000L)], Bounds(duplicated));
    }

    [Fact]
    public void DeleteAndCreateClips()
    {
        var a = Clip(0, 1000);
        var project = With(a, Clip(3000, 1000));
        var id = TrackOf(project).Id;

        Assert.Equal([(3000L, 4000L)], Bounds(ClipCommands.DeleteClips(id, [a.Id]).Apply(project)));
        Assert.Equal([(0L, 1000L), (1000L, 3000L), (3000L, 4000L)], Bounds(ClipCommands.CreateClip(id, NoteClip.Create(new Tick(1000), new TickSpan(5000))).Apply(project)));
        Assert.Same(project, ClipCommands.CreateClip(id, NoteClip.Create(new Tick(500), new TickSpan(100))).Apply(project));
    }
}
