using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Platform.CoreMidi;

/// <summary>macOS thread scheduling for time-critical threads.</summary>
[SupportedOSPlatform("macos")]
public static partial class ThreadScheduling
{
    private const uint QosClassUserInteractive = 0x21;

    /// <summary>
    /// Raises the calling thread to the user-interactive quality-of-service class, so the scheduler
    /// favours it and does not coalesce its timers. Returns false if the system refused.
    /// </summary>
    public static bool PromoteCurrentThread() => pthread_set_qos_class_self_np(QosClassUserInteractive, 0) == 0;

    /// <summary>
    /// Gives the calling thread the real-time "time constraint" policy used by audio threads: it may
    /// run for up to <paramref name="computation"/> within each <paramref name="constraint"/> window.
    /// A thread that exceeds its budget is demoted by the kernel, so callers must block rather than spin.
    /// </summary>
    public static unsafe bool MakeCurrentThreadRealtime(TimeSpan computation, TimeSpan constraint)
    {
        mach_timebase_info(out var numer, out var denom);
        uint ToAbsolute(TimeSpan t) => (uint)((ulong)t.Ticks * 100 * denom / numer);
        var policy = stackalloc uint[4];
        policy[0] = 0; // period: aperiodic
        policy[1] = ToAbsolute(computation);
        policy[2] = ToAbsolute(constraint);
        policy[3] = 1; // preemptible
        return thread_policy_set(mach_thread_self(), TimeConstraintPolicy, policy, 4) == 0;
    }

    private const int TimeConstraintPolicy = 2;

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial uint mach_thread_self();

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static unsafe partial int thread_policy_set(uint thread, int flavor, uint* policy, uint count);

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "mach_timebase_info")]
    private static partial int mach_timebase_info_raw(out ulong packed);

    private static void mach_timebase_info(out ulong numer, out ulong denom)
    {
        _ = mach_timebase_info_raw(out var packed);
        numer = packed & 0xFFFFFFFF;
        denom = packed >> 32;
        if (numer == 0 || denom == 0)
        {
            numer = denom = 1;
        }
    }
}
