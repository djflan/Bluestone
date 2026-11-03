using System.Diagnostics;

namespace Bluestone.Plugins;

/// <summary>
/// How long plugin collects may still wait on this thread: a deadline the audio engine sets for each block it renders in
/// the engine process (ADR 0044 step 7), shared by every plugin collected in that block, so the waits of several plugins
/// (the instrument played live at depth 1 and the MIDI effects on its route, say) add up to no more than the budget, and
/// a late worker can never hold the device callback past it. A thread with no deadline (the live thread, Bluestone's own
/// process, offline rendering) waits each plugin's own collect wait. Lock-free and allocation-free.
/// </summary>
public static class CollectBudget
{
    [ThreadStatic]
    private static long _deadline;

    /// <summary>Collects on this thread end by <paramref name="deadline"/> (a <see cref="Stopwatch"/> timestamp) until <see cref="End"/>.</summary>
    public static void Begin(long deadline) => _deadline = deadline;

    /// <summary>Collects on this thread wait their own collect wait again.</summary>
    public static void End() => _deadline = 0;

    /// <summary><paramref name="wait"/>, cut to what is left before this thread's deadline (zero when it has passed).</summary>
    public static TimeSpan Bound(TimeSpan wait)
    {
        var deadline = _deadline;
        if (deadline == 0)
        {
            return wait;
        }

        var left = deadline - Stopwatch.GetTimestamp();
        if (left <= 0)
        {
            return TimeSpan.Zero;
        }

        var remaining = Stopwatch.GetElapsedTime(0, left);
        return remaining < wait ? remaining : wait;
    }
}
