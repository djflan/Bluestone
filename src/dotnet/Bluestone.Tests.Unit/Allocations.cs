namespace Bluestone.Tests.Unit;

/// <summary>Measures allocation on a path that must not allocate once warmed up.</summary>
internal static class Allocations
{
    /// <summary>
    /// Bytes allocated on this thread by the last round of <paramref name="steps"/> calls to <paramref name="step"/>: the
    /// first round from the third on that allocates nothing, or the tenth. A path that allocates every call fails every
    /// round; a one-time runtime allocation (tiered compilation, seen on CI runners) lands in one round only.
    /// </summary>
    public static long InSteadyState(Action step, int steps)
    {
        // Allocations made by the code repeat every round. One-time runtime work (a tier-up, a late static initializer) does
        // not, and on a slow or busy machine it can land in any round; so a round after the first that allocates nothing
        // ends the measurement.
        long allocated = 0;
        for (var round = 0; round < 10; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < steps; i++)
            {
                step();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round >= 2 && allocated == 0)
            {
                break;
            }
        }

        return allocated;
    }
}
