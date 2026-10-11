using System.Collections.Immutable;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>
/// An immutable list of events in canonical order with unique <see cref="EventId"/>s. Every edit returns
/// a new list.
/// </summary>
/// <remarks>
/// Events are kept sorted by position, then <see cref="EventPhase"/>. Events that tie on both keep
/// the order in which they were supplied or inserted (a newly inserted event goes after its ties).
/// </remarks>
public sealed class EventList
{
    // Computed on first use; -1 until then. A long is written atomically, so concurrent readers at
    // worst compute it twice.
    private long _endPosition = -1;

    private EventList(ImmutableArray<TrackEvent> items) => Items = items;

    /// <exception cref="ArgumentException">Two events share an <see cref="EventId"/>.</exception>
    public EventList(IEnumerable<TrackEvent> events)
        : this(Canonicalize(events))
    {
    }

    public static EventList Empty { get; } = new(ImmutableArray<TrackEvent>.Empty);

    public ImmutableArray<TrackEvent> Items { get; }

    /// <summary>The latest tick affected by any event (note releases included), or zero if empty.</summary>
    public Tick EndPosition
    {
        get
        {
            if (_endPosition < 0)
            {
                _endPosition = Items.IsEmpty ? 0 : Items.Max(e => e.EndPosition).Value;
            }

            return new Tick(_endPosition);
        }
    }

    public TrackEvent? Find(EventId id)
    {
        var index = IndexOf(id);
        return index < 0 ? null : Items[index];
    }

    /// <exception cref="ArgumentException">An event with the same ID already exists.</exception>
    public EventList Add(TrackEvent trackEvent)
    {
        ArgumentNullException.ThrowIfNull(trackEvent);
        if (IndexOf(trackEvent.Id) >= 0)
        {
            throw new ArgumentException($"Event {trackEvent.Id} is already on this track.", nameof(trackEvent));
        }

        return new EventList(Items.Insert(UpperBound(Items, trackEvent), trackEvent));
    }

    /// <summary>Removes the event with <paramref name="id"/>, or returns this list unchanged if absent.</summary>
    public EventList Remove(EventId id)
    {
        var index = IndexOf(id);
        return index < 0 ? this : new EventList(Items.RemoveAt(index));
    }

    /// <summary>Replaces the event with the same ID, re-sorting it as a fresh insertion.</summary>
    /// <exception cref="KeyNotFoundException">No event has that ID.</exception>
    public EventList Replace(TrackEvent trackEvent)
    {
        ArgumentNullException.ThrowIfNull(trackEvent);
        var index = IndexOf(trackEvent.Id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Event {trackEvent.Id} is not on this track.");
        }

        var without = Items.RemoveAt(index);
        return new EventList(without.Insert(UpperBound(without, trackEvent), trackEvent));
    }

    private int IndexOf(EventId id)
    {
        for (var i = 0; i < Items.Length; i++)
        {
            if (Items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static int UpperBound(ImmutableArray<TrackEvent> events, TrackEvent candidate)
    {
        int low = 0, high = events.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (EventOrder.Compare(events[mid], candidate) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static ImmutableArray<TrackEvent> Canonicalize(IEnumerable<TrackEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.ToList();
        var ids = new HashSet<EventId>();
        foreach (var e in list)
        {
            ArgumentNullException.ThrowIfNull(e, nameof(events));
            if (!ids.Add(e.Id))
            {
                throw new ArgumentException($"Event {e.Id} appears more than once.", nameof(events));
            }
        }

        // OrderBy is stable, preserving supplied order among ties.
        return [.. list.OrderBy(e => e.Position).ThenBy(e => e.Phase)];
    }
}
