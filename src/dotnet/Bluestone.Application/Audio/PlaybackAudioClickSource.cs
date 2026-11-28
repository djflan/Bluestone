using Bluestone.Audio;
using Bluestone.Playback;

namespace Bluestone.Application.Audio;

internal sealed class PlaybackAudioClickSource(IPlaybackTransport playback) : IAudioClickSource
{
    private readonly IAudioMetronome? _metronome = playback as IAudioMetronome;

    public bool TryGetClick(TimeSpan from, TimeSpan until, out AudioClickEvent click)
    {
        if (_metronome is not null && _metronome.TryGetAudioClick(from, until, out var scheduled))
        {
            click = new AudioClickEvent(scheduled.Timestamp, scheduled.Downbeat);
            return true;
        }

        click = default;
        return false;
    }
}
