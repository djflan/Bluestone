using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Bluestone.Platform.Windows;

/// <summary>Windows thread scheduling for time-critical threads.</summary>
[SupportedOSPlatform("windows")]
public static partial class ThreadScheduling
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = uint.MaxValue;

    [ThreadStatic]
    private static SafeWaitHandle? _preciseTimer;

    [ThreadStatic]
    private static bool _preciseTimerAttempted;

    /// <summary>
    /// Requests a 1 ms system timer resolution for the life of the process. Without it, timed waits
    /// wake on the default 15.6 ms tick, which made immediate-delivery playback 10-20 ms late
    /// (ADR 0013). Windows releases the request when the process exits. Returns false if refused.
    /// </summary>
    public static bool RequestHighTimerResolution() => timeBeginPeriod(1) == 0;

    /// <summary>
    /// Registers the calling thread with the Multimedia Class Scheduler Service as a "Pro Audio"
    /// task, which raises its priority the way audio engines do. Returns false if the service refused.
    /// </summary>
    public static bool JoinProAudioTask()
    {
        uint taskIndex = 0;
        return AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex) != IntPtr.Zero;
    }

    /// <summary>Sleeps with a high-resolution waitable timer, spinning only if the OS does not support one.</summary>
    public static void SleepPrecisely(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

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

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", EntryPoint = "SetWaitableTimerEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
}
