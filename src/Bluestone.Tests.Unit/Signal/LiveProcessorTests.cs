using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal.Live;
using CsCheck;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

/// <summary>
/// The live (real-time) built-in processors against their plan-time twins: the same input gives the same notes, in
/// any block sizes (ADR 0035). Notes are compared as (start, end, channel, pitch, velocity); other events as they are.
/// </summary>
public sealed class LiveProcessorTests
{
    private static readonly Gen<(long Start, int Length, int Pitch, int Channel, int Velocity)[]> NotesGen =
        Gen.Select(Gen.Long[0, 3000], Gen.Int[1, 700], Gen.Int[40, 90], Gen.Int[0, 1], Gen.Int[1, 127]).Array[1, 14];

    private static readonly Gen<(long At, int Controller, int Value)[]> ControllersGen =
        Gen.Select(Gen.Long[0, 3000], Gen.Int[1, 10], Gen.Int[0, 127]).Array[0, 5];

    private static readonly Gen<int[]> BlocksGen = Gen.Int[1, 400].Array[1, 6];

    [Fact]
    public void Arpeggiator_Live_PlaysTheSameNotesAsAtPlanTime_InAnyBlocks()
    {
        Gen.Select(NotesGen, Gen.Select(Gen.Int[0, 3], Gen.Int[0, 2], Gen.Int[1, 3], Gen.Double[0.05, 1]), BlocksGen).Sample((notes, settings, blocks) =>
        {
            var (rate, pattern, octaves, gate) = settings;
            var device = BuiltInDevices.CreateArpeggiator(rate, (ArpeggiatorPattern)pattern, octaves, gate);
            return SameNotes([device], Playable(notes), [], blocks);
        }, iter: 2000);
    }

    [Fact]
    public void Transpose_Live_PlaysTheSameAsAtPlanTime_InAnyBlocks()
    {
        Gen.Select(NotesGen, ControllersGen, Gen.Int[-48, 48], BlocksGen).Sample((notes, controllers, semitones, blocks) =>
            SameNotes([BuiltInDevices.CreateTranspose(semitones)], Playable(notes), controllers, blocks));
    }

    [Fact]
    public void EventFilter_Live_PassesTheSameAsAtPlanTime_InAnyBlocks()
    {
        Gen.Select(NotesGen, ControllersGen, Gen.Int[0, 15], BlocksGen).Sample((notes, controllers, blocked, blocks) =>
            SameNotes([BuiltInDevices.CreateEventFilter((EventClass)blocked & EventClass.Transmitted)], Playable(notes), controllers, blocks));
    }

    [Fact]
    public void AChainOfBuiltIns_Live_PlaysTheSameAsAtPlanTime_InAnyBlocks()
    {
        Gen.Select(NotesGen, ControllersGen, Gen.Select(Gen.Int[-24, 24], Gen.Int[0, 3], Gen.Int[0, 2]), BlocksGen).Sample((notes, controllers, settings, blocks) =>
        {
            var (semitones, rate, pattern) = settings;
            DeviceInstance[] devices = [BuiltInDevices.CreateTranspose(semitones), BuiltInDevices.CreateArpeggiator(rate, (ArpeggiatorPattern)pattern), BuiltInDevices.CreateEventFilter(EventClass.Programs)];
            return SameNotes(devices, Playable(notes), controllers, blocks);
        }, iter: 1000);
    }

    [Fact]
    public void Arpeggiator_Live_StepsEveryStepLength_WhileAKeyIsHeld_AndStopsAtTheRelease()
    {
        var chain = LiveChainOf(BuiltInDevices.CreateArpeggiator(rate: 2));
        var output = new LiveEventBuffer();

        // Sixteenths at 480 PPQN are 120 ticks; the chord is held from tick 10 to tick 500, played in 32-tick blocks.
        Feed(chain, output, 0, 640, 32, [LiveEvent.NoteOn(10, 0, 64, 90), LiveEvent.NoteOn(10, 0, 60, 80), LiveEvent.NoteOff(500, 0, 60), LiveEvent.NoteOff(500, 0, 64)]);

        var ons = output.Events.ToArray().Where(e => e.IsNoteOn).ToList();
        Assert.Equal([10L, 130, 250, 370, 490], ons.Select(e => e.Tick));
        Assert.Equal([60, 64, 60, 64, 60], ons.Select(e => (int)e.Data1));
        var offs = output.Events.ToArray().Where(e => e.IsNoteOff).ToList();
        Assert.Equal([70L, 190, 310, 430, 550], offs.Select(e => e.Tick));
    }

    [Fact]
    public void Arpeggiator_Release_EndsTheNoteItIsPlaying_AndForgetsTheKeys()
    {
        var arpeggiator = new LiveArpeggiatorProcessor(Resolution);
        var output = new LiveEventBuffer();
        arpeggiator.Process(new LiveBlock(0, 30), [LiveEvent.NoteOn(0, 0, 60, 100)], output);
        Assert.Equal(1, arpeggiator.Sounding);

        arpeggiator.Release(new LiveBlock(30, 60), output);
        arpeggiator.Process(new LiveBlock(30, 1000), [], output);

        Assert.Equal([LiveEvent.NoteOn(0, 0, 60, 100), LiveEvent.NoteOff(30, 0, 60)], output.Events.ToArray());
        Assert.Equal(0, arpeggiator.Sounding);
    }

