using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

public sealed class SequenceTests
{
    [Fact]
    public void CreateEmpty_UsesDefaults()
    {
        var sequence = Sequence.CreateEmpty(Ppqn.Default);

        Assert.Equal(Ppqn.Default, sequence.Ppqn);
        Assert.Equal(Tempo.Default, sequence.TempoMap.TempoAt(Tick.Zero));
        Assert.Equal(TimeSignature.CommonTime, sequence.MeterMap.SignatureAt(Tick.Zero));
        Assert.Empty(sequence.Tracks);
        Assert.Equal(Tick.Zero, sequence.EndPosition);
    }

    [Fact]
    public void Constructor_RejectsMismatchedResolutions() =>
        Assert.Throws<ArgumentException>(() => new Sequence(
            TempoMap.Constant(new Ppqn(480), Tempo.Default),
            MeterMap.Constant(new Ppqn(960), TimeSignature.CommonTime),
            [],
            []));

    [Fact]
    public void Constructor_RejectsDuplicateTracks()
    {
        var track = Track.Create("a");
        var empty = Sequence.CreateEmpty(Ppqn.Default);

        Assert.Throws<ArgumentException>(() => new Sequence(empty.TempoMap, empty.MeterMap, [track, track], []));
    }

    [Fact]
    public void WithTrack_ReplacesInPlaceOrAppends()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(a).WithTrack(b);

        var renamed = sequence.WithTrack(a.WithName("A"));

        Assert.Equal(["A", "b"], renamed.Tracks.Select(t => t.Name));
        Assert.Equal(["a", "b"], sequence.Tracks.Select(t => t.Name));
        Assert.Equal(["b"], renamed.WithoutTrack(a.Id).Tracks.Select(t => t.Name));
        Assert.Same(b, sequence.FindTrack(b.Id));
    }

    [Fact]
    public void EndPosition_IncludesNoteReleases()
    {
        var note = new NoteEvent(new Tick(100), new TickSpan(50), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max);
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(Track.FromEvents(TrackId.New(), "a", [note]));

        Assert.Equal(new Tick(150), sequence.EndPosition);
    }

    [Fact]
    public void InsertTrack_PlacesTrackAtIndex()
    {
        var a = Track.Create("a");
        var c = Track.Create("c");
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(a).WithTrack(c);

        var inserted = sequence.InsertTrack(1, Track.Create("b"));

        Assert.Equal(["a", "b", "c"], inserted.Tracks.Select(t => t.Name));
        Assert.Throws<ArgumentException>(() => inserted.InsertTrack(0, a));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.InsertTrack(3, Track.Create("x")));
    }

    [Fact]
    public void Markers_AreSorted()
    {
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithMarkers([new Marker(new Tick(10), "B"), new Marker(Tick.Zero, "A")]);

        Assert.Equal(["A", "B"], sequence.Markers.Select(m => m.Name));
    }
}
