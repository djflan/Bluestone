using System.Diagnostics;
using Bluestone.Audio.Devices;

namespace Bluestone.Audio;

/// <summary>
/// Renders the current <see cref="AudioGraph"/> for an audio device (as its <see cref="IAudioRenderer"/>) or for the
/// <see cref="OfflineAudioDriver"/>. Control threads publish graphs and transport changes; the audio thread picks them
/// up between blocks without waiting. A device callback longer than <see cref="BlockFrames"/> is rendered in blocks
/// of at most that size, and every block is cut at the loop end, so nodes always see a contiguous song range.
/// </summary>
public sealed class AudioEngine : IAudioRenderer, IDisposable
{
    private readonly float[] _master;
    private readonly float[] _tone;
    private readonly DiskStreamer? _streamer;
    private readonly HashSet<IStreamedSource> _serviced = [];
    private AudioGraph _graph;
    private ToneNode? _testTone;

    /// <param name="sampleRate">The device's rate in Hz; graphs and sources are built for it.</param>
    /// <param name="blockFrames">The largest block nodes see (plugin instances must accept this many frames).</param>
    /// <param name="streamFromDisk">Start the disk-streamer thread. Off for deterministic tests, which call <see cref="ServiceStreams"/>.</param>
    public AudioEngine(int sampleRate, int blockFrames = 256, bool streamFromDisk = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockFrames);
        SampleRate = sampleRate;
        BlockFrames = blockFrames;
        _master = new float[2 * blockFrames];
        _tone = new float[2 * blockFrames];
        _graph = AudioGraph.Silent(blockFrames);
        _streamer = streamFromDisk ? new DiskStreamer(this) : null;
    }

    public int SampleRate { get; }

    public int BlockFrames { get; }

    public AudioTransport Transport { get; } = new();

    public AudioEngineStatistics Statistics { get; } = new();

    /// <summary>The graph being rendered.</summary>
    public AudioGraph Graph => Volatile.Read(ref _graph);

    /// <summary>A test tone added after the master (null for none). Any thread.</summary>
    public ToneNode? TestTone
    {
        get => Volatile.Read(ref _testTone);
        set => Volatile.Write(ref _testTone, value);
    }

    /// <summary>Swaps in a new graph; the audio thread uses it from its next block. Any thread.</summary>
    public void Publish(AudioGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.BlockFrames != BlockFrames)
        {
            throw new ArgumentException($"The graph was built for {graph.BlockFrames}-frame blocks; this engine renders {BlockFrames}.", nameof(graph));
        }

        Volatile.Write(ref _graph, graph);
        _streamer?.Wake();
    }

    /// <summary>Reads ahead for every disk source of the current graph, on the calling thread. For tests without the streamer thread.</summary>
    public void ServiceStreams()
    {
        if (_streamer is not null)
        {
            throw new InvalidOperationException("The disk streamer thread services this engine.");
        }

        DiskStreamer.ServiceOnce(Graph, _serviced);
    }

    public void Render(Span<float> interleaved, int frames, int channels, in AudioDeviceBlock block)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            RenderBlocks(interleaved, frames, channels, block);
        }
#pragma warning disable CA1031 // The audio thread must never unwind; the callback becomes silence and is counted.
        catch (Exception)
#pragma warning restore CA1031
        {
            interleaved.Clear();
            Statistics.Fault();
        }

        Statistics.Record(Stopwatch.GetTimestamp() - started, frames, SampleRate, block.Discontinuity);
    }

    /// <summary>Stops the disk streamer and closes the files of the disk sources it served.</summary>
    public void Dispose()
    {
        _streamer?.Dispose();
        foreach (var source in _serviced)
        {
            source.Close();
        }

        _serviced.Clear();
    }

    private void RenderBlocks(Span<float> interleaved, int frames, int channels, in AudioDeviceBlock block)
    {
        Transport.BeginCallback(block.HeardTimestamp, SampleRate);
        var graph = Volatile.Read(ref _graph);
        var tone = Volatile.Read(ref _testTone);
        var done = 0;
        while (done < frames)
        {
            var context = Transport.Next(Math.Min(frames - done, BlockFrames), SampleRate);
            var master = StereoBuffer.Of(_master, BlockFrames, context.Frames);
            graph.Render(context, master);
            if (tone is not null)
            {
                var toneBlock = StereoBuffer.Of(_tone, BlockFrames, context.Frames);
                toneBlock.Clear();
                tone.Render(context, toneBlock, toneBlock);
                master.Add(toneBlock);
            }

            Interleave(master, interleaved.Slice(done * channels, context.Frames * channels), channels);
            Transport.Advance(context.Frames);
            done += context.Frames;
        }
    }

    private static void Interleave(StereoBuffer source, Span<float> destination, int channels)
    {
        var frames = source.Frames;
        if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                destination[i] = 0.5f * (source.Left[i] + source.Right[i]);
            }

            return;
        }

        destination.Clear();
        for (var i = 0; i < frames; i++)
        {
            destination[i * channels] = source.Left[i];
            destination[(i * channels) + 1] = source.Right[i];
        }
    }
}
