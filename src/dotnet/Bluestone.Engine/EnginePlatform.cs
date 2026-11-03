using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Platform.CoreMidi;

namespace Bluestone.Engine;

/// <summary>
/// The operating system's MIDI providers and thread policies in the engine: the same choices as Bluestone's own
/// (<c>PlatformProviders</c> in Bluestone.Desktop), without the virtual output, which Bluestone publishes itself.
/// </summary>
internal static class EnginePlatform
{
    /// <summary>The provider for this operating system, or none when its MIDI service is unavailable.</summary>
    public static IMidiEndpointProvider? CreateProvider(IMonotonicClock clock)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return new CoreMidiProvider(clock, "Bluestone Engine");
            }

            if (OperatingSystem.IsWindows())
            {
                return new Bluestone.Platform.Windows.WinMmProvider();
            }

            if (OperatingSystem.IsLinux())
            {
                return new Bluestone.Platform.Alsa.AlsaProvider("Bluestone Engine");
            }
        }
        catch (EndpointUnavailableException ex)
        {
            Console.Error.WriteLine($"MIDI is unavailable: {ex.Message}");
        }

        return null;
    }

    /// <summary>The macOS real-time policy, or MMCSS and a 1 ms timer on Windows, for the playback thread (ADRs 0012, 0013).</summary>
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

    /// <summary>The playback thread's sub-millisecond sleep on macOS (ADR 0043).</summary>
    public static Action<TimeSpan>? PlaybackPreciseSleep => OperatingSystem.IsMacOS() ? ThreadScheduling.SleepPrecisely : null;
}
