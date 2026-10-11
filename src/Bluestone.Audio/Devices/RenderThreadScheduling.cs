using System.Runtime.InteropServices;

namespace Bluestone.Audio.Devices;

/// <summary>
/// Raises the calling thread (the buffered mode's .NET render-ahead thread) to the operating system's audio priority
/// where an ordinary user process may: macOS's real-time time-constraint policy, the Windows MMCSS "Pro Audio" task,
/// and on Linux <c>SCHED_FIFO</c>, which needs an rtprio limit (PipeWire and JACK set-ups grant one; otherwise it is
/// refused and the thread stays at normal priority). .NET's own <see cref="ThreadPriority"/> only has an effect on
/// Windows. The device callback thread is the backend's own real-time thread and is not changed here.
/// </summary>
internal static partial class RenderThreadScheduling
{
    private const int TimeConstraintPolicy = 2;
    private const int SchedFifo = 1;

    /// <summary>Promotes the calling thread; returns what was done, in words.</summary>
    /// <param name="period">How long the queue the thread keeps filled lasts: its real-time budget is based on it.</param>
    public static string PromoteCurrentThread(TimeSpan period)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return PromoteOnMacOS(period);
            }

            if (OperatingSystem.IsWindows())
            {
                uint task = 0;
                return AvSetMmThreadCharacteristicsW("Pro Audio", ref task) != 0
                    ? "MMCSS \"Pro Audio\" task"
                    : "highest thread priority (MMCSS refused)";
            }

            if (OperatingSystem.IsLinux())
            {
                var priority = Math.Clamp(60, sched_get_priority_min(SchedFifo), sched_get_priority_max(SchedFifo));
                var parameters = new SchedParam { Priority = priority };
                var error = pthread_setschedparam(pthread_self(), SchedFifo, ref parameters);
                return error == 0
                    ? $"SCHED_FIFO priority {priority}"
                    : $"normal priority (SCHED_FIFO refused, error {error}: the user needs an rtprio limit)";
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return $"normal priority ({ex.Message})";
        }

        return "normal priority";
    }

    // Aperiodic, may run for half the period in every period, preemptible. A thread that overruns is demoted by the
    // kernel; this one blocks in native code between bursts, so it stays well inside the budget (ADR 0012).
    private static string PromoteOnMacOS(TimeSpan period)
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
        return thread_policy_set(pthread_mach_thread_np(pthread_self()), TimeConstraintPolicy, ref policy, 4) == 0
            ? $"real-time (time constraint {clamped.TotalMilliseconds / 2:0.#} of {clamped.TotalMilliseconds:0.#} ms)"
            : "normal priority (the time-constraint policy was refused)";
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
    private struct SchedParam
    {
        public int Priority;
    }

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "pthread_self")]
    private static partial nint MacPthreadSelf();

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial uint pthread_mach_thread_np(nint thread);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int thread_policy_set(uint thread, int flavor, ref TimeConstraint policy, uint count);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int mach_timebase_info(out Timebase info);

    [LibraryImport("libc.so.6", EntryPoint = "pthread_self")]
    private static partial nint LinuxPthreadSelf();

    [LibraryImport("libc.so.6")]
    private static partial int pthread_setschedparam(nint thread, int policy, ref SchedParam parameters);

    [LibraryImport("libc.so.6")]
    private static partial int sched_get_priority_min(int policy);

    [LibraryImport("libc.so.6")]
    private static partial int sched_get_priority_max(int policy);

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    private static nint pthread_self() => OperatingSystem.IsMacOS() ? MacPthreadSelf() : LinuxPthreadSelf();
}
