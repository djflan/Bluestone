using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CsCheck;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

public sealed class EventListTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, int note = 60, long length = 10) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber(note), new Velocity(100));

    private static ControllerEvent Cc(long at, ControllerNumber controller, int value = 0) =>
        new(new Tick(at), One, controller, ControlValue.FromSevenBit(value));

    private static ProgramEvent Program(long at, int program) =>
        new(new Tick(at), One, new ProgramSelection(new ProgramNumber(program)));

    [Fact]
    public void SimultaneousEvents_FollowCanonicalPhases()
    {
        var note = Note(0);
        var program = Program(0, 5);
        var bankLsb = Cc(0, ControllerNumber.BankSelectLsb);
        var volume = Cc(0, ControllerNumber.ChannelVolume, 100);
        var bankMsb = Cc(0, ControllerNumber.BankSelectMsb);
        var sysEx = new SysExEvent(Tick.Zero, SysExMessage.Create([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]));
        var meta = new MetaEvent(EventId.New(), Tick.Zero, 0x01, ByteBlock.Copy("hi"u8));

        var list = new EventList([note, program, bankLsb, volume, bankMsb, sysEx, meta]);

        Assert.Equal<TrackEvent>([meta, sysEx, bankLsb, bankMsb, program, volume, note], list.Items);
    }

    [Fact]
    public void Ties_PreserveSuppliedOrder()
    {
        // An RPN sequence must reach the device in the order written.
        var rpnMsb = Cc(0, ControllerNumber.RpnMsb);
        var rpnLsb = Cc(0, ControllerNumber.RpnLsb);
        var data = Cc(0, ControllerNumber.DataEntryMsb, 2);

        Assert.Equal<TrackEvent>([rpnMsb, rpnLsb, data], new EventList([rpnMsb, rpnLsb, data]).Items);
    }

    [Fact]
    public void Add_InsertsAfterTies()
    {
        var first = Cc(10, ControllerNumber.ModulationWheel, 1);
        var second = Cc(10, ControllerNumber.ModulationWheel, 2);
        var list = new EventList([Note(0), first, Note(20)]).Add(second);

        Assert.Equal(1, list.Items.IndexOf(first));
        Assert.Equal(2, list.Items.IndexOf(second));
    }

    [Fact]
    public void Replace_RepositionsEvent()
    {
        var moving = Note(0);
        var list = new EventList([moving, Note(50)]);

        var moved = moving with { Position = new Tick(100) };
        var updated = list.Replace(moved);

        Assert.Same(moved, updated.Items[^1]);
        Assert.Equal(2, updated.Items.Length);
        Assert.Equal(new Tick(110), updated.EndPosition);
        Assert.Throws<KeyNotFoundException>(() => list.Replace(Note(5)));
    }

    [Fact]
    public void Remove_IgnoresUnknownId()
    {
        var note = Note(0);
        var list = new EventList([note]);

        Assert.Empty(list.Remove(note.Id).Items);
        Assert.Same(list, list.Remove(EventId.New()));
    }

    [Fact]
    public void DuplicateEventIds_AreRejected()
    {
        var note = Note(0);

        Assert.Throws<ArgumentException>(() => new EventList([note, note with { Position = new Tick(5) }]));
        Assert.Throws<ArgumentException>(() => new EventList([note]).Add(note));
    }

    [Fact]
    public void EndPosition_IncludesNoteReleases()
    {
        Assert.Equal(Tick.Zero, EventList.Empty.EndPosition);
        Assert.Equal(new Tick(110), new EventList([Note(100), Cc(105, ControllerNumber.ModulationWheel)]).EndPosition);
    }

    private static readonly Gen<TrackEvent> GenEvent =
        Gen.OneOf<TrackEvent>(
            Gen.Select(Gen.Long[0, 20], Gen.Int[0, 127]).Select(x => (TrackEvent)Note(x.Item1, x.Item2)),
            Gen.Select(Gen.Long[0, 20], Gen.Int[0, 127]).Select(x => (TrackEvent)Cc(x.Item1, new ControllerNumber(x.Item2))),
            Gen.Select(Gen.Long[0, 20], Gen.Int[0, 127]).Select(x => (TrackEvent)Program(x.Item1, x.Item2)));

    [Fact]
    public void Items_AreAStableSortOfTheInput() =>
        GenEvent.Array[0, 40].Sample(events =>
        {
            var expected = events
                .Select((e, index) => (e, index))
                .OrderBy(x => x.e.Position).ThenBy(x => x.e.Phase).ThenBy(x => x.index)
                .Select(x => x.e);
            return new EventList(events).Items.SequenceEqual(expected);
        });

    [Fact]
    public void Add_MatchesConstructingFromAllEvents() =>
        GenEvent.Array[0, 30].Sample(events =>
            events.Aggregate(EventList.Empty, (list, e) => list.Add(e)).Items.SequenceEqual(new EventList(events).Items));
}
