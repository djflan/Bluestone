using Bluestone.Domain.Devices;
using Bluestone.Signal;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

/// <summary>Note activity per chain junction (ADR 0037): what lights a Device View's note lamps during playback.</summary>
public sealed class NoteActivityTests
{
    [Fact]
    public void Ranges_MergeWhereNotesTouchOrOverlap_AndEndExclusive()
    {
        var activity = NoteActivity.Of(Keyed(0, Note(0, length: 100), Note(50, length: 100), Note(150, length: 10), Note(400, length: 1), Volume(200, 64)));

        Assert.Equal([(0L, 160L), (400L, 401L)], activity.Ranges);
        Assert.True(activity.IsActiveAt(0));
        Assert.True(activity.IsActiveAt(159));
        Assert.False(activity.IsActiveAt(160));
        Assert.False(activity.IsActiveAt(-1));
        Assert.True(activity.IsActiveAt(400));
        Assert.False(activity.IsActiveAt(401));
        Assert.Same(NoteActivity.None, NoteActivity.Of(Keyed(0, Volume(0, 1))));
        Assert.False(NoteActivity.None.IsActiveAt(0));
    }

    [Fact]
    public void TheSignalGraph_RecordsNotesAtEveryJunction_AsTheyAreThere()
    {
        // The input has the clip's note; after the arpeggiator the steps; the filter blocks notes, so after it none.
        var keys = TrackOf("Keys", Note(0, 60, length: 960));
        var arp = BuiltInDevices.CreateArpeggiator(rate: 2);
        var filter = BuiltInDevices.CreateEventFilter(Bluestone.Domain.Sequencing.EventClass.Notes);
        var project = NewProject().With(keys, arp, filter);
        var chain = project.ChainOf(keys.Id)!;

        var result = project.Evaluate();

        Assert.Equal([(0L, 960L)], result.NoteActivity[chain.Id.Value].Ranges);
        Assert.True(result.NotesAt(arp.Id.Value, 10));
        Assert.True(result.NoteActivity[arp.Id.Value].Count >= 1);
        Assert.False(result.NotesAt(filter.Id.Value, 10));
        Assert.False(result.NotesAt(chain.Id.Value, 960));
    }

    [Fact]
    public void AMutedTrack_HoldsNoNotesAnywhere()
    {
        var keys = TrackOf("Keys", Note(0, 60, length: 960)).WithMuted(true);
        var project = NewProject().With(keys, BuiltInDevices.CreateTranspose(5));

        Assert.False(project.Evaluate().NotesAt(project.ChainOf(keys.Id)!.Id.Value, 10));
    }
}
