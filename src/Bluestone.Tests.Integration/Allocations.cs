namespace Bluestone.Tests.Integration;

/// <summary>Measures allocation on a path that must not allocate once warmed up.</summary>
internal static class Allocations
{
    /// <summary>
    /// Bytes allocated on this thread by the last of three rounds of <paramref name="steps"/> calls to
    /// <paramref name="step"/>. A path that allocates every call fails every round; a one-time runtime allocation (tiered
    /// compilation, seen on CI runners) lands in an earlier round instead of failing the test.
    /// </summary>
    public static long InSteadyState(Action step, int steps)
    {
        long allocated = 0;
        for (var round = 0; round < 3; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < steps; i++)
            {
                step();
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        return allocated;
    }
}