    [Fact]
    public void Transpose_ChangedWhileAKeyIsHeld_ReleasesTheNoteItStarted()
    {
        var transpose = new LiveTransposeProcessor();
        transpose.SetParameter(BuiltInDevices.TransposeSemitones, BuiltInDevices.Transpose.Parameters[0].ToStored(12));
        var output = new LiveEventBuffer();
        transpose.Process(new LiveBlock(0, 10), [LiveEvent.NoteOn(0, 0, 60, 100)], output);

        transpose.SetParameter(BuiltInDevices.TransposeSemitones, BuiltInDevices.Transpose.Parameters[0].ToStored(-5));
        transpose.Process(new LiveBlock(10, 20), [LiveEvent.NoteOff(15, 0, 60)], output);

        Assert.Equal([72, 72], output.Events.ToArray().Select(e => (int)e.Data1));
    }

    [Fact]
    public void EventFilter_BlockingNotesWhileAKeyIsHeld_StillLetsItsReleaseThrough()
    {
        var filter = new LiveEventFilterProcessor();
        var output = new LiveEventBuffer();
        filter.Process(new LiveBlock(0, 10), [LiveEvent.NoteOn(0, 0, 60, 100)], output);

        filter.SetParameter(BuiltInDevices.FilterNotes, ControlValue.Min);
        filter.Process(new LiveBlock(10, 20), [LiveEvent.NoteOn(12, 0, 62, 100), LiveEvent.NoteOff(15, 0, 60), LiveEvent.NoteOff(16, 0, 62)], output);

        Assert.Equal([LiveEvent.NoteOn(0, 0, 60, 100), LiveEvent.NoteOff(15, 0, 60)], output.Events.ToArray());
    }

    [Fact]
    public void Chain_Release_PassesEachStagesReleasesThroughTheStagesAfterIt()
    {
        // The arpeggiator's sounding note is transposed on the way out, so its release must be too.
        var chain = LiveChainOf(BuiltInDevices.CreateArpeggiator(rate: 0), BuiltInDevices.CreateTranspose(7));
        var output = new LiveEventBuffer();
        chain.Process(new LiveBlock(0, 100), [LiveEvent.NoteOn(0, 2, 60, 100)], output);

        chain.Release(new LiveBlock(100, 200), output);

        Assert.Equal([LiveEvent.NoteOn(0, 2, 67, 100), LiveEvent.NoteOff(100, 2, 67)], output.Events.ToArray());
    }

    [Fact]
    public void Chain_InSteadyState_AllocatesNothing()
    {
        var chain = LiveChainOf(BuiltInDevices.CreateTranspose(3), BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.UpDown, 2), BuiltInDevices.CreateEventFilter(EventClass.Programs));
        var output = new LiveEventBuffer();
        LiveEvent[] chord = [LiveEvent.NoteOn(0, 0, 60, 90), LiveEvent.NoteOn(0, 0, 64, 90), new LiveEvent(0, LiveEventKind.ControlChange, 0, 1, 64)];
        var tick = 0L;
        void RunOnce()
        {
            output.Clear();
            chain.Process(new LiveBlock(tick, tick + 64), chord.Select(e => e.At(tick)).ToArray(), output);
            chain.Process(new LiveBlock(tick + 64, tick + 512), [], output);
            chain.Release(new LiveBlock(tick + 512, tick + 513), output);
            tick += 1000;
        }

        RunOnce();
        var input = new LiveEvent[3];
        void Step()
        {
            output.Clear();
            for (var k = 0; k < chord.Length; k++)
            {
                input[k] = chord[k].At(tick);
            }

            chain.Process(new LiveBlock(tick, tick + 64), input, output);
            chain.Process(new LiveBlock(tick + 64, tick + 512), [], output);
            chain.Release(new LiveBlock(tick + 512, tick + 513), output);
            tick += 1000;
        }

