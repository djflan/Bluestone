using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CsCheck;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

public sealed class NoteClipTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);
    private static readonly MeterMap Meter = MeterMap.Constant(new Ppqn(480), TimeSignature.CommonTime);

    private static NoteEvent Note(long at, long length = 10, int note = 60) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber(note), new Velocity(100));

    private static ControllerEvent Cc(long at) => new(new Tick(at), One, ControllerNumber.ModulationWheel, ControlValue.Max);

    private static NoteClip Clip(long start, long length, long offset, params TrackEvent[] content) =>
        new(ClipId.New(), new Tick(start), new TickSpan(length), new TickSpan(offset), new EventList(content));

    private static List<long> Positions(IEnumerable<TrackEvent> events) => [.. events.Select(e => e.Position.Value)];

    [Fact]
    public void Arrange_ShowsTheWindowAtTimelinePositions()
    {
        var clip = Clip(1000, 100, 50, Cc(10), Cc(50), Cc(149), Cc(150));

        Assert.Equal([1000L, 1099L], Positions(clip.Arrange()));
        Assert.Equal(950, clip.Origin);
    }

    [Fact]
    public void Arrange_CutsNotesAtTheClipEnd()
    {
        var clip = Clip(0, 100, 0, Note(90, length: 50), Note(10, length: 20));

        var notes = clip.Arrange().Cast<NoteEvent>().ToList();

        Assert.Equal([(10L, 20L), (90L, 10L)], notes.Select(n => (n.Position.Value, n.Duration.Value)));
    }

    [Fact]
    public void Arrange_WithoutShiftOrCuts_ReturnsTheSameEvents()
    {
        var note = Note(5);

        Assert.Same(note, Assert.Single(Clip(0, 100, 0, note).Arrange()));
    }

    [Fact]
    public void TimelineContent_IncludesHiddenContentButNothingBeforeTickZero()
    {
        // Trimmed by 100 and then moved to tick 50, so content 0-49 would be before the sequence start.
        var clip = Clip(50, 100, 100, Cc(10), Cc(60), Cc(120), Cc(400));

        Assert.Equal([10L, 70L, 350L], Positions(clip.TimelineContent()));
    }

    [Fact]
    public void Shows_ChecksTheWindow()
    {
        var hidden = Cc(10);
        var visible = Cc(60);
        var clip = Clip(0, 100, 50, hidden, visible);

        Assert.False(clip.Shows(hidden));
        Assert.True(clip.Shows(visible));
    }

    [Fact]
    public void WithBounds_KeepsContentInPlaceOnTheTimeline()
    {
        var clip = Clip(1000, 500, 0, Cc(0), Cc(200), Cc(400));

        var trimmed = clip.WithBounds(new Tick(1100), new Tick(1300));
        Assert.Equal([1200L], Positions(trimmed.Arrange()));
        Assert.Equal(100, trimmed.ContentOffset.Value);

        var widened = trimmed.WithBounds(new Tick(900), new Tick(1500));
        Assert.Equal([1000L, 1200L, 1400L], Positions(widened.Arrange()));
        Assert.Equal(0, widened.ContentOffset.Value);
        Assert.Equal([100L, 300L, 500L], Positions(widened.Content.Items));
        Assert.Throws<ArgumentException>(() => clip.WithBounds(new Tick(10), new Tick(10)));
    }

    [Fact]
    public void SplitAt_PartitionsContentAndKeepsEventIds()
    {
        var held = Note(50, length: 100);
        var late = Cc(150);
        var clip = Clip(1000, 200, 0, held, late);
        var rightId = ClipId.New();

        var (left, right) = clip.SplitAt(new Tick(1100), rightId);

        Assert.Equal((clip.Id, 1000L, 1100L), (left.Id, left.Start.Value, left.End.Value));
        Assert.Equal((rightId, 1100L, 1200L), (right.Id, right.Start.Value, right.End.Value));
        // The held note stays left and is cut at the split.
        var note = Assert.IsType<NoteEvent>(Assert.Single(((NoteClip)left).Arrange()));
        Assert.Equal((held.Id, 50L), (note.Id, note.Duration.Value));
        Assert.Equal([late.Id], ((NoteClip)right).Arrange().Select(e => e.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.SplitAt(new Tick(1000), rightId));
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.SplitAt(new Tick(1200), rightId));
    }

    [Fact]
    public void CopyTo_GivesNewIds()
    {
        var note = Note(0);
        var clip = Clip(0, 100, 0, note);

        var copy = clip.CopyTo(new Tick(500));

        Assert.NotEqual(clip.Id, copy.Id);
        Assert.Equal(new Tick(500), copy.Start);
        var copied = Assert.Single(copy.Arrange());
        Assert.NotEqual(note.Id, copied.Id);
        Assert.Equal(new Tick(500), copied.Position);
    }

    [Fact]
    public void EditTimeline_ConvertsPositionsAndMovesTheOriginBack()
    {
        var kept = Cc(100);
        var clip = Clip(1000, 500, 0, kept);

        var edited = clip.EditTimeline([], [Note(900), Note(1200)]);

        Assert.Equal((1000L, 100L, 900L), (edited.Start.Value, edited.ContentOffset.Value, edited.Origin));
        Assert.Equal([900L, 1100L, 1200L], Positions(edited.TimelineContent()));
        // The note before the clip is kept but hidden.
        Assert.Equal([1100L, 1200L], Positions(edited.Arrange()));
        Assert.Empty(edited.EditTimeline([kept.Id], []).Content.Items.Where(e => e.Id == kept.Id));
    }

    [Fact]
    public void Enclosing_SpansBarLinesAroundTheEvents()
    {
        var clip = NoteClip.Enclosing([Note(2000, length: 100), Cc(3000)], Meter)!;

        Assert.Equal((1920L, 3840L), (clip.Start.Value, clip.End.Value));
        Assert.Equal([80L, 1080L], Positions(clip.Content.Items));
        Assert.Null(NoteClip.Enclosing([], Meter));
    }

    [Fact]
    public void Enclosing_KeepsAnEventOnTheEndBarLine()
    {
        // The note ends exactly on a bar line, where a release is also kept from an import.
        var note = Note(0, length: 1920);
        var off = new NoteOffEvent(EventId.New(), new Tick(1920), One, NoteNumber.MiddleC, Velocity.DefaultRelease);

        Assert.Equal(new Tick(1920), NoteClip.Enclosing([note], Meter)!.End);
        Assert.Equal(new Tick(3840), NoteClip.Enclosing([note, off], Meter)!.End);
    }

    [Fact]
    public void Enclosing_FollowsAMidBarMeterChange()
    {
        var meter = new MeterMap(new Ppqn(480), [new MeterChange(new Tick(960), new TimeSignature(3, 4))]);

        var clip = NoteClip.Enclosing([Note(1000)], meter)!;

        Assert.Equal((960L, 2400L), (clip.Start.Value, clip.End.Value));
    }

    private static readonly Gen<TrackEvent> GenEvent =
        Gen.OneOf<TrackEvent>(
            Gen.Select(Gen.Long[0, 10_000], Gen.Long[1, 5000]).Select(x => (TrackEvent)Note(x.Item1, x.Item2)),
            Gen.Long[0, 10_000].Select(x => (TrackEvent)Cc(x)));

    [Fact]
    public void Enclosing_PlaysExactlyWhatTheEventsSay() =>
        GenEvent.Array[1, 30].Sample(events =>
        {
            var arranged = NoteClip.Enclosing(events, Meter)!.Arrange().ToList();
            var expected = new EventList(events).Items;
            return arranged.Count == expected.Length
                && arranged.Zip(expected).All(p => p.First.Id == p.Second.Id && p.First.Position == p.Second.Position && p.First.EndPosition == p.Second.EndPosition);
        });
}
