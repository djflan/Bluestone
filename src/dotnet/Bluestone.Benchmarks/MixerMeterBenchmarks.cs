using BenchmarkDotNet.Attributes;
using Bluestone.Audio;

namespace Bluestone.Benchmarks;

/// <summary>Steady-state 128-frame mixer rendering with the selected meter DSP active.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MixerMeterBenchmarks
{
    private const int BlockFrames = 128;
    private AudioGraph _graph = null!;
    private float[] _output = null!;
    private long _songFrame;

    [Params(44_100, 48_000, 88_200, 96_000)]
    public int SampleRate { get; set; }

    [Params(1, 8, 32)]
    public int Strips { get; set; }

    [Params(MixerMeterMode.PeakRms, MixerMeterMode.Vu, MixerMeterMode.LufsS)]
    public MixerMeterMode Mode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _graph = MixerMeterBenchmarkGraph.Build(SampleRate, Strips, Mode, out _);
        _output = new float[2 * BlockFrames];

        // LUFS-S must be past its three-second warm-up before measurement begins.
        var warmupBlocks = ((3 * SampleRate) + BlockFrames - 1) / BlockFrames;
        for (var i = 0; i < warmupBlocks; i++)
        {
            RenderBlock(jumped: i == 0);
        }
    }

    /// <summary>One warmed graph render, including every strip meter and the Master meter.</summary>
    [Benchmark]
    public float Render128Frames()
    {
        RenderBlock(jumped: false);
        return _output[0];
    }

    private void RenderBlock(bool jumped)
    {
        var context = new AudioRenderContext(BlockFrames, SampleRate, Playing: true, SongFrame: _songFrame, Jumped: jumped);
        _songFrame += BlockFrames;
        _graph.Render(context, StereoBuffer.Of(_output, BlockFrames, BlockFrames));
    }
}

/// <summary>Control-thread graph construction, with meter preparation measured separately from steady rendering.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class MixerMeterBenchmarksGraphBuild
{
    private const int BlockFrames = 128;
    private MixerStripState[] _reusedStrips = null!;
    private MixerStripState _reusedMaster = null!;

    [Params(44_100, 48_000, 88_200, 96_000)]
    public int SampleRate { get; set; }

    [Params(1, 8, 32)]
    public int Strips { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _reusedMaster = new MixerStripState();
        _reusedStrips = new MixerStripState[Strips];
        for (var i = 0; i < _reusedStrips.Length; i++)
        {
            _reusedStrips[i] = new MixerStripState();
        }

        _reusedMaster.Meter.Prepare(SampleRate);
        foreach (var strip in _reusedStrips)
        {
            strip.Meter.Prepare(SampleRate);
        }
    }

    /// <summary>Build a graph with new meter state and prepare its filters for this sample rate.</summary>
    [Benchmark]
    public AudioGraph BuildAndPrepareNewMeters()
    {
        var master = new MixerStripState();
        var strips = new MixerStripState[Strips];
        for (var i = 0; i < strips.Length; i++)
        {
            strips[i] = new MixerStripState();
        }

        return MixerMeterBenchmarkGraph.Build(SampleRate, strips, master);
    }

    /// <summary>Build a graph snapshot reusing the already-prepared strip and Master meter state.</summary>
    [Benchmark]
    public AudioGraph BuildSnapshotWithReusedMeters() =>
        MixerMeterBenchmarkGraph.Build(SampleRate, _reusedStrips, _reusedMaster);
}

internal static class MixerMeterBenchmarkGraph
{
    private const int BlockFrames = 128;

    public static AudioGraph Build(int sampleRate, int stripCount, MixerMeterMode mode, out MixerStripState master)
    {
        master = new MixerStripState();
        master.Meter.Settings = new MixerMeterSettings(mode, -18);
        var strips = new MixerStripState[stripCount];
        for (var i = 0; i < strips.Length; i++)
        {
            strips[i] = new MixerStripState();
            strips[i].Meter.Settings = new MixerMeterSettings(mode, -18);
        }

        return Build(sampleRate, strips, master);
    }

    public static AudioGraph Build(int sampleRate, MixerStripState[] strips, MixerStripState master)
    {
        var builder = new AudioGraphBuilder();
        var input = builder.AddNode(new MeterBenchmarkSignal());
        foreach (var state in strips)
        {
            var strip = builder.AddStrip(state, new MixerStripSettings());
            builder.ConnectToStrip(input, strip);
        }

        var graph = builder.Build(BlockFrames, master);
        graph.PrepareMeters(sampleRate);
        return graph;
    }

    private sealed class MeterBenchmarkSignal : AudioNode
    {
        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            output.Left.Fill(0.005f);
            output.Right.Fill(0.005f);
        }
    }
}
