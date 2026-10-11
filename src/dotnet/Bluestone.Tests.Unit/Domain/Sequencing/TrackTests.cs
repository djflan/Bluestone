using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

public sealed class TrackTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, int note = 60, long length = 10) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber(note), new Velocity(100));

    private static ControllerEvent Cc(long at, ControllerNumber controller, int value = 0) =>
        new(new Tick(at), One, controller, ControlValue.FromSevenBit(value));

    private static ProgramEvent Program(long at, int program) =>
        new(new Tick(at), One, new ProgramSelection(new ProgramNumber(program)));

    // Events are at content positions; the clip has no trim.
    private static NoteClip Clip(long start, long length, params TrackEvent[] events) =>
        new(ClipId.New(), new Tick(start), new TickSpan(length), TickSpan.Zero, new EventList(events));

    private static Track With(params Clip[] clips) => new(TrackId.New(), "t", clips);

    [Fact]
    public void Clips_AreSortedByStart()
    {
        var late = Clip(100, 50);
        var early = Clip(0, 50);

        Assert.Equal<Clip>([early, late], With(late, early).Clips);
    }

    [Fact]
    public void OverlappingClips_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => With(Clip(0, 100), Clip(99, 10)));
        Assert.Equal(2, With(Clip(0, 100), Clip(100, 10)).Clips.Length);
    }

    [Fact]
    public void DuplicateIds_AcrossClips_AreRejected()
    {
        var note = Note(0);
        var clip = Clip(0, 50, note);

        Assert.Throws<ArgumentException>(() => With(clip, Clip(100, 50, note)));
        Assert.Throws<ArgumentException>(() => With(clip, clip with { Start = new Tick(100) }));
    }

    [Fact]
    public void ArrangedEvents_ConcatenateClipsAtTimelinePositions()
    {
        var a = Note(5);
        var b = Cc(0, ControllerNumber.ModulationWheel, 3);
        var track = With(Clip(1000, 100, b), Clip(0, 100, a));

        Assert.Equal([5L, 1000L], track.ArrangedEvents.Select(e => e.Position.Value));
        Assert.Equal([a.Id, b.Id], track.ArrangedEvents.Select(e => e.Id));
    }

    [Fact]
    public void EndPosition_IsTheEndOfTheLastClip()
    {
        Assert.Equal(Tick.Zero, Track.Create("t").EndPosition);
        Assert.Equal(new Tick(1100), With(Clip(0, 10, Note(0, length: 5000)), Clip(1000, 100)).EndPosition);
    }

    [Fact]
    public void FromEvents_WrapsEverythingInOneClipFromZero()
    {
        var note = Note(100, length: 50);
        var off = new NoteOffEvent(EventId.New(), new Tick(200), One, NoteNumber.MiddleC, Velocity.DefaultRelease);

        var track = Track.FromEvents(TrackId.New(), "t", [note, off]);

        var clip = Assert.IsType<NoteClip>(Assert.Single(track.Clips));
        Assert.Equal((0L, 201L), (clip.Start.Value, clip.End.Value));
        Assert.Equal([note, off], track.ArrangedEvents);
        Assert.Empty(Track.FromEvents(TrackId.New(), "t", []).Clips);
    }

    [Fact]
    public void ClipAt_AndClipOf_FindTheRightClip()
    {
        var note = Note(0);
        var first = Clip(0, 100, note);
        var second = Clip(200, 100);
        var track = With(first, second);

        Assert.Same(first, track.ClipAt(new Tick(99)));
        Assert.Null(track.ClipAt(new Tick(100)));
        Assert.Same(second, track.ClipAt(new Tick(200)));
        Assert.Same(first, track.ClipOf(note.Id));
        Assert.Null(track.ClipOf(EventId.New()));
        Assert.Same(second, track.FindClip(second.Id));
    }

    [Fact]
    public void GapAt_IsTheFreeSpaceAroundAPosition()
    {
        var track = With(Clip(1000, 500), Clip(3000, 500));

        Assert.Equal((Tick.Zero, (Tick?)new Tick(1000)), track.GapAt(new Tick(10)));
        Assert.Equal((new Tick(1500), (Tick?)new Tick(3000)), track.GapAt(new Tick(1500)));
        Assert.Equal((new Tick(3500), (Tick?)null), track.GapAt(new Tick(9000)));
        Assert.Equal((Tick.Zero, (Tick?)null), Track.Create("t").GapAt(new Tick(5)));
    }

    [Fact]
    public void PlaceClip_TrimsSplitsAndRemovesWhatItCovers()
    {
        var left = Clip(0, 100, Note(10), Note(90));
        var covered = Clip(150, 20);
        var spanning = Clip(300, 400, Note(50), Note(250));
        var track = With(left, covered, spanning);

        var placed = Clip(50, 400);
        var result = track.PlaceClip(placed);

        Assert.Equal([(0L, 50L), (50L, 450L), (450L, 700L)], result.Clips.Select(c => (c.Start.Value, c.End.Value)));
        Assert.DoesNotContain(result.Clips, c => c.Id == covered.Id);
        // Trimming hides content without deleting it.
        Assert.Equal(2, ((NoteClip)result.Clips[0]).Content.Items.Length);
        Assert.Equal([10L, 550L], result.ArrangedEvents.Select(e => e.Position.Value));
    }

    [Fact]
    public void ClearRange_SplitsAClipAroundTheRange()
    {
        var before = Note(10);
        var inside = Note(500);
        var after = Note(900);
        var clip = Clip(0, 1000, before, inside, after);

        var cleared = With(clip).ClearRange(new Tick(400), new Tick(600));

        Assert.Equal([(0L, 400L), (600L, 1000L)], cleared.Clips.Select(c => (c.Start.Value, c.End.Value)));
        Assert.Equal(clip.Id, cleared.Clips[0].Id);
        Assert.NotEqual(clip.Id, cleared.Clips[1].Id);
        Assert.Equal([before.Id, after.Id], cleared.ArrangedEvents.Select(e => e.Id));
        Assert.Same(cleared, cleared.ClearRange(new Tick(400), new Tick(600)));
    }

    [Fact]
    public void WithClip_ReplacesById()
    {
        var clip = Clip(0, 100);
        var track = With(clip);

        var moved = track.WithClip(clip with { Start = new Tick(500) });

        Assert.Equal(new Tick(500), Assert.Single(moved.Clips).Start);
        Assert.Empty(moved.RemoveClip(clip.Id).Clips);
        Assert.Same(moved, moved.RemoveClip(ClipId.New()));
    }

    [Fact]
    public void Names_AreBounded() =>
        Assert.Throws<ArgumentException>(() => Track.Create(new string('x', Track.MaxNameLength + 1)));

    [Fact]
    public void Edits_DoNotMutateTheOriginal()
    {
        var original = Track.Create("t");
        var edited = original.WithClip(Clip(0, 10, Note(0))).WithName("u").WithMuted(true).WithSoloed(true);

        Assert.Empty(original.ArrangedEvents);
        Assert.Equal("t", original.Name);
        Assert.False(original.IsMuted);
        Assert.Equal(("u", true, true, original.Id), (edited.Name, edited.IsMuted, edited.IsSoloed, edited.Id));
        Assert.Single(edited.ArrangedEvents);
    }

    [Fact]
    public void NoteEvent_EnforcesInvariantsIncludingThroughWith()
    {
        var note = Note(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => Note(0, length: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => note with { Duration = TickSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => note with { Velocity = Velocity.Off });
        Assert.Equal(new Tick(10), note.EndPosition);
    }

    [Fact]
    public void ChannelEvents_FallInTheirDocumentedPhases()
    {
        Assert.Equal(EventPhase.NoteOff, new NoteOffEvent(EventId.New(), Tick.Zero, One, NoteNumber.MiddleC, Velocity.DefaultRelease).Phase);
        Assert.Equal(EventPhase.BankSelect, Cc(0, ControllerNumber.BankSelectLsb).Phase);
        Assert.Equal(EventPhase.Control, Cc(0, ControllerNumber.ChannelVolume).Phase);
        Assert.Equal(EventPhase.ProgramChange, Program(0, 1).Phase);
        Assert.Equal(EventPhase.Control, new PitchBendEvent(Tick.Zero, One, ControlValue.Center).Phase);
    }

    [Fact]
    public void MetaEvent_RejectsInvalidType() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetaEvent(EventId.New(), Tick.Zero, 0x80, ByteBlock.Empty));

    [Fact]
    public void FirstChannel_IsTheChannelOfTheFirstChannelEvent()
    {
        var two = MidiChannel.FromNumber(2);
        var meta = new MetaEvent(EventId.New(), Tick.Zero, 0x03, ByteBlock.Copy("name"u8));
        var late = Note(100);
        var early = new ControllerEvent(new Tick(5), two, ControllerNumber.ChannelVolume, ControlValue.Max);

        Assert.Null(Track.FromEvents(TrackId.New(), "t", [meta]).FirstChannel);
        Assert.Equal(two, Track.FromEvents(TrackId.New(), "t", [late, meta, early]).FirstChannel);
        Assert.Equal(two, With(Clip(0, 10, meta), Clip(10, 10, early)).FirstChannel);
    }
}
