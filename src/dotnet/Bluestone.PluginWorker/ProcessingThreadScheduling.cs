using System.Runtime.InteropServices;

namespace Bluestone.PluginWorker;

/// <summary>
/// Puts a plugin's processing thread on macOS's real-time time-constraint policy and gives it a short, precise pause
/// to wait in. A real-time thread that polls by yielding would use its whole budget and be demoted by the kernel, so
/// on macOS the thread waits in <c>nanosleep</c> slices instead: blocked, not computing, and woken within tens of
/// microseconds. Elsewhere it keeps .NET's thread priority and <see cref="Thread.Yield"/> (ADR 0043, live plugins).
/// </summary>
internal static partial class ProcessingThreadScheduling
{
    private const int TimeConstraintPolicy = 2;

    /// <summary>Whether <see cref="Pause"/> blocks precisely on this platform (macOS).</summary>
    public static bool HasPrecisePause => OperatingSystem.IsMacOS();

    /// <summary>Promotes the calling thread; false when the platform has no such policy or refused it.</summary>
    /// <param name="period">The block period: the thread may compute for half of it in every period.</param>
    public static bool TryPromoteCurrentThread(TimeSpan period)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var clamped = TimeSpan.FromTicks(Math.Clamp(period.Ticks, TimeSpan.FromMilliseconds(1).Ticks, TimeSpan.FromMilliseconds(50).Ticks));
            _ = mach_timebase_info(out var timebase);
            var numer = timebase.Numer == 0 ? 1UL : timebase.Numer;
            var denom = timebase.Denom == 0 ? 1UL : timebase.Denom;
            uint ToAbsolute(TimeSpan t) => (uint)((ulong)t.Ticks * 100 * denom / numer);
            var policy = new TimeConstraint
            {
                Period = 0,
                Computation = ToAbsolute(clamped / 2),
                Constraint = ToAbsolute(clamped),
                Preemptible = 1,
            };
            return thread_policy_set(pthread_mach_thread_np(pthread_self()), TimeConstraintPolicy, ref policy, 4) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Blocks for about <paramref name="microseconds"/> (macOS only; see <see cref="HasPrecisePause"/>).</summary>
    public static void Pause(int microseconds)
    {
        var request = new TimeSpec { Seconds = 0, Nanoseconds = microseconds * 1000L };
        _ = nanosleep(ref request, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeConstraint
    {
        public uint Period;
        public uint Computation;
        public uint Constraint;
        public int Preemptible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timebase
    {
        public uint Numer;
        public uint Denom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial nint pthread_self();

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial uint pthread_mach_thread_np(nint thread);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int thread_policy_set(uint thread, int flavor, ref TimeConstraint policy, uint count);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int mach_timebase_info(out Timebase info);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int nanosleep(ref TimeSpec request, nint remaining);
}
