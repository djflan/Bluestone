using System.Collections.Immutable;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

internal static class Search
{
    /// <summary>The index of the last item positioned at or before <paramref name="position"/>, or -1, in items sorted by position.</summary>
    public static int LastAtOrBefore<T>(ImmutableArray<T> items, Tick position, Func<T, Tick> positionOf)
    {
        int low = 0, high = items.Length - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (positionOf(items[mid]) <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}
