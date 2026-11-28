using System.Diagnostics;

namespace Bluestone.Audio;

/// <summary>An audio click at an absolute monotonic-clock time.</summary>
public readonly record struct AudioClickEvent(TimeSpan Timestamp, bool Downbeat);

/// <summary>Lock-free source of metronome clicks for the audio callback.</summary>
public interface IAudioClickSource
{
    bool TryGetClick(TimeSpan from, TimeSpan until, out AudioClickEvent click);
}

internal static class AudioClick
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(40);

    public static void Render(IAudioClickSource source, in AudioRenderContext context, StereoBuffer output)
    {
        if (!context.Playing)
        {
            return;
        }

        var blockStart = Stopwatch.GetElapsedTime(0, context.HeardTimestamp);
        var blockEnd = blockStart + TimeSpan.FromSeconds((double)context.Frames / context.SampleRate);
        var search = blockStart - Duration;
        var maxClicksPerBlock = (int)Math.Ceiling((blockEnd - search).TotalSeconds / Duration.TotalSeconds);
        for (var count = 0; count < maxClicksPerBlock && source.TryGetClick(search, blockEnd, out var click); count++)
        {
            Add(click, blockStart, context.SampleRate, output);
            search = click.Timestamp + Duration;
        }
    }

    private static void Add(in AudioClickEvent click, TimeSpan blockStart, int sampleRate, StereoBuffer output)
    {
        var start = (click.Timestamp - blockStart).TotalSeconds * sampleRate;
        var first = Math.Max(0, (int)Math.Floor(start));
        var last = Math.Min(output.Frames, (int)Math.Ceiling(start + (Duration.TotalSeconds * sampleRate)));
        var frequency = click.Downbeat ? 1760.0 : 1320.0;
        var level = click.Downbeat ? 0.32 : 0.22;
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
