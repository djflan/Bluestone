using System.Diagnostics;

namespace Bluestone.Audio;

/// <summary>An audio click at an absolute monotonic-clock time.</summary>
public readonly record struct AudioClickEvent(TimeSpan Timestamp, bool Downbeat);

/// <summary>Lock-free source of metronome clicks for the audio callback.</summary>
public interface IAudioClickSource
{
    bool TryGetClick(TimeSpan from, TimeSpan until, out AudioClickEvent click);
}

/// <summary>
/// Renders metronome clicks on the audio thread. It keeps state across blocks: a click already past when playback
/// starts (render-ahead and output latency) sounds whole at the block start instead of being lost, and a click
/// suppressed because it overlaps the sounding one stays suppressed whatever the block size. Single reader.
/// </summary>
internal sealed class AudioClickRenderer(IAudioClickSource source)
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan LateWindow = TimeSpan.FromMilliseconds(200);

    private bool _running;
    private bool _active;
    private TimeSpan _activeStart;
    private bool _activeDownbeat;
    private TimeSpan _searchFrom;
    private TimeSpan _lastBlockStart;

    public void Render(in AudioRenderContext context, StereoBuffer output)
    {
        if (!context.Playing)
        {
            _running = false;
            _active = false;
            return;
        }

        var blockStart = Stopwatch.GetElapsedTime(0, context.HeardTimestamp);
        var blockEnd = blockStart + TimeSpan.FromSeconds((double)context.Frames / context.SampleRate);
        // A new stream (started, or heard time went back after a device change) looks back for a click just missed.
        if (!_running || blockStart < _lastBlockStart)
        {
            _running = true;
            _active = false;
            _searchFrom = blockStart - LateWindow;
        }
        else if (_searchFrom < blockStart - LateWindow)
        {
            _searchFrom = blockStart - LateWindow;
        }

        _lastBlockStart = blockStart;

        if (_active && _activeStart + Duration > blockStart)
        {
            Add(_activeStart, _activeDownbeat, blockStart, context.SampleRate, output);
        }

        // Each found click moves the search at least one click length on, so this is bounded by the block window.
        var maxClicks = (int)Math.Ceiling((blockEnd - _searchFrom).TotalSeconds / Duration.TotalSeconds) + 1;
        for (var count = 0; count < maxClicks && _searchFrom < blockEnd && source.TryGetClick(_searchFrom, blockEnd, out var click); count++)
        {
            var start = click.Timestamp > blockStart ? click.Timestamp : blockStart;
            if (!_active || start >= _activeStart + Duration)
            {
                _active = true;
                _activeStart = start;
                _activeDownbeat = click.Downbeat;
                Add(start, click.Downbeat, blockStart, context.SampleRate, output);
            }

            var next = _activeStart + Duration;
            _searchFrom = next > click.Timestamp ? next : click.Timestamp + TimeSpan.FromTicks(1);
        }

        if (_searchFrom < blockEnd)
        {
            _searchFrom = blockEnd;
        }
    }

    private static void Add(TimeSpan timestamp, bool downbeat, TimeSpan blockStart, int sampleRate, StereoBuffer output)
    {
        var start = (timestamp - blockStart).TotalSeconds * sampleRate;
        var first = Math.Max(0, (int)Math.Floor(start));
        var last = Math.Min(output.Frames, (int)Math.Ceiling(start + (Duration.TotalSeconds * sampleRate)));
        var frequency = downbeat ? 1760.0 : 1320.0;
        var level = downbeat ? 0.32 : 0.22;
        var durationFrames = Duration.TotalSeconds * sampleRate;
        for (var frame = first; frame < last; frame++)
        {
            var age = frame - start;
            if (age < 0)
            {
                continue;
            }

            var envelope = 1.0 - (age / durationFrames);
            var sample = (float)(level * envelope * envelope * Math.Sin(2 * Math.PI * frequency * age / sampleRate));
            output.Left[frame] += sample;
            output.Right[frame] += sample;
        }
    }
}
