using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Bluestone.Audio;
using Bluestone.Audio.Devices;

namespace Bluestone.Benchmarks;

/// <summary>
/// ADR 0035's measurement of live-input latency on the real default output device: notes are "played" (timestamped
/// with the system clock, as MIDI input is) at random moments and taken by a node at the start of the next block the
/// engine renders, the way a live route feeds a plugin instrument; the latency is from the timestamp until that
/// block is heard (the engine's heard time: render-ahead queue plus the device's reported output latency). The same
/// run first uses the playback queue, then switches the running stream to the live queue, so it also shows that the
/// switch keeps the stream continuous and what it costs in underruns under load. A plugin adds its pipeline (one
/// block at depth 2) on top; the offline tests measure that part exactly.
/// </summary>
/// <remarks>
/// Run: <c>dotnet run -c Release --project src/Bluestone.Benchmarks -- --live [seconds] [--pressure none|app|churn]
/// [--buffer 128] [--queue 1024] [--live-queue 256]</c>.
/// </remarks>
internal static class LiveLatencyProbe
{
    private const int SampleRate = 48_000;

    public static void Run(string[] args)
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var s) ? s : 20;
        var pressure = AudioCallbackProbe.Option(args, "--pressure") ?? "app";
        var buffer = int.Parse(AudioCallbackProbe.Option(args, "--buffer") ?? "128", CultureInfo.InvariantCulture);
        var queue = int.Parse(AudioCallbackProbe.Option(args, "--queue") ?? "1024", CultureInfo.InvariantCulture);
        var liveQueue = int.Parse(AudioCallbackProbe.Option(args, "--live-queue") ?? "256", CultureInfo.InvariantCulture);
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        Console.WriteLine($"Device: {AudioDevices.List().FirstOrDefault(d => d.IsDefault)?.Name ?? "default"}; {buffer}-frame buffer and blocks at {SampleRate} Hz; pressure: {pressure}; {seconds} s per queue");
        var probe = new ProbeNode();
        using var engine = new AudioEngine(SampleRate, buffer);
        engine.Publish(AudioCallbackProbe.Graph(buffer, 32, probe));
        using var output = AudioOutput.Open(engine, new AudioStreamRequest(SampleRate: SampleRate, BufferFrames: buffer, QueuedFrames: queue));
        output.Start();
        Thread.Sleep(500);
        Console.WriteLine($"Render thread: {output.RenderThreadPolicy}; device output latency {output.Status.OutputLatency.TotalMilliseconds:0.0} ms");
        Console.WriteLine("queue | notes | min | p50 | p95 | max | underrun frames | xruns");

        using var stop = new CancellationTokenSource();
        var threads = new List<Thread>();
        if (pressure == "app")
        {
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.Application(stop.Token)));
        }

        if (pressure == "churn")
        {
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.Churn(stop.Token)));
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.Churn(stop.Token)));
        }

        Console.WriteLine(Phase(output, probe, queue, seconds));
        output.SetQueuedFrames(liveQueue);
        Console.WriteLine(Phase(output, probe, liveQueue, seconds));
        output.SetQueuedFrames(queue);
        Console.WriteLine(Phase(output, probe, queue, seconds) + " (back to the playback queue)");
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        output.Stop();
    }

    private static string Phase(AudioOutput output, ProbeNode probe, int queue, int seconds)
    {
        Thread.Sleep(200);
        var before = output.Status;
        probe.Latencies.Clear();
        var random = new Random(35);
        var until = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            probe.Played.Enqueue(Stopwatch.GetTimestamp());
            Thread.Sleep(20 + random.Next(30));
        }

        Thread.Sleep(200);
        var after = output.Status;
        var sorted = probe.Latencies.Order().ToArray();
        string Ms(double ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("0.0", CultureInfo.InvariantCulture) + " ms";
        return sorted.Length == 0
            ? $"{queue} | 0"
            : string.Join(" | ", [
                $"{queue} ({queue * 1000.0 / SampleRate:0.0} ms)",
                sorted.Length.ToString(CultureInfo.InvariantCulture),
                Ms(sorted[0]),
                Ms(sorted[sorted.Length / 2]),
                Ms(sorted[(int)(sorted.Length * 0.95)]),
                Ms(sorted[^1]),
                (after.QueueUnderrunFrames - before.QueueUnderrunFrames).ToString(CultureInfo.InvariantCulture),
                (after.Xruns - before.Xruns).ToString(CultureInfo.InvariantCulture),
            ]);
    }

    /// <summary>Takes what was played at the start of each block, as a live route does, and notes when that block is heard.</summary>
    private sealed class ProbeNode : AudioNode
    {
        public ConcurrentQueue<long> Played { get; } = new();

        public ConcurrentBag<long> Latencies { get; } = [];

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            while (Played.TryDequeue(out var played))
            {
                Latencies.Add(context.HeardTimestamp - played);
            }
        }
    }
}
