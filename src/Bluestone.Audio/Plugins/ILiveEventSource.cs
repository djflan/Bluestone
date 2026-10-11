using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Audio.Plugins;

/// <summary>
/// Live input for a plugin instrument (ADR 0035): the application's live route, run on the audio thread by the
/// <see cref="PluginNode"/> it is attached to, one block at a time. Both methods run on the audio thread: no
/// allocation, locking, I/O, or throwing.
/// </summary>
public interface ILiveEventSource
{
    /// <summary>Writes the live events for this block (sample offsets inside it) into <paramref name="events"/>; returns how many.</summary>
    int Render(in AudioRenderContext context, Span<PluginEvent> events);

    /// <summary>The source is being replaced or removed: writes a note-off for everything it sounds; returns how many.</summary>
    int Release(Span<PluginEvent> events);
}
