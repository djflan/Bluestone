using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Bluestone.Platform.Windows;

/// <summary>Windows thread scheduling for time-critical threads.</summary>
[SupportedOSPlatform("windows")]
public static partial class ThreadScheduling
{
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

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);
}
