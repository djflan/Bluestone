using System.Diagnostics;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Tests.Unit.Playback;

/// <summary>Looping note clips in the playback plan: repeats are planned with every note released inside its pass.</summary>
public sealed class ClipLoopPlaybackTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, long length, int note) => new(new Tick(at), new TickSpan(length), One, new NoteNumber((byte)note), new Velocity(100));

    private static Sequence WithClip(NoteClip clip) =>
        Sequence.CreateEmpty(Ppqn.Default).WithTrack(new Track(TrackId.New(), "Loop", [clip]));

    [Fact]
    public void ALoopingClip_IsPlannedPassByPass_AndNoNoteOutlivesItsPass()
    {
        // A one-beat loop over four bars, its note crossing the loop's end.
        var beat = Ppqn.Default.TicksPerQuarterNote;
        var clip = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(16L * beat), TickSpan.Zero, new EventList([Note(0, beat / 2, 60), Note(beat * 3 / 4, beat, 64)]))
        {
            Loop = new ClipLoop(Tick.Zero, new TickSpan(beat)),
        };

        var plan = PlanDump.Compile(PlanDump.Routed(WithClip(clip)));
        var notes = Enumerable.Range(0, plan.EventCount).Select(i => plan.Events[i]).Where(e => e.IsNote).ToList();

        Assert.Equal(32, notes.Count);
        foreach (var note in notes)
        {
            var passEnd = ((note.Tick / beat) + 1) * beat;
            Assert.True(note.Tick + note.DurationTicks <= passEnd, $"A note at {note.Tick} ends at {note.Tick + note.DurationTicks}, after its pass ({passEnd}).");
        }

        Assert.Equal(16, notes.Count(n => n.Message.Note.Value == 64 && n.DurationTicks == beat / 4));
    }

    [Fact]
    public void ALongClipWithAShortLoop_IsPlannedQuickly_WithinTheLimit()
    {
        // Close to the per-clip limit: a 16th-note loop with two events over 2000 bars (64,000 passes).
        var sixteenth = Ppqn.Default.TicksPerQuarterNote / 4;
        var clip = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(sixteenth * 16L * 2000), TickSpan.Zero, new EventList([Note(0, 10, 60), new ControllerEvent(new Tick(5), One, ControllerNumber.ModulationWheel, ControlValue.FromSevenBit(3))]))
        {
            Loop = new ClipLoop(Tick.Zero, new TickSpan(sixteenth)),
        };
        Assert.True(clip.ArrangedEventCount() <= NoteClip.MaxArrangedEvents);

        var watch = Stopwatch.StartNew();
        var plan = PlanDump.Compile(PlanDump.Routed(WithClip(clip)));
        watch.Stop();

        Assert.Equal(clip.ArrangedEventCount(), plan.EventCount);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Planning took {watch.Elapsed}.");
    }
}
