using System.Diagnostics;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Platform.CoreMidi;

namespace Bluestone.Desktop.Services;

/// <summary>Creates the operating system's MIDI providers available on this machine.</summary>
internal static class PlatformProviders
{
    /// <summary>The virtual port Bluestone publishes so other applications can receive from it.</summary>
    public const string VirtualOutputName = "Bluestone Out";

    public static IEnumerable<IMidiEndpointProvider> Create(IMonotonicClock clock)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var coreMidi = new CoreMidiProvider(clock);
                coreMidi.CreateVirtualOutput(VirtualOutputName);
                return [coreMidi];
            }
            catch (EndpointUnavailableException ex)
            {
                // Bluestone still works with its built-in monitor; the reason is visible in diagnostics.
                Trace.TraceWarning($"CoreMIDI is unavailable: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows MIDI Services is the planned primary adapter; WinMM works on every Windows version.
            return [new Bluestone.Platform.Windows.WinMmProvider()];
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var alsa = new Bluestone.Platform.Alsa.AlsaProvider();
                alsa.CreateVirtualOutput(VirtualOutputName);
                return [alsa];
            }
            catch (EndpointUnavailableException ex)
            {
                Trace.TraceWarning($"ALSA is unavailable: {ex.Message}");
            }
        }

        return [];
    }

    /// <summary>
    /// Scheduling for the playback thread. On macOS the real-time time-constraint policy removed
    /// multi-millisecond wake-up delays in measurement (ADR 0012). On Windows a 1 ms timer resolution
    /// and the MMCSS "Pro Audio" task do the same job (ADR 0013).
    /// </summary>
    public static void ConfigurePlaybackThread()
    {
        if (OperatingSystem.IsMacOS() && !ThreadScheduling.MakeCurrentThreadRealtime(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5)))
        {
            ThreadScheduling.PromoteCurrentThread();
        }
        else if (OperatingSystem.IsWindows())
        {
            Bluestone.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
            Bluestone.Platform.Windows.ThreadScheduling.JoinProAudioTask();
        }
    }
}
