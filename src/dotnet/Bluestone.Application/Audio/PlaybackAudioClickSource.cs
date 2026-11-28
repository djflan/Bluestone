using Bluestone.Audio;
using Bluestone.Playback;

namespace Bluestone.Application.Audio;

internal sealed class PlaybackAudioClickSource(IPlaybackTransport playback) : IAudioClickSource
{
    private readonly IAudioMetronome? _metronome = playback as IAudioMetronome;

    public bool TryGetClick(TimeSpan from, TimeSpan until, long clickGeneration, out AudioClickEvent click, out bool ready)
    {
        if (_metronome is null)
        {
            click = default;
            ready = true;
            return false;
        }

        var found = _metronome.TryGetAudioClick(from, until, clickGeneration, out var scheduled, out ready);
        click = found ? new AudioClickEvent(scheduled.Timestamp, scheduled.Downbeat) : default;
        return found;
    }
}
