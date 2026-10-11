using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio.Plugins;

/// <summary>
/// Every live source playing one plugin instrument (ADR 0035): each track whose live route ends at the instrument, so
/// several tracks can play it at once. Sources are added and removed on control threads; the audio thread renders all
/// of them, in the order they were added, and releases what a removed source sounds at its next block. The audio
/// thread allocates nothing.
/// </summary>
public sealed class LiveSourceSet : ILiveEventSource
{
    private readonly Lock _gate = new();
    private ILiveEventSource[] _sources = [];

    // Audio thread: the sources it rendered last.
    private ILiveEventSource[] _active = [];

    /// <summary>Sources in the set. Any thread.</summary>
    public int Count => Volatile.Read(ref _sources).Length;

    /// <summary>Adds a source (once). Any thread; the audio thread renders it from its next block.</summary>
    public void Add(ILiveEventSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (Array.IndexOf(_sources, source) < 0)
            {
                Volatile.Write(ref _sources, [.. _sources, source]);
            }
        }
    }

    /// <summary>Removes a source. Any thread; at its next block the audio thread asks it to release what it sounds.</summary>
    public void Remove(ILiveEventSource source)
    {
        lock (_gate)
        {
            if (Array.IndexOf(_sources, source) >= 0)
            {
                Volatile.Write(ref _sources, [.. _sources.Where(s => !ReferenceEquals(s, source))]);
            }
        }
    }

    public int Render(in AudioRenderContext context, Span<PluginEvent> events)
    {
        var sources = Volatile.Read(ref _sources);
        var count = 0;
        if (!ReferenceEquals(sources, _active))
        {
            foreach (var source in _active)
            {
                if (Array.IndexOf(sources, source) < 0)
                {
                    count += source.Release(events[count..]);
                }
            }

            _active = sources;
        }

        foreach (var source in sources)
        {
            count += source.Render(context, events[count..]);
        }

        return count;
    }

    public int Release(Span<PluginEvent> events)
    {
        var count = 0;
        foreach (var source in _active)
        {
            count += source.Release(events[count..]);
        }

        _active = [];
        return count;
    }
}
