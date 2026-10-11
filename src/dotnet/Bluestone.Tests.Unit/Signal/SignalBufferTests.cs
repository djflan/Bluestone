using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

public sealed class SignalBufferTests
{
    [Fact]
    public void Add_GrowsAndKeepsOrder()
    {
        var buffer = new SignalBuffer(capacity: 1);
        var events = Keyed(0, Note(0), Note(10), Note(20));

        foreach (var e in events)
        {
            buffer.Add(e);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(events, buffer.Events.ToArray());
    }

    [Fact]
    public void Clear_KeepsCapacity()
    {
        var buffer = new SignalBuffer();
        buffer.AddRange(Keyed(0, [.. Enumerable.Range(0, 200).Select(i => (TrackEvent)Note(i))]));
        var capacity = buffer.Capacity;

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(capacity, buffer.Capacity);
    }

    [Fact]
    public void StableSort_OrdersByPositionPhaseOriginAndIndex_AndKeepsTiesInPlace()
    {
        var note = Note(10);
        var release = new NoteOffEvent(EventId.New(), new Tick(10), note.Channel, note.Note, note.ReleaseVelocity);
        var a = new SignalEvent(note, 1, 0);
        var b = new SignalEvent(release, 1, 1);
        var c = new SignalEvent(Note(10, 64), 0, 5);
        var d = new SignalEvent(Note(0), 2, 9);
        var tieFirst = new SignalEvent(Note(20, 60), 0, 0);
        var tieSecond = new SignalEvent(Note(20, 72), 0, 0);
        SignalEvent[] events = [a, tieFirst, b, tieSecond, c, d];

        SignalOrder.StableSort(events);

        SignalEvent[] expected = [d, b, c, a, tieFirst, tieSecond];
        Assert.Equal(expected, events);
    }

    [Fact]
    public void Merge_PutsTheFirstSpanFirstOnEqualKeys()
    {
        var shared = Note(0);
        SignalEvent[] first = [new(shared, 0, 0)];
        SignalEvent[] second = [new(Note(0, 72), 0, 0), new(Note(5), 0, 1)];
        var output = new SignalBuffer();

        SignalOrder.Merge(first, second, output);

        Assert.Equal([first[0], second[0], second[1]], output.Events.ToArray());
    }

    [Fact]
    public void SignalBlock_EndingBeforeItStarts_Throws() =>
        Assert.Throws<ArgumentException>(() => new SignalBlock(new Tick(10), new Tick(5)));
}
