using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio.Plugins;

/// <summary>A plugin event at a song frame (the event's own sample offset is ignored; the node sets it per block).</summary>
public readonly record struct ScheduledEvent(long Frame, PluginEvent Event);

/// <summary>A parameter change at a song frame. Value is normalized, 0 to 1.</summary>
public readonly record struct ScheduledParameterChange(long Frame, uint Parameter, double Value);

/// <summary>
/// What one plugin device receives over the song: events (from the software-instrument feed) and parameter changes
/// (from device automation), each sorted by song frame at the engine's rate. Immutable; built on a control thread and
/// handed to a <see cref="PluginNode"/>, which reads it on the audio thread.
/// </summary>
public sealed class PluginSchedule
{
    public PluginSchedule(IEnumerable<ScheduledEvent> events, IEnumerable<ScheduledParameterChange> parameters)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(parameters);

        // Stable sorts keep the canonical order of events at the same frame.
        Events = [.. events.OrderBy(e => e.Frame)];
        Parameters = [.. parameters.OrderBy(p => p.Frame)];
    }

    public static PluginSchedule Empty { get; } = new([], []);

    public ScheduledEvent[] Events { get; }

    public ScheduledParameterChange[] Parameters { get; }

    /// <summary>Index of the first event at or after <paramref name="frame"/>.</summary>
    internal int FirstEventAt(long frame)
    {
        int lo = 0, hi = Events.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Events[mid].Frame < frame)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>Index of the first parameter change at or after <paramref name="frame"/>.</summary>
    internal int FirstParameterAt(long frame)
    {
        int lo = 0, hi = Parameters.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (Parameters[mid].Frame < frame)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