        Assert.Equal(0, Allocations.InSteadyState(Step, 100));
    }

    private static LiveChain LiveChainOf(params DeviceInstance[] devices) =>
        new(devices.Select(d => Catalog.CreateLiveStage(d, Resolution)!.Value));

    // Notes a keyboard can play: a key is not struck again before its release.
    private static NoteEvent[] Playable((long Start, int Length, int Pitch, int Channel, int Velocity)[] notes)
    {
        var kept = new List<NoteEvent>();
        foreach (var (start, length, pitch, channel, velocity) in notes.OrderBy(n => n.Start))
        {
            var end = start + length;
            if (kept.Any(k => k.Note.Value == pitch && k.Channel.Index == channel && k.Position.Value < end && k.EndPosition.Value > start))
            {
                continue;
            }

            kept.Add(new NoteEvent(new Tick(start), new TickSpan(length), MidiChannel.FromIndex(channel), new NoteNumber(pitch), new Velocity(velocity)));
        }

        return [.. kept];
    }

    private static bool SameNotes(DeviceInstance[] devices, NoteEvent[] notes, (long At, int Controller, int Value)[] controllers, int[] blocks)
    {
        TrackEvent[] events = [.. notes, .. controllers.Select(c => (TrackEvent)new ControllerEvent(new Tick(c.At), MidiChannel.FromIndex(0), new ControllerNumber(c.Controller), ControlValue.FromSevenBit(c.Value)))];
        var ordered = events.OrderBy(e => e.Position.Value).ToArray();
        var planned = Run(Chain(devices), Keyed(0, ordered));
        var expected = Describe(planned.Select(e => e.Event));

        // Live: note-ons and note-offs at their ticks; releases before strikes at one tick.
        var live = ordered.SelectMany(e => e switch
        {
            NoteEvent n => new[] { (Rank: 1, Event: LiveEvent.NoteOn(n.Position.Value, n.Channel.Index, n.Note.Value, n.Velocity.Value)), (Rank: 0, Event: LiveEvent.NoteOff(n.EndPosition.Value, n.Channel.Index, n.Note.Value)) },
            ControllerEvent c => [(Rank: 1, Event: new LiveEvent(c.Position.Value, LiveEventKind.ControlChange, c.Channel.Index, c.Controller.Value, (byte)c.Value.ToSevenBit()))],
            _ => [],
        }).Select((x, i) => (x.Rank, x.Event, Order: i)).OrderBy(x => x.Event.Tick).ThenBy(x => x.Rank).ThenBy(x => x.Order).Select(x => x.Event).ToArray();

        var chain = LiveChainOf(devices);
        var output = new LiveEventBuffer(4096);
        var tick = 0L;
        var b = 0;
        while (tick < 6000)
        {
            var length = blocks[b++ % blocks.Length];
            var block = new LiveBlock(tick, tick + length);
            chain.Process(block, live.Where(e => e.Tick >= block.StartTick && e.Tick < block.EndTick).ToArray(), output);
            tick += length;
        }

        var actual = Describe(Paired(output.Events.ToArray()));
        return expected.SequenceEqual(actual);
    }

    private static void Feed(LiveChain chain, LiveEventBuffer output, long from, long to, int blockTicks, LiveEvent[] input)
    {
        for (var tick = from; tick < to; tick += blockTicks)
        {
            var block = new LiveBlock(tick, tick + blockTicks);
            chain.Process(block, input.Where(e => e.Tick >= block.StartTick && e.Tick < block.EndTick).ToArray(), output);
        }
    }

    // Live output back into plan-shaped events: each note-on with the first later note-off of its key.
    private static IEnumerable<TrackEvent> Paired(LiveEvent[] events)
    {
        var open = new Dictionary<int, Queue<LiveEvent>>();
        foreach (var e in events)
        {
            if (e.IsNoteOn)
            {
                if (!open.TryGetValue(e.NoteSlot, out var queue))
                {
                    open[e.NoteSlot] = queue = new Queue<LiveEvent>();
                }

                queue.Enqueue(e);
            }
            else if (e.IsNoteOff)
            {
                if (open.TryGetValue(e.NoteSlot, out var queue) && queue.TryDequeue(out var on))
                {
                    yield return new NoteEvent(new Tick(on.Tick), new TickSpan(e.Tick - on.Tick), MidiChannel.FromIndex(on.Channel), new NoteNumber(on.Data1), new Velocity(on.Data2));
                }
                else
                {
                    yield return new NoteOffEvent(EventId.New(), new Tick(e.Tick), MidiChannel.FromIndex(e.Channel), new NoteNumber(e.Data1), new Velocity(e.Data2));
                }
            }
            else if (e.Kind == LiveEventKind.ControlChange)
            {
                yield return new ControllerEvent(new Tick(e.Tick), MidiChannel.FromIndex(e.Channel), new ControllerNumber(e.Data1), ControlValue.FromSevenBit(e.Data2));
            }
        }

        foreach (var on in open.Values.SelectMany(q => q))
        {
            yield return new NoteEvent(new Tick(on.Tick), new TickSpan(1_000_000), MidiChannel.FromIndex(on.Channel), new NoteNumber(on.Data1), new Velocity(on.Data2));
        }
    }

    private static ImmutableArray<string> Describe(IEnumerable<TrackEvent> events) =>
        [.. events.Select(e => e switch
        {
            NoteEvent n => $"note {n.Position.Value}-{n.EndPosition.Value} ch{n.Channel.Index} {n.Note.Value} v{n.Velocity.Value}",
            NoteOffEvent n => $"off {n.Position.Value} ch{n.Channel.Index} {n.Note.Value}",
            ControllerEvent c => $"cc {c.Position.Value} ch{c.Channel.Index} {c.Controller.Value}={c.Value.ToSevenBit()}",
            _ => e.ToString()!,
        }).Order(StringComparer.Ordinal)];
}
