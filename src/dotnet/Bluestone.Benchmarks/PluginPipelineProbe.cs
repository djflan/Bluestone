using System.Diagnostics;
using System.Globalization;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Benchmarks;

/// <summary>
/// Whether a plugin instrument can be played live without the worker pipeline's extra block (ADR 0043, plan item 3).
/// Hosts Bluestone's reference sine instrument in a real worker process and drives it block by block at the device's
/// pace from a real-time thread, as the audio callback would, with notes arriving. At pipeline depth 2 (today) a
/// block's result is collected one block later, adding a block of latency; at depth 1 the block is submitted and
/// collected in the same call, so the result must come back within the block.
/// </summary>
/// <remarks>
/// Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --plugin-pipeline [seconds]
/// [--depth 1|2] [--buffer 128] [--pressure none|app] [--collect-wait-us 1500]</c>.
/// </remarks>
internal static class PluginPipelineProbe
{
    private const int SampleRate = 48_000;

    public static void Run(ProbeOptions options)
    {
        var depth = options.Int("--depth", 1);
        var frames = options.Int("--buffer", 128);
        var period = TimeSpan.FromSeconds((double)frames / SampleRate);
        var collectWait = TimeSpan.FromMicroseconds(options.Int("--collect-wait-us", (int)(period.TotalMicroseconds * 0.6)));
        var settings = options.BaseSettings();
        settings["depth"] = depth.ToString(CultureInfo.InvariantCulture);
        settings["buffer"] = frames.ToString(CultureInfo.InvariantCulture);
        settings["collect-wait"] = $"{collectWait.TotalMicroseconds:0} µs";
        var report = new TimingReport("plugin-pipeline", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Plugin pipeline: depth {depth}, {frames} frames at {SampleRate} Hz ({period.TotalMilliseconds:0.00} ms), collect wait {collectWait.TotalMicroseconds:0} µs, {options.Seconds} s");

        var data = Path.Combine(Path.GetTempPath(), $"bluestone-plugin-probe-{Environment.ProcessId}");
        var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = data, PipelineDepth = depth, CollectWait = collectWait });
        try
        {
            var sine = new PluginIdentity("bluestone-reference", "bluestone.reference", "reference.sine", "Reference Sine", "Bluestone", PluginKind.Instrument, "1.0.0");
            var instance = manager.CreateInstanceAsync(new PluginInstanceRequest(sine, SampleRate, frames, 0, 2)).GetAwaiter().GetResult();
            if (instance.Status.State != PluginInstanceState.Running)
            {
                Console.WriteLine($"The instrument did not start: {instance.Status}");
                return;
            }

            using var stop = new CancellationTokenSource();
            var threads = options.StartPressure(stop.Token);
            Thread.Sleep(threads.Count > 0 ? 1000 : 200);
            using var gc = new GcWatch();
            var blocks = (int)(options.Seconds * SampleRate / frames);
            var durations = new double[blocks];
            var outcomes = new Dictionary<ProcessOutcome, int>();
            var lateness = new double[blocks];
            var thread = new Thread(() =>
            {
                options.ThreadSetup()?.Invoke();
                var sleep = options.PreciseSleep();
                var output = new float[2 * frames];
                var events = new PluginEvent[1];
                var outputEvents = new PluginEvent[16];
                var next = Stopwatch.GetTimestamp();
                var step = (long)(period.TotalSeconds * Stopwatch.Frequency);
                var position = 0L;
                for (var b = 0; b < blocks; b++)
                {
                    next += step;
                    while (Stopwatch.GetTimestamp() < next - (Stopwatch.Frequency / 2000))
                    {
                        if (sleep is not null)
                        {
                            sleep(TimeSpan.FromTicks((next - Stopwatch.GetTimestamp()) * TimeSpan.TicksPerSecond / Stopwatch.Frequency / 2));
                        }
                        else
                        {
                            Thread.Sleep(0);
                        }
                    }

                    while (Stopwatch.GetTimestamp() < next)
                    {
                        Thread.SpinWait(10);
                    }

                    // A note every 40 blocks, released 20 blocks later: the instrument has work to do.
                    var input = b % 40 == 0 ? (events[0] = PluginEvent.NoteOn(0, 0, 60 + (b / 40 % 12), 100), 1).Item2
                        : b % 40 == 20 ? (events[0] = PluginEvent.NoteOff(0, 0, 60 + (b / 40 % 12)), 1).Item2 : 0;
                    var started = Stopwatch.GetTimestamp();
                    lateness[b] = (started - next) * 1000.0 / Stopwatch.Frequency;
                    var outcome = instance.ProcessBlock(frames, [], output, events.AsSpan(0, input), outputEvents, new TransportState(position, 120, true));
                    durations[b] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    outcomes[outcome.Outcome] = outcomes.GetValueOrDefault(outcome.Outcome) + 1;
                    position += frames;
                }
            })
            { IsBackground = true, Priority = ThreadPriority.Highest, Name = "probe audio" };
            thread.Start();
            thread.Join();
            gc.AddTo(report);
            stop.Cancel();
            foreach (var t in threads)
            {
                t.Join();
            }

            var processed = outcomes.GetValueOrDefault(ProcessOutcome.Processed);
            var priming = outcomes.GetValueOrDefault(ProcessOutcome.Priming);
            report.Add(new Metric("plugin.period", "ms", period.TotalMilliseconds));
            report.Add(new Metric("plugin.added-latency", "ms", (depth - 1) * period.TotalMilliseconds));
            report.Add(new Metric("plugin.processed", "blocks", processed));
            report.Add(new Metric("plugin.not-processed", "blocks", blocks - processed - priming, Target: 0));
            report.Add(new Metric("plugin.call", "ms", Distribution: Distribution.Of(durations), Target: period.TotalMilliseconds));
            report.Add(new Metric("plugin.call-start-lateness", "ms", Distribution: Distribution.Of(lateness)));
            report.Notes.Add("outcomes: " + string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}")));
            options.Finish(report);
        }
        finally
        {
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
