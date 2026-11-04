using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bluestone.PluginWorker;

/// <summary>
/// Puts a plugin's processing thread on the platform's audio scheduling class and gives it a short pause to wait in.
/// A real-time thread that polls by yielding would use its whole budget and be demoted by the kernel, so macOS uses
/// <c>nanosleep</c> and Windows uses a high-resolution waitable timer. Elsewhere it keeps .NET's thread priority and
/// <see cref="Thread.Yield"/> (ADR 0043, live plugins).
/// </summary>
internal static partial class ProcessingThreadScheduling
{
    private const int TimeConstraintPolicy = 2;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = uint.MaxValue;

    [ThreadStatic]
    private static SafeWaitHandle? _preciseTimer;

    [ThreadStatic]
    private static bool _preciseTimerAttempted;

    /// <summary>Whether <see cref="Pause"/> blocks precisely on this platform.</summary>
    public static bool HasPrecisePause => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>Promotes the calling thread; false when the platform has no such policy or refused it.</summary>
    /// <param name="period">The block period: the thread may compute for half of it in every period.</param>
    public static bool TryPromoteCurrentThread(TimeSpan period)
    {
        if (OperatingSystem.IsWindows())
        {
            uint taskIndex = 0;
            return AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex) != IntPtr.Zero;
        }

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

    /// <summary>Blocks for about <paramref name="microseconds"/> (see <see cref="HasPrecisePause"/>).</summary>
    public static void Pause(int microseconds)
    {
        if (OperatingSystem.IsWindows())
        {
            PauseOnWindows(TimeSpan.FromMicroseconds(Math.Max(1, microseconds)));
            return;
        }

        if (!OperatingSystem.IsMacOS())
        {
            Thread.Yield();
            return;
        }

        var request = new TimeSpec { Seconds = 0, Nanoseconds = microseconds * 1000L };
        _ = nanosleep(ref request, 0);
    }

    private static void PauseOnWindows(TimeSpan duration)
    {
        if (!_preciseTimerAttempted)
        {
            var handle = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            _preciseTimer = handle == IntPtr.Zero ? null : new SafeWaitHandle(handle, ownsHandle: true);
            _preciseTimerAttempted = true;
        }

        if (_preciseTimer is { IsInvalid: false } timer)
        {
            var dueTime = -Math.Max(1, duration.Ticks);
            var handle = timer.DangerousGetHandle();
            if (SetWaitableTimerEx(handle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)
                && WaitForSingleObject(handle, Infinite) == 0)
            {
                return;
            }
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var spinner = default(SpinWait);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }
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

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimerEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

}
