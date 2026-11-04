using System.Diagnostics;
using System.Globalization;
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
/// Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --audio [seconds] [--pressure none|app|churn|full]
/// [--sustained] [--buffers 64,128,256] [--strips 32] [--queue 0] [--isolate] [--device NAME]</c>. GC configuration comes from the
/// environment (DOTNET_gcServer, DOTNET_gcConcurrent, DOTNET_GCgen0size, ...) and is recorded in the report.
/// </remarks>
internal static class AudioCallbackProbe
{
    private const int SampleRate = 48_000;

    public static void Run(ProbeOptions options)
    {
        var args = options.Args;
        var buffers = (Option(args, "--buffers") ?? "64,128,256").Split(',').Select(b => int.Parse(b, CultureInfo.InvariantCulture)).ToArray();
        var strips = options.Int("--strips", 32);
        var queue = options.Int("--queue", 0);
        var host = Option(args, "--host");
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        var (deviceId, device) = options.OutputDevice();
        foreach (var buffer in buffers)
        {
            var settings = options.BaseSettings();
            settings["device"] = device;
            settings["buffer"] = buffer.ToString(CultureInfo.InvariantCulture);
            settings["rate"] = SampleRate.ToString(CultureInfo.InvariantCulture);
            settings["strips"] = strips.ToString(CultureInfo.InvariantCulture);
            settings["mode"] = queue == 0 ? "direct" : $"buffered {queue}";
            var report = new TimingReport("audio-callback", settings);
            report.Machine.WarnIfBusy();
            Console.WriteLine($"Audio callback: {device}, {buffer} frames at {SampleRate} Hz, {strips} strips, {settings["mode"]}, pressure {settings["pressure"]}, {options.Seconds} s");
            Measure(report, buffer, options, strips, queue, deviceId, host);
            options.Finish(report);
        }
    }

    private static void Measure(TimingReport report, int buffer, ProbeOptions options, int strips, int queue, string? deviceId, string? host)
    {
        using var engine = new AudioEngine(SampleRate, buffer);
        engine.Publish(Graph(buffer, strips));
        using var output = AudioOutput.Open(engine, new AudioStreamRequest(DeviceId: deviceId, SampleRate: SampleRate, BufferFrames: buffer, QueuedFrames: queue, Host: host));
        output.Start();
        Thread.Sleep(500); // warm-up: tiering, first callbacks
        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 0); // let the pressure reach its steady state
        engine.Statistics.Reset();
        var before = output.Status;
        var baseline = output.RenderHistogram();
        using var gc = new GcWatch();

        Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
        var after = output.Status;
        var histogram = output.RenderHistogram().Zip(baseline, (a, b) => a - b).ToArray();
        gc.AddTo(report);
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        output.Stop();
        var period = (double)after.LastBufferFrames / SampleRate * 1000;
        var expected = options.Seconds * SampleRate / Math.Max(1, after.LastBufferFrames);
        report.Add(new Metric("audio.period", "ms", period));
        report.Add(new Metric("audio.device-buffer", "frames", after.LastBufferFrames));
        report.Add(new Metric("audio.output-latency", "ms", after.OutputLatency.TotalMilliseconds));
        report.Add(new Metric("audio.callback-render", "ms", Distribution: Distribution.OfHistogram(histogram, 0.01, 20), Target: period));
        report.Add(new Metric("audio.callbacks-missing", "callbacks", Math.Max(0, expected - (after.Callbacks - before.Callbacks)), Target: 0));
        report.Add(new Metric("audio.late-callbacks", "callbacks", after.LateRenders - before.LateRenders, Target: 0));
        report.Add(new Metric("audio.late-managed", "blocks", engine.Statistics.DeadlineMisses));
        report.Add(new Metric("audio.xruns", "xruns", after.Xruns - before.Xruns, Target: 0));
        report.Add(new Metric("audio.gaps", "callbacks", after.Discontinuities - before.Discontinuities, Target: 0));
        report.Add(new Metric("audio.queue-underrun", "frames", after.QueueUnderrunFrames - before.QueueUnderrunFrames, Target: 0));
    }

    internal static AudioGraph Graph(int blockFrames, int strips, AudioNode? extra = null)
    {
        var builder = new AudioGraphBuilder();
        if (extra is not null)
        {
            builder.ConnectToMaster(builder.AddNode(extra));
        }

        var buses = Enumerable.Range(0, 4).Select(_ => builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -3))).ToArray();
        for (var i = 0; i < strips; i++)
        {
            var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -12, Pan: ((i % 9) - 4) / 4.0));
            builder.RouteStrip(strip, buses[i % buses.Length]);
            builder.ConnectToStrip(builder.AddNode(new ToneNode(110 * (i + 1), 0.1)), strip);
        }

        return builder.Build(blockFrames, new MixerStripState(), masterGainDecibels: -120);
    }

    internal static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static Thread Start(Action body)
    {
        var thread = new Thread(() => body()) { IsBackground = true };
        thread.Start();
        return thread;
    }

    // Short-lived garbage with a rolling window of survivors, so gen0 and gen1 collections happen continuously.
    internal static void Churn(CancellationToken stop)
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

    // Roughly what an editing session does: a 200 MB retained heap (the project, undo history, caches), about
    // 50 MB/s of short-lived allocations in small objects, a quarter of them kept for a while, and no forced collections.
    internal static void Application(CancellationToken stop)
    {
        var retained = new List<byte[]>();
        for (var i = 0; i < 200_000; i++)
        {
            retained.Add(new byte[1000]);
        }

        var recent = new object[200_000];
        var random = new Random(3);
        var i2 = 0;
        var clock = Stopwatch.StartNew();
        long allocated = 0;
        while (!stop.IsCancellationRequested)
        {
            for (var k = 0; k < 500; k++)
            {
                var size = random.Next(16, 400);
                var item = new byte[size];
                allocated += size + 24;
                if (k % 4 == 0)
                {
                    recent[i2++ % recent.Length] = item;
                }
            }

            // Pace to about 50 MB/s.
            var due = TimeSpan.FromSeconds(allocated / 50e6);
            if (clock.Elapsed < due)
            {
                Thread.Sleep(due - clock.Elapsed);
            }
        }

        GC.KeepAlive(retained);
    }

    // A large retained object graph that keeps changing, collected with forced, blocking, compacting gen2 collections.
    internal static void FullCollections(CancellationToken stop)
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
}
