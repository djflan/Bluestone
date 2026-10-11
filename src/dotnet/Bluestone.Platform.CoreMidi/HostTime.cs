using System.Runtime.Versioning;
using Bluestone.Midi.Timing;

namespace Bluestone.Platform.CoreMidi;

/// <summary>
/// Converts between Bluestone's monotonic clock and CoreMIDI host time (mach_absolute_time units).
/// Conversions are made relative to "now" on both clocks, so they do not depend on the clocks
/// sharing an origin.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class HostTime
{
    private readonly IMonotonicClock _clock;
    private readonly ulong _numer;
    private readonly ulong _denom;

    public HostTime(IMonotonicClock clock)
    {
        _clock = clock;
        Native.mach_timebase_info(out var timebase);
        _numer = timebase.Numer == 0 ? 1 : timebase.Numer;
        _denom = timebase.Denom == 0 ? 1 : timebase.Denom;
    }

    /// <summary>Host time for <paramref name="target"/>, or 0 ("now") when it is not in the future.</summary>
    public ulong ToHost(TimeSpan target)
    {
        var ahead = target - _clock.Now;
        if (ahead <= TimeSpan.Zero)
        {
            return 0;
        }

        var nanoseconds = (ulong)ahead.Ticks * 100;
        return Native.mach_absolute_time() + (nanoseconds * _denom / _numer);
    }

    /// <summary>Bluestone clock time for a host timestamp; 0 means "now".</summary>
    public TimeSpan FromHost(ulong host)
    {
        var now = _clock.Now;
        if (host == 0)
        {
            return now;
        }

        var hostNow = Native.mach_absolute_time();
        var nanosecondsAgo = hostNow >= host
            ? (long)((hostNow - host) * _numer / _denom)
            : -(long)((host - hostNow) * _numer / _denom);
        return now - TimeSpan.FromTicks(nanosecondsAgo / 100);
    }
}
