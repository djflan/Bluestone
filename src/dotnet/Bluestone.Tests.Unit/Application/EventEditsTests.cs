using Bluestone.Application.Editing;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CsCheck;

namespace Bluestone.Tests.Unit.Application;

public sealed class EventEditsTests
{
    private static readonly Ppqn Ppqn = new(480);
    private static readonly MeterMap FourFour = MeterMap.Constant(Ppqn, TimeSignature.CommonTime);
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, int pitch = 60, long length = 240, int velocity = 100) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber(pitch), new Velocity(velocity));

    private static ControllerEvent Cc(long at, int value = 10) =>
        new(new Tick(at), One, ControllerNumber.ModulationWheel, ControlValue.FromSevenBit(value));

    [Theory]
    [InlineData(GridDivision.Quarter, 230, 0)]
    [InlineData(GridDivision.Quarter, 370, 480)]
    [InlineData(GridDivision.Sixteenth, 130, 120)]
    [InlineData(GridDivision.EighthTriplet, 170, 160)]
    [InlineData(GridDivision.Bar, 1000, 1920)]
    [InlineData(GridDivision.Bar, 900, 0)]
    public void Snap_FindsTheNearestGridLine(GridDivision grid, long tick, long expected) =>
        Assert.Equal(expected, MusicalGrid.Snap(tick, grid, FourFour));

    [Fact]
    public void Grid_RestartsAtEveryBarLine()
    {
        // In 7/8 a bar is 1680 ticks; a quarter grid must land on the next bar's downbeat, not 1920.
        var sevenEight = MeterMap.Constant(Ppqn, new TimeSignature(7, 8));

        Assert.Equal(1680, MusicalGrid.Snap(1700, GridDivision.Quarter, sevenEight));
        Assert.Equal(1680 + 480, MusicalGrid.SnapDown(1680 + 500, GridDivision.Quarter, sevenEight));
    }

    [Fact]
    public void Move_ShiftsTimeAndPitchAndKeepsIds()
    {
        var note = Note(480, 60);
        var cc = Cc(480);

        var moved = EventEdits.Move([note, cc], 240, 2);

        var movedNote = Assert.IsType<NoteEvent>(moved[0]);
        Assert.Equal(note.Id, movedNote.Id);
        Assert.Equal(720, movedNote.Position.Value);
        Assert.Equal(62, movedNote.Note.Value);
        Assert.Equal(720, moved[1].Position.Value);
    }

    [Fact]
    public void Move_ClampsTheGroupAtTheEdges()
    {
        var notes = new TrackEvent[] { Note(100, 120), Note(300, 10) };

        var moved = EventEdits.Move(notes, -1000, 20).Cast<NoteEvent>().ToList();

        // The earliest note stops at tick 0 and the highest at 127; spacing is preserved.
        Assert.Equal([0L, 200L], moved.Select(n => n.Position.Value));
        Assert.Equal([127, 17], moved.Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void Move_ByNothing_ReturnsNoChanges() =>
        Assert.Empty(EventEdits.Move([Note(0, 0)], -10, -5));

    [Fact]
    public void ResizeEnd_KeepsAMinimumLength()
    {
        var resized = (NoteEvent)Assert.Single(EventEdits.Resize([Note(0, length: 240)], NoteEdge.End, -1000, minimumLength: 30));

        Assert.Equal(30, resized.Duration.Value);
    }

    [Fact]
    public void ResizeStart_MovesTheStartAndKeepsTheEnd()
    {
        var resized = (NoteEvent)Assert.Single(EventEdits.Resize([Note(480, length: 480)], NoteEdge.Start, 120));

        Assert.Equal(600, resized.Position.Value);
        Assert.Equal(960, resized.EndPosition.Value);
    }

    [Fact]
    public void ResizeStart_StopsAtZeroAndAtTheMinimumLength()
    {
        var early = (NoteEvent)Assert.Single(EventEdits.Resize([Note(100, length: 100)], NoteEdge.Start, -500));
        var late = (NoteEvent)Assert.Single(EventEdits.Resize([Note(100, length: 100)], NoteEdge.Start, 500, minimumLength: 10));

        Assert.Equal((0L, 200L), (early.Position.Value, early.EndPosition.Value));
        Assert.Equal((190L, 200L), (late.Position.Value, late.EndPosition.Value));
    }

    [Fact]
    public void Quantize_MovesStartsByStrength()
    {
        var full = (NoteEvent)Assert.Single(EventEdits.Quantize([Note(500)], FourFour, GridDivision.Quarter));
        var half = (NoteEvent)Assert.Single(EventEdits.Quantize([Note(500)], FourFour, GridDivision.Quarter, new QuantizeOptions(Strength: 50)));

        Assert.Equal(480, full.Position.Value);
        Assert.Equal(490, half.Position.Value);
        Assert.Equal(240, full.Duration.Value);
    }

    [Fact]
    public void Quantize_CanSnapEndsAndApplySwing()
    {
        var ends = (NoteEvent)Assert.Single(EventEdits.Quantize([Note(10, length: 200)], FourFour, GridDivision.Eighth, new QuantizeOptions(QuantizeEnds: true)));
        var swung = EventEdits.Quantize([Note(5), Note(250)], FourFour, GridDivision.Eighth, new QuantizeOptions(Swing: 66)).Cast<NoteEvent>().ToList();

        Assert.Equal((0L, 240L), (ends.Position.Value, ends.Duration.Value));

        // 66% swing puts the off-beat two thirds of the way through the quarter: a triplet feel.
        Assert.Equal([0L, 317L], swung.Select(n => n.Position.Value));
    }

    [Fact]
    public void Swing_ChoosesTheNearestSwungLine()
    {
        // 125 is just past half a step, but the swung off-beat (317) is further away than the downbeat.
        var result = EventEdits.Quantize([Note(125), Note(300)], FourFour, GridDivision.Eighth, new QuantizeOptions(Swing: 66)).Cast<NoteEvent>().ToList();

        Assert.Equal([0L, 317L], result.Select(n => n.Position.Value));
    }

    [Fact]
    public void Snap_NeverPassesTheNextDownbeat()
    {
        // A 7/16 bar is 840 ticks, not a whole number of quarter notes.
        var sevenSixteen = MeterMap.Constant(Ppqn, new TimeSignature(7, 16));

        Assert.Equal(840, MusicalGrid.Snap(800, GridDivision.Quarter, sevenSixteen));
        Assert.Equal(480, MusicalGrid.SnapDown(800, GridDivision.Quarter, sevenSixteen));
    }

    [Fact]
    public void Quantize_LeavesEventsOnTheGridUnchanged() =>
        Assert.Empty(EventEdits.Quantize([Note(480), Cc(960)], FourFour, GridDivision.Quarter));

    [Fact]
    public void Velocity_EditsClampToTheAudibleRange()
    {
        var notes = new[] { Note(0, velocity: 100), Note(10, velocity: 20) };

        Assert.Equal([127, 47], EventEdits.AddVelocity(notes, 27).Cast<NoteEvent>().Select(n => (int)n.Velocity.Value));
        Assert.Equal([1], EventEdits.AddVelocity(notes, -100).Cast<NoteEvent>().Select(n => (int)n.Velocity.Value).Distinct());
        Assert.Equal([50, 10], EventEdits.ScaleVelocity(notes, 50).Cast<NoteEvent>().Select(n => (int)n.Velocity.Value));
        Assert.Single(EventEdits.SetVelocity(notes, 100));
    }

    [Fact]
    public void VelocityRamp_InterpolatesAcrossTheRange()
    {
        var notes = new[] { Note(0), Note(480), Note(960), Note(2000) };

        var ramp = EventEdits.VelocityRamp(notes, 0, 20, 960, 120).Cast<NoteEvent>().ToList();

        Assert.Equal([20, 70, 120], ramp.Select(n => (int)n.Velocity.Value));
    }

    [Fact]
    public void Legato_ExtendsToTheNextStart()
    {
        var chordA = Note(0, 60, 100);
        var chordB = Note(0, 64, 50);
        var next = Note(480, 62, 100);

        var result = EventEdits.Legato([next, chordA, chordB]).Cast<NoteEvent>().ToDictionary(n => n.Id);

        Assert.Equal(480, result[chordA.Id].Duration.Value);
        Assert.Equal(480, result[chordB.Id].Duration.Value);
        Assert.False(result.ContainsKey(next.Id));
    }

    [Fact]
    public void SetChannel_ReaddressesNotesAndMessages()
    {
        var two = MidiChannel.FromNumber(2);

        var result = EventEdits.SetChannel([Note(0), Cc(0)], two);

        Assert.Equal(two, Assert.IsType<NoteEvent>(result[0]).Channel);
        Assert.Equal(two, Assert.IsType<ControllerEvent>(result[1]).Channel);
    }

    [Fact]
    public void Copy_GivesNewIdsAndShifts()
    {
        var note = Note(100);

        var copy = Assert.Single(EventEdits.Copy([note], 1920));

        Assert.NotEqual(note.Id, copy.Id);
        Assert.Equal(2020, copy.Position.Value);
        Assert.Equal(240, EventEdits.Extent([note]).Value);
    }

    [Fact]
    public void Move_NeverLeavesTheValidRange()
    {
        var gen = Gen.Select(Gen.Long[0, 10_000], Gen.Int[0, 127]).Array[1, 8];
        Gen.Select(gen, Gen.Long[-20_000, 20_000], Gen.Int[-200, 200]).Sample((spec, dt, dp) =>
        {
            var notes = spec.Select(s => (TrackEvent)Note(s.Item1, s.Item2)).ToList();
            var moved = EventEdits.Move(notes, dt, dp).Cast<NoteEvent>().ToList();
            return moved.All(n => n.Position.Value >= 0 && n.Note.Value <= 127);
        });
    }

    [Fact]
    public void EditEvents_ReplacesAddsAndRemovesInOneUndoStep()
    {
        var keep = Note(0);
        var drop = Note(480);
        var track = Track.FromEvents(TrackId.New(), "t", [keep, drop]);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn).WithTrack(track) });
        var added = Note(960);

        history.Execute(ProjectCommands.EditEvents(track.Id, track.Clips[0].Id, "Edit Notes", [drop.Id], [added, keep with { Velocity = new Velocity(1) }]));

        var events = history.Current.Sequence.Tracks[0].ArrangedEvents.Cast<NoteEvent>().ToList();
        Assert.Equal([keep.Id, added.Id], events.Select(e => e.Id));
        Assert.Equal(1, events[0].Velocity.Value);
        Assert.Equal("Edit Notes", history.UndoLabel);
        history.Undo();
        Assert.Equal(2, history.Current.Sequence.Tracks[0].ArrangedEvents.Length);
    }

    [Fact]
    public void EditEvents_ThatChangeNothing_AreNotRecorded()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0)]);
        var history = new EditHistory(Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn).WithTrack(track) });

        Assert.False(history.Execute(ProjectCommands.RemoveEvents(track.Id, [EventId.New()])));
        Assert.False(history.Execute(ProjectCommands.ReplaceEvents(track.Id, track.Clips[0].Id, "Quantize", [])));
    }

    [Fact]
    public void Record_ReplaceRemovesOnlyEventsInsideTheRange()
    {
        var before = Note(0);
        var inside = Note(1000);
        var after = Note(5000);
        var track = Track.FromEvents(TrackId.New(), "t", [before, inside, after]);
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn).WithTrack(track) };
        var take = Note(1200);

        var replaced = ProjectCommands.Record(track.Id, [take], new TickRange(new Tick(960), new Tick(4000))).Apply(project);
        var merged = ProjectCommands.Record(track.Id, [take], null).Apply(project);

        Assert.Equal([before.Id, take.Id, after.Id], replaced.Sequence.Tracks[0].ArrangedEvents.Select(e => e.Id));
        Assert.Equal(4, merged.Sequence.Tracks[0].ArrangedEvents.Length);
        Assert.Same(project, ProjectCommands.Record(track.Id, [], new TickRange(new Tick(6000), new Tick(7000))).Apply(project));
    }
}
