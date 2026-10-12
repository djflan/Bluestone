using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

/// <summary>Clip loops: passes, pickups, notes at pass ends, chase at repeats, editing, limits, and the file format.</summary>
public sealed class ClipLoopTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, long length, int note = 60) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber((byte)note), new Velocity(100));

    private static ControllerEvent Cc(long at, int controller, int value) =>
        new(new Tick(at), One, new ControllerNumber(controller), ControlValue.FromSevenBit(value));

    // A pickup note at 0, then a loop [480, 960) holding a note at 480 and one at 900 that runs past the loop's end.
    private static NoteClip Pickup(long start = 1000, long length = 2400, bool loop = true) =>
        new NoteClip(ClipId.New(), new Tick(start), new TickSpan(length), TickSpan.Zero, new EventList([Note(0, 240, 48), Note(480, 240, 60), Note(900, 200, 64)]))
        {
            Loop = new ClipLoop(new Tick(480), new TickSpan(480), loop),
        };

    private static List<(long Position, long End, int Note)> Notes(NoteClip clip) =>
        [.. clip.Arrange().OfType<NoteEvent>().Select(n => (n.Position.Value, n.EndPosition.Value, (int)n.Note.Value))];

    [Fact]
    public void APickupPlaysOnce_ThenTheLoopRepeats_WithNotesCutAtEachPassEnd()
    {
        var clip = Pickup();

        Assert.Equal(
            [
                (1000L, 1240L, 48), (1480L, 1720L, 60), (1900L, 1960L, 64),
                (1960L, 2200L, 60), (2380L, 2440L, 64),
                (2440L, 2680L, 60), (2860L, 2920L, 64),
                (2920L, 3160L, 60), (3340L, 3400L, 64),
            ],
            Notes(clip));
        Assert.Equal([0L, 1L, 2L, 3L], [.. EnumeratePasses(clip).Select(p => p.Index)]);
        Assert.Equal(4, clip.Passes.Count);
    }

    [Fact]
    public void ALoopTurnedOff_PlaysTheContentOnce_AndLeavesTheRestSilent()
    {
        var clip = Pickup(loop: false);

        Assert.Equal([(1000L, 1240L, 48), (1480L, 1720L, 60), (1900L, 2100L, 64)], Notes(clip));
        Assert.Equal(new Tick(480), clip.Loop!.Start);
    }

    [Fact]
    public void AClipStartingInsideItsLoop_StartsMidPass_AndWrapsAtTheLoopEnd()
    {
        var clip = Pickup(length: 900) with { ContentOffset = new TickSpan(700) };

        // Content 700..960 (the note at 900, cut at 960), then 480..960 again, then 480..640.
        Assert.Equal([(1200L, 1260L, 64), (1260L, 1500L, 60), (1680L, 1740L, 64), (1740L, 1900L, 60)], Notes(clip));
        Assert.Equal(700, clip.ContentAt(0));
        Assert.Equal(480, clip.ContentAt(260));
    }

    [Fact]
    public void ARepeat_RestoresTheLoopStartsControllerState_AndResetsWhatTheLoopFirstSets()
    {
        var bend = new PitchBendEvent(new Tick(600), One, ControlValue.FromFourteenBit(12000));
        var content = new EventList([Cc(0, 1, 10), Cc(480, 7, 90), Cc(600, 1, 100), Cc(700, 7, 40), bend, Note(480, 100)]);
        var clip = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(1440), TickSpan.Zero, content) { Loop = new ClipLoop(new Tick(480), new TickSpan(480)) };

        var atSecondPass = clip.Arrange().Where(e => e.Position.Value == 960).ToList();

        // Modulation goes back to its value before the loop; the bend first set inside it is centred; volume is set
        // at the loop start itself, so that event plays again and is not chased.
        Assert.Contains(atSecondPass, e => e is ControllerEvent { Controller.Value: 1 } c && c.Value == ControlValue.FromSevenBit(10));
        Assert.Contains(atSecondPass, e => e is PitchBendEvent b && b.Value == ControlValue.Center);
        Assert.Single(atSecondPass, e => e is ControllerEvent { Controller.Value: 7 });
        Assert.IsType<NoteEvent>(atSecondPass[^1]);
        Assert.Equal(clip.Arrange().Select(e => e.Position), clip.Arrange().Select(e => e.Position).Order());
    }

    [Fact]
    public void MovingAndCopying_KeepTheLoopAndItsPhase()
    {
        var clip = Pickup();

        var moved = clip with { Start = new Tick(5000) };
        var copy = clip.CopyTo(new Tick(5000));

        Assert.Equal(Notes(clip).Select(n => (n.Position + 4000, n.End + 4000, n.Note)), Notes(moved));
        Assert.Equal(Notes(moved), Notes(copy));
        Assert.Equal(clip.Loop, copy.Loop);
    }

    [Fact]
    public void Splitting_KeepsWhatPlaysOnBothSides_WithoutRepeatingEventIds()
    {
        var clip = Pickup();
        var track = new Track(TrackId.New(), "t", [clip]);

        var (left, right) = clip.SplitAt(new Tick(2100), ClipId.New());
        var split = track.WithClips([left, right]);

        // The note at 2100 is cut at the split (both halves end there); the rest is identical.
        Assert.Equal(
            Notes(clip).Select(n => n.Position < 2100 && n.End > 2100 ? (n.Position, 2100L, n.Note) : n),
            split.ArrangedEvents.OfType<NoteEvent>().Select(n => (n.Position.Value, n.EndPosition.Value, (int)n.Note.Value)));
        Assert.Empty(((NoteClip)left).Content.Items.Select(e => e.Id).Intersect(((NoteClip)right).Content.Items.Select(e => e.Id)));
    }

    [Fact]
    public void TrimmingTheLeftEdge_KeepsWhatPlaysInTheRest_AndTheHiddenContent()
    {
        var clip = Pickup();

        var trimmed = clip.WithBounds(new Tick(2000), clip.End);

        Assert.Equal(Notes(clip).Where(n => n.Position >= 2000), Notes(trimmed).Where(n => n.Position >= 2000));
        Assert.Equal(1000, trimmed.ContentOffset.Value);
        Assert.Equal(clip.Content, trimmed.Content);
    }

    [Fact]
    public void ExtendingTheLeftEdgeBeforeTheContent_RebasesTheLoopWithIt()
    {
        var clip = Pickup(start: 1000);

        var extended = clip.WithBounds(new Tick(500), clip.End);

        Assert.Equal(new Tick(980), extended.Loop!.Start);
        Assert.Equal(Notes(clip), Notes(extended));
    }

    [Fact]
    public void Consolidating_WritesOutWhatPlays_WithoutALoop()
    {
        var clip = Pickup();

        var consolidated = clip.Consolidated();

        Assert.Null(consolidated.Loop);
        Assert.Equal(Notes(clip), Notes(consolidated));
        Assert.Equal(consolidated.Content.Items.Length, consolidated.Content.Items.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void TheEventCount_MatchesWhatIsArranged()
    {
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var events = Enumerable.Range(0, random.Next(0, 12)).Select(_ => (TrackEvent)(random.Next(3) == 0
                ? Cc(random.Next(0, 2000), random.Next(1, 12), random.Next(0, 128))
                : Note(random.Next(0, 2000), random.Next(1, 400), random.Next(40, 80))));
            var clip = new NoteClip(ClipId.New(), new Tick(random.Next(0, 5000)), new TickSpan(random.Next(1, 8000)), new TickSpan(random.Next(0, 1500)), new EventList(events))
            {
                Loop = new ClipLoop(new Tick(random.Next(0, 1500)), new TickSpan(random.Next(1, 1200)), random.Next(4) != 0),
            };

            Assert.Equal(clip.Arrange().Count(), clip.ArrangedEventCount());
        }
    }

    [Fact]
    public void AVeryShortLoopOverALongClip_IsRefused_RatherThanSilentlyCut()
    {
        var clip = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(960L * 4 * 1000), TickSpan.Zero, new EventList([Note(0, 1)]))
        {
            Loop = new ClipLoop(Tick.Zero, new TickSpan(1)),
        };

        Assert.True(clip.ArrangedEventCount() > NoteClip.MaxArrangedEvents);
        var ex = Assert.Throws<ClipLoopLimitException>(() => new Track(TrackId.New(), "t", [clip]));
        Assert.Contains("loop", ex.Message, StringComparison.OrdinalIgnoreCase);

        // A count too large for a long saturates rather than wrapping past the budget check.
        var endless = clip with { Length = new TickSpan(long.MaxValue), Content = new EventList([Note(0, 1), Note(0, 1, 64)]) };
        Assert.Equal(long.MaxValue, endless.ArrangedEventCount());
        Assert.Throws<ClipLoopLimitException>(() => new Track(TrackId.New(), "t", [endless]));

        // An empty loop repeats nothing, however short.
        var empty = clip with { Content = EventList.Empty };
        Assert.Equal(0, empty.ArrangedEventCount());
        Assert.Single(new Track(TrackId.New(), "t", [empty]).Clips);
    }

    [Fact]
    public void AnAudioClipSplit_StartsTheRightPartAtTheAudioHeardThere()
    {
        var source = new AudioSource("a.wav", 48000, 480000, 2);
        var clip = new AudioClip(ClipId.New(), new Tick(1000), new TickSpan(3000), new TickSpan(100), source)
        {
            Loop = new ClipLoop(new Tick(500), new TickSpan(400)),
            FadeIn = new TickSpan(50),
            FadeOut = new TickSpan(60),
        };

        var (left, right) = clip.SplitAt(new Tick(2000), ClipId.New());

        // The offset is the unwrapped position (100 + 1000), never wrapped into the loop; it plays content 700 there.
        Assert.Equal(1100, right.ContentOffset.Value);
        Assert.Equal(700, right.ContentAt(0));
        Assert.Equal(clip.Loop, right.Loop);
        Assert.Equal(TickSpan.Zero, ((AudioClip)left).FadeOut);
        Assert.Equal(TickSpan.Zero, ((AudioClip)right).FadeIn);
        Assert.Equal(new TickSpan(60), ((AudioClip)right).FadeOut);
        for (var t = 0; t < 2000; t += 37)
        {
            Assert.Equal(clip.ContentAt(1000 + t), right.ContentAt(t));
        }
    }

    [Fact]
    public void ClipLoops_AreSavedAndReadBack_OnBothClipTypes()
    {
        var note = Pickup(loop: false);
        var audio = new AudioClip(ClipId.New(), new Tick(8000), new TickSpan(3000), TickSpan.Zero, new AudioSource("a.wav", 48000, 480000, 2))
        {
            Loop = new ClipLoop(new Tick(100), new TickSpan(900)),
        };
        var track = new Track(TrackId.New(), "t", [note, audio], role: TrackRole.Hybrid);
        var project = Project.CreateNew() with { Sequence = Project.CreateNew().Sequence.WithTrack(track) };

        var saved = ProjectSerializer.Default.Serialize(new ProjectDocument(project));
        var restored = ProjectSerializer.Default.Deserialize(saved).Project.Sequence.Tracks.Single();

        Assert.Equal(note.Loop, restored.Clips[0].Loop);
        Assert.Equal(audio.Loop, restored.Clips[1].Loop);

        var json = JsonNode.Parse(saved)!;
        json["project"]!["sequence"]!["tracks"]![0]!["clips"]![0]!["loop"]!["length"] = 0;
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Default.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void ABankChangedInsideTheLoop_IsRestoredWithTheProgram_AsAtAnArrangerWrap()
    {
        var program = new ProgramEvent(Tick.Zero, One, new ProgramSelection(new ProgramNumber(5)));
        var content = new EventList([Cc(0, 0, 1), program, Cc(600, 0, 9), Note(480, 100)]);
        var clip = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(1440), TickSpan.Zero, content) { Loop = new ClipLoop(new Tick(480), new TickSpan(480)) };

        var atRepeat = clip.Arrange().Where(e => e.Position.Value == 960).ToList();

        Assert.IsType<ControllerEvent>(atRepeat[0]);
        Assert.Equal(ControlValue.FromSevenBit(1), ((ControllerEvent)atRepeat[0]).Value);
        Assert.Equal(program.Selection, Assert.IsType<ProgramEvent>(atRepeat[1]).Selection);
        Assert.IsType<NoteEvent>(atRepeat[^1]);
    }

    [Fact]
    public void AnEmptyOneTickLoopOverAVeryLongClip_IsArrangedWithoutWalkingEveryPass()
    {
        var clip = new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(long.MaxValue / 4), TickSpan.Zero, new EventList([Note(0, 10)]))
        {
            Loop = new ClipLoop(new Tick(100), new TickSpan(1)),
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var arranged = clip.Arrange().ToList();

        Assert.Single(arranged);
        Assert.Equal(1, clip.ArrangedEventCount());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ManyLoopingClipsTogether_AreLimitedAcrossTheSequence()
    {
        // Each clip is under the per-clip limit; five of them together are over the project's.
        Track Dense() => new(TrackId.New(), "t", [new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(200_000 * 10L), TickSpan.Zero, new EventList([Note(0, 1)])) { Loop = new ClipLoop(Tick.Zero, new TickSpan(10)) }]);
        var sequence = Sequence.CreateEmpty(Ppqn.Default);
        for (var i = 0; i < 5; i++)
        {
            sequence = sequence.WithTrack(Dense());
        }

        Assert.Throws<ClipLoopLimitException>(() => sequence.WithTrack(Dense()));
    }

    [Fact]
    public void ALoopEndingBeyondTheTimelinesRange_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClipLoop(new Tick(long.MaxValue - 5), new TickSpan(10)));

    private static List<ClipPass> EnumeratePasses(Clip clip)
    {
        var passes = new List<ClipPass>();
        foreach (var pass in clip.Passes)
        {
            passes.Add(pass);
        }

        return passes;
    }
}
