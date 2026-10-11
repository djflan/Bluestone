using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal;

/// <summary>
/// An event travelling through the signal graph, with the key that fixes its place among simultaneous
/// events. <see cref="Origin"/> is the index of the track whose content it came from and
/// <see cref="Sequence"/> its index among that track's rendered events (negative for events placed
/// ahead of them). Devices keep the key of the event they transform or derive from, so merging streams
/// stays deterministic and matches the playback plan's order (ADR 0003).
/// </summary>
public readonly record struct SignalEvent(TrackEvent Event, int Origin, int Sequence)
{
    /// <summary>The same place in the order, carrying <paramref name="replacement"/>.</summary>
    public SignalEvent With(TrackEvent replacement) => this with { Event = replacement };
}

/// <summary>Canonical order of signal events: position, phase, origin track, then index within the origin.</summary>
public static class SignalOrder
{
    /// <summary>
    /// Orders as <see cref="EventOrder.Compare"/> does, then by origin and index. Phases are compared as
    /// integers: comparing the enum values directly would box them, and this runs for every event.
    /// </summary>
    public static int Compare(in SignalEvent left, in SignalEvent right)
    {
        var byPosition = left.Event.Position.Value.CompareTo(right.Event.Position.Value);
        if (byPosition != 0)
        {
            return byPosition;
        }

        var byPhase = ((int)left.Event.Phase).CompareTo((int)right.Event.Phase);
        if (byPhase != 0)
        {
            return byPhase;
        }

        var byOrigin = left.Origin.CompareTo(right.Origin);
        return byOrigin != 0 ? byOrigin : left.Sequence.CompareTo(right.Sequence);
    }

    public static bool IsOrdered(ReadOnlySpan<SignalEvent> events)
    {
        for (var i = 1; i < events.Length; i++)
        {
            if (Compare(events[i - 1], events[i]) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Sorts in place, keeping the given order among equal keys. An insertion sort: it allocates nothing
    /// and is linear on input that is already in order, which is what processors almost always produce.
    /// </summary>
    public static void StableSort(Span<SignalEvent> events)
    {
        for (var i = 1; i < events.Length; i++)
        {
            var item = events[i];
            var j = i - 1;
            while (j >= 0 && Compare(events[j], item) > 0)
            {
                events[j + 1] = events[j];
                j--;
            }

            events[j + 1] = item;
        }
    }

    /// <summary>Merges two ordered spans into <paramref name="output"/>; on equal keys, <paramref name="first"/> goes first.</summary>
    public static void Merge(ReadOnlySpan<SignalEvent> first, ReadOnlySpan<SignalEvent> second, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.EnsureCapacity(output.Count + first.Length + second.Length);
        int i = 0, j = 0;
        while (i < first.Length || j < second.Length)
        {
            if (j == second.Length || (i < first.Length && Compare(first[i], second[j]) <= 0))
            {
                output.Add(first[i++]);
            }
            else
            {
                output.Add(second[j++]);
            }
        }
    }
}
