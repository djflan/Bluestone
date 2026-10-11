using System.Diagnostics.Tracing;
using System.Globalization;
using System.Runtime;
using Bluestone.Audio;
using Bluestone.Audio.Devices;

namespace Bluestone.Benchmarks;

/// <summary>
/// ADR 0026's measurement for the audio callback: runs the C# render graph on the real default output device at
/// several buffer sizes while other threads put the garbage collector under pressure, and reports deadline misses,
/// device xruns, render-time percentiles (measured natively around the call into .NET, so a wait for a collection
/// is included), and the collections and pauses that happened. The output is silent (master at −120 dB); every
/// node and strip still renders.
/// </summary>
/// <remarks>
/// Run: <c>dotnet run -c Release --project src/Bluestone.Benchmarks -- --audio [seconds] [--pressure none|churn|full]
/// [--sustained] [--buffers 64,128,256] [--strips 32]</c>. GC configuration comes from the environment
/// (DOTNET_gcServer, DOTNET_gcConcurrent, DOTNET_GCgen0size, DOTNET_TieredPGO), printed at the start.
/// </remarks>
internal static class AudioCallbackProbe
{
    private const int SampleRate = 48_000;

    public static void Run(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var s) ? s : 20;
        var pressure = Option(args, "--pressure") ?? "full";
        var buffers = (Option(args, "--buffers") ?? "64,128,256").Split(',').Select(b => int.Parse(b, CultureInfo.InvariantCulture)).ToArray();
        var strips = int.Parse(Option(args, "--strips") ?? "32", CultureInfo.InvariantCulture);
        if (args.Contains("--sustained"))
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }

        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        Console.WriteLine($"Device: {AudioDevices.List().FirstOrDefault(d => d.IsDefault)?.Name ?? "default"}");
        Console.WriteLine($"GC: server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} concurrent={Environment.GetEnvironmentVariable("DOTNET_gcConcurrent") ?? "default"} " +
                          $"gen0size={Environment.GetEnvironmentVariable("DOTNET_GCgen0size") ?? "default"} tieredPGO={Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "default"}");
        Console.WriteLine($"Pressure: {pressure}; {strips} tone nodes into {strips} strips into 4 buses; {seconds} s per buffer size at {SampleRate} Hz");
        Console.WriteLine();
        Console.WriteLine("buffer | period | callbacks | late (native) | late (managed) | xruns | gaps | p50 | p99 | p99.9 | max | gen0 | gen1 | gen2 | GC pause total | GC pause max | pauses > period");
        foreach (var buffer in buffers)
        {
            Console.WriteLine(Measure(buffer, seconds, pressure, strips));
        }
    }

    private static string Measure(int buffer, int seconds, string pressure, int strips)
    {
        using var engine = new AudioEngine(SampleRate, buffer);
        engine.Publish(Graph(buffer, strips));
        using var output = AudioOutput.Open(engine, new AudioStreamRequest(SampleRate: SampleRate, BufferFrames: buffer));
        output.Start();
        Thread.Sleep(500); // warm-up: tiering, first callbacks
        engine.Statistics.Reset();
        var before = output.Status;
        var baseline = output.RenderHistogram();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var paused = GC.GetTotalPauseDuration();

        using var stop = new CancellationTokenSource();
        using var pauses = new GcPauseListener();
        var threads = new List<Thread>();
        if (pressure is "churn" or "full")
        {
            threads.Add(Start(() => Churn(stop.Token)));
            threads.Add(Start(() => Churn(stop.Token)));
        }

        if (pressure == "full")
        {
            threads.Add(Start(() => FullCollections(stop.Token)));
        }

        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        output.Stop();
        var after = output.Status;
        var histogram = output.RenderHistogram().Zip(baseline, (a, b) => a - b).ToArray();
        var period = TimeSpan.FromSeconds((double)after.LastBufferFrames / SampleRate);
        return string.Join(" | ", [
            $"{buffer} ({after.LastBufferFrames})",
            Ms(period),
            (after.Callbacks - before.Callbacks).ToString(CultureInfo.InvariantCulture),
            (after.LateRenders - before.LateRenders).ToString(CultureInfo.InvariantCulture),
            engine.Statistics.DeadlineMisses.ToString(CultureInfo.InvariantCulture),
            (after.Xruns - before.Xruns).ToString(CultureInfo.InvariantCulture),
            (after.Discontinuities - before.Discontinuities).ToString(CultureInfo.InvariantCulture),
            Ms(AudioOutput.Percentile(histogram, 50)),
            Ms(AudioOutput.Percentile(histogram, 99)),
            Ms(AudioOutput.Percentile(histogram, 99.9)),
            Ms(HistogramMax(histogram)),
            (GC.CollectionCount(0) - gen0).ToString(CultureInfo.InvariantCulture),
            (GC.CollectionCount(1) - gen1).ToString(CultureInfo.InvariantCulture),
            (GC.CollectionCount(2) - gen2).ToString(CultureInfo.InvariantCulture),
            Ms(GC.GetTotalPauseDuration() - paused),
            Ms(pauses.Max),
            pauses.LongerThan(period).ToString(CultureInfo.InvariantCulture),
        ]);
    }

    private static AudioGraph Graph(int blockFrames, int strips)
    {
        var builder = new AudioGraphBuilder();
        var buses = Enumerable.Range(0, 4).Select(_ => builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -3))).ToArray();
        for (var i = 0; i < strips; i++)
        {
            var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -12, Pan: ((i % 9) - 4) / 4.0));
            builder.RouteStrip(strip, buses[i % buses.Length]);
            builder.ConnectToStrip(builder.AddNode(new ToneNode(110 * (i + 1), 0.1)), strip);
        }

        return builder.Build(blockFrames, new MixerStripState(), masterGainDecibels: -120);
    }

    // The top of the highest occupied 10 µs bucket (the last bucket means "longer than 20 ms").
    private static TimeSpan HistogramMax(long[] histogram)
    {
        for (var i = histogram.Length - 1; i >= 0; i--)
        {
            if (histogram[i] > 0)
            {
                return TimeSpan.FromMicroseconds((i + 1) * 10);
            }
        }

        return TimeSpan.Zero;
    }

    private static string Ms(TimeSpan time) => time.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture) + " ms";

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static Thread Start(Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true };
        thread.Start();
        return thread;
    }

    // Short-lived garbage with a rolling window of survivors, so gen0 and gen1 collections happen continuously.
    private static void Churn(CancellationToken stop)
    {
        var random = new Random(1);
        var window = new object[16_384];
        var i = 0;
        while (!stop.IsCancellationRequested)
        {
            window[i++ % window.Length] = (i % 3) switch
            {
                0 => new byte[random.Next(16, 4096)],
                1 => string.Create(CultureInfo.InvariantCulture, $"item {i}"),
                _ => new List<int>(random.Next(1, 64)),
            };
        }
    }

    // A large retained object graph that keeps changing, collected with forced, blocking, compacting gen2 collections.
    private static void FullCollections(CancellationToken stop)
    {
        var nodes = new Node[1_000_000];
        for (var i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new Node(i, i > 0 ? nodes[i - 1] : null);
        }

        var random = new Random(2);
        while (!stop.IsCancellationRequested)
        {
            for (var i = 0; i < 100_000; i++)
            {
                var at = random.Next(nodes.Length);
                nodes[at] = new Node(at, nodes[random.Next(nodes.Length)]);
            }

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }

        GC.KeepAlive(nodes);
    }

    private sealed record Node(int Value, Node? Next);

    /// <summary>Every garbage collection's suspension, from the runtime's GCSuspendEEBegin to GCRestartEEEnd events.</summary>
    private sealed class GcPauseListener : EventListener
    {
        private const int SuspendBegin = 9;
        private const int RestartEnd = 3;
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _pauses = [];
        private DateTime? _suspended;

        public TimeSpan Max
        {
            get
            {
                lock (_gate)
                {
                    return _pauses.Count == 0 ? TimeSpan.Zero : _pauses.Max();
                }
            }
        }

        public int LongerThan(TimeSpan limit)
        {
            lock (_gate)
            {
                return _pauses.Count(p => p > limit);
            }
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            lock (_gate)
            {
                if (data.EventId == SuspendBegin)
                {
                    _suspended = data.TimeStamp;
                }
                else if (data.EventId == RestartEnd && _suspended is { } began)
                {
                    _pauses.Add(data.TimeStamp - began);
                    _suspended = null;
                }
            }
        }
    }
}
