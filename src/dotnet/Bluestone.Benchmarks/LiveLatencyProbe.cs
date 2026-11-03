using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Bluestone.Audio;
using Bluestone.Audio.Devices;

namespace Bluestone.Benchmarks;

/// <summary>
/// Live-input latency on the real default output device (ADR 0035, judged by ADR 0043): notes are "played"
/// (timestamped with the system clock, as MIDI input is) at random moments and taken by a node at the start of the next
/// block the engine renders, the way a live route feeds a plugin instrument; the latency is from the timestamp until
/// that block is heard (the engine's heard time: render-ahead queue plus the device's reported output latency). A
/// plugin adds its pipeline on top; the offline tests measure that part exactly.
/// </summary>
/// <remarks>
/// Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --live [seconds] [--pressure none|app|churn]
/// [--buffer 128] [--queue 512] [--isolate] [--device NAME]</c>; <c>--queue 0</c> renders in the device callback (direct mode).
/// </remarks>
internal static class LiveLatencyProbe
{
    private const int SampleRate = 48_000;

    public static void Run(ProbeOptions options)
    {
        var buffer = options.Int("--buffer", 128);
        var queue = options.Int("--queue", 512);
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        var (deviceId, device) = options.OutputDevice();
        var settings = options.BaseSettings();
        settings["device"] = device;
        settings["buffer"] = buffer.ToString(CultureInfo.InvariantCulture);
        settings["rate"] = SampleRate.ToString(CultureInfo.InvariantCulture);
        settings["queue"] = queue == 0 ? "0 (direct)" : queue.ToString(CultureInfo.InvariantCulture);
        var report = new TimingReport("live-latency", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Live latency: {device}; {buffer}-frame buffer and blocks at {SampleRate} Hz; queue {settings["queue"]}; pressure {settings["pressure"]}; {options.Seconds} s");

        var probe = new ProbeNode((options.Seconds + 2) * 250);
        using var engine = new AudioEngine(SampleRate, buffer);
        engine.Publish(AudioCallbackProbe.Graph(buffer, 32, probe));
        using var output = AudioOutput.Open(engine, new AudioStreamRequest(DeviceId: deviceId, SampleRate: SampleRate, BufferFrames: buffer, QueuedFrames: queue));
        output.Start();
        Thread.Sleep(500);
        report.Notes.Add($"render thread: {(queue == 0 ? "the device callback" : output.RenderThreadPolicy)}");

        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 200);
        using var gc = new GcWatch();
        var before = output.Status;
        var random = new Random(35);
        var until = Stopwatch.GetTimestamp() + (options.Seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            probe.Played.Enqueue(Stopwatch.GetTimestamp());
            Thread.Sleep(5 + random.Next(30));
        }

        Thread.Sleep(200);
        var after = output.Status;
        gc.AddTo(report);
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        output.Stop();
        var period = (double)after.LastBufferFrames / SampleRate * 1000;
        var deviceLatency = after.OutputLatency.TotalMilliseconds;
        var total = probe.Latencies().Select(t => t * 1000.0 / Stopwatch.Frequency).ToArray();
        report.Add(new Metric("audio.period", "ms", period));
        report.Add(new Metric("audio.output-latency", "ms", deviceLatency));
        report.Add(new Metric("live.total", "ms", Distribution: Distribution.Of(total)));
        report.Add(new Metric("live.beyond-device", "ms", Distribution: Distribution.Of(total.Select(t => t - deviceLatency))));
        report.Add(new Metric("live.added", "ms", Distribution: Distribution.Of(total.Select(t => t - deviceLatency - period)), Target: Targets.LiveAddedMs));
        report.Add(new Metric("audio.xruns", "xruns", after.Xruns - before.Xruns, Target: 0));
        report.Add(new Metric("audio.late-callbacks", "callbacks", after.LateRenders - before.LateRenders, Target: 0));
        report.Add(new Metric("audio.queue-underrun", "frames", after.QueueUnderrunFrames - before.QueueUnderrunFrames, Target: 0));
        options.Finish(report);
    }

    /// <summary>Takes what was played at the start of each block, as a live route does, and notes when that block is heard.</summary>
    private sealed class ProbeNode(int capacity) : AudioNode
    {
        private readonly long[] _latencies = new long[capacity];
        private int _count;

        public ConcurrentQueue<long> Played { get; } = new();

        public IEnumerable<long> Latencies() => _latencies.Take(Volatile.Read(ref _count));

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            while (Played.TryDequeue(out var played))
            {
                if (_count < _latencies.Length)
                {
                    _latencies[_count] = context.HeardTimestamp - played;
                    Volatile.Write(ref _count, _count + 1);
                }
            }
        }
    }
}
