using System.Diagnostics;
using Bluestone.Audio.Devices;

namespace Bluestone.Audio;

/// <summary>
/// Drives an <see cref="AudioEngine"/> without a device, through the same render path a device callback uses: for
/// deterministic tests, offline rendering, and measurement. Time is virtual: each call advances the frame position,
/// and the block's "heard" timestamp moves by exactly the block's duration from <see cref="StartTimestamp"/>.
/// </summary>
public sealed class OfflineAudioDriver
{
    private readonly AudioEngine _engine;
    private long _frame;

    /// <param name="engine">The engine to drive.</param>
    /// <param name="channels">Device channels to render (2 is stereo).</param>
    /// <param name="serviceStreams">Let disk sources read ahead before each block (the engine must not run its own streamer thread).</param>
    public OfflineAudioDriver(AudioEngine engine, int channels = 2, bool serviceStreams = true)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        Channels = channels;
        ServiceStreamsBeforeEachBlock = serviceStreams;
    }

    public int Channels { get; }

    public bool ServiceStreamsBeforeEachBlock { get; set; }

    /// <summary>The virtual time of frame 0, in <see cref="Stopwatch"/> ticks.</summary>
    public long StartTimestamp { get; set; }

    /// <summary>Frames rendered so far.</summary>
    public long FramePosition => _frame;

    /// <summary>The virtual time at which the next block is heard.</summary>
    public long NextTimestamp => StartTimestamp + (_frame * Stopwatch.Frequency / _engine.SampleRate);

    /// <summary>Renders one device callback of <paramref name="frames"/> into <paramref name="interleaved"/>.</summary>
    public void Render(Span<float> interleaved, int frames)
    {
        if (interleaved.Length != frames * Channels)
        {
            throw new ArgumentException("The buffer must hold frames × channels samples.", nameof(interleaved));
        }

        if (ServiceStreamsBeforeEachBlock)
        {
            _engine.ServiceStreams();
        }

        interleaved.Clear();
        var block = new AudioDeviceBlock(_frame, NextTimestamp, TimeSpan.Zero, _engine.SampleRate, false);
        _engine.Render(interleaved, frames, Channels, block);
        _frame += frames;
    }

    /// <summary>Renders <paramref name="totalFrames"/> in callbacks of <paramref name="callbackFrames"/>; returns the interleaved result.</summary>
    public float[] Render(int totalFrames, int callbackFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(callbackFrames);
        var output = new float[totalFrames * Channels];
        for (var done = 0; done < totalFrames; done += callbackFrames)
        {
            var frames = Math.Min(callbackFrames, totalFrames - done);
            Render(output.AsSpan(done * Channels, frames * Channels), frames);
        }

        return output;
    }
}
