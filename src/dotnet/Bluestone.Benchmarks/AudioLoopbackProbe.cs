using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Bluestone.Audio;
using Bluestone.Audio.Devices;

namespace Bluestone.Benchmarks;

/// <summary>
/// Checks the engine's heard-time model against a real loopback (ADR 0043, plan item 4). A live note is "played"
/// (timestamped with the system clock, as MIDI input is) and becomes a single-sample click at the start of the next
/// block, as a live route would; the model says when that block is heard (<see cref="AudioRenderContext.HeardTimestamp"/>:
/// the callback's time plus the device's reported output latency, plus any render-ahead queue). The click goes out to a
/// device whose output is looped back to an input (BlackHole, or a cable from an interface's output to its input),
/// and the probe finds it in the captured audio and works out when it was captured from the input's own timestamps.
/// </summary>
/// <remarks>
/// <para>
/// Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --audio-loopback [seconds]
/// --device "BlackHole 2ch" [--input-device NAME] [--buffer 128] [--queue 0]</c>. The output device and the input
/// device (the same one by default) must be looped back; nothing is played anywhere else.
/// </para>
/// <para>
/// <c>loopback.model-error</c> is when the click was captured less when the model said it would be heard. For a digital
/// loopback such as BlackHole it measures what the model does not account for; through an interface it also contains
/// the converters' latency, which the device may not report. <c>loopback.total</c> is from the note's timestamp to the
/// capture: what a musician monitoring through that output would hear, the device's own path included.
/// </para>
/// </remarks>
internal static class AudioLoopbackProbe
{
    private const int SampleRate = 48_000;

    public static void Run(ProbeOptions options)
    {
        var buffer = options.Int("--buffer", 128);
        var queue = options.Int("--queue", 0);
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        var (outputId, outputName) = options.OutputDevice();
        var host = ProbeOptions.Option(options.Args, "--host");
        var inputName = ProbeOptions.Option(options.Args, "--input-device") ?? outputName;
        var inputs = AudioDevices.ListInputs().Where(d => host is null || d.Host.Equals(host, StringComparison.OrdinalIgnoreCase)).ToArray();
        var input = inputs.SingleOrDefault(d => d.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No input device is called {inputName}. Inputs: {string.Join(", ", inputs.Select(d => d.Name))}.");
        var settings = options.BaseSettings();
        settings["device"] = outputName;
        settings["input-device"] = input.Name;
        settings["buffer"] = buffer.ToString(CultureInfo.InvariantCulture);
        settings["rate"] = SampleRate.ToString(CultureInfo.InvariantCulture);
        settings["queue"] = queue == 0 ? "0 (direct)" : queue.ToString(CultureInfo.InvariantCulture);
        var report = new TimingReport("audio-loopback", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Audio loopback: out {outputName}, in {input.Name}; {buffer} frames at {SampleRate} Hz; queue {settings["queue"]}; {options.Seconds} s");

        var capacity = (options.Seconds + 2) * 20;
        var click = new ClickNode(capacity);
        var onsets = new OnsetDetector(capacity, threshold: 0.5f);
        using var engine = new AudioEngine(SampleRate, buffer);
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(click));
        engine.Publish(builder.Build(buffer, new MixerStripState()));
        using var capture = AudioInput.Open(onsets.Capture, input.Id, SampleRate, buffer, host: host);
        using var output = AudioOutput.Open(engine, new AudioStreamRequest(DeviceId: outputId, SampleRate: SampleRate, BufferFrames: buffer, QueuedFrames: queue, Host: host));
        capture.Start();
        output.Start();
        Thread.Sleep(500);

        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 200);
        var random = new Random(4);
        var until = Stopwatch.GetTimestamp() + (options.Seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            click.Played.Enqueue(Stopwatch.GetTimestamp());
            Thread.Sleep(50 + random.Next(100));
        }

        Thread.Sleep(300);
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        output.Stop();
        capture.Stop();
        var outputStatus = output.Status;
        var inputStatus = capture.Status;

        // Pair each click with the first onset captured after the model's heard time less 20 ms (anything earlier
        // would be another click); clicks are 50 ms or more apart.
        var clicks = click.Clicks().ToArray();
        var captured = onsets.Onsets().ToArray();
        var errors = new List<double>();
        var totals = new List<double>();
        var unmatched = 0;
        var next = 0;
        foreach (var (played, heard) in clicks)
        {
            var window = heard - (Stopwatch.Frequency / 50);
            while (next < captured.Length && captured[next] < window)
            {
                next++;
            }

            if (next < captured.Length && captured[next] < heard + (Stopwatch.Frequency / 20))
            {
                errors.Add(Ms(captured[next] - heard));
                totals.Add(Ms(captured[next] - played));
                next++;
            }
            else
            {
                unmatched++;
            }
        }

        report.Add(new Metric("audio.output-latency", "ms", outputStatus.OutputLatency.TotalMilliseconds));
        report.Add(new Metric("audio.input-latency", "ms", inputStatus.InputLatency.TotalMilliseconds));
        report.Add(new Metric("audio.period", "ms", buffer * 1000.0 / SampleRate));
        report.Add(new Metric("loopback.clicks", "clicks", clicks.Length));
        report.Add(new Metric("loopback.unmatched", "clicks", unmatched, Target: 0));
        report.Add(new Metric("loopback.model-error", "ms", Distribution: Distribution.Of(errors)));
        report.Add(new Metric("loopback.model-error-spread", "ms", errors.Count == 0 ? 0 : errors.Max() - errors.Min(), Target: 0.5));
        report.Add(new Metric("loopback.model-error-offset", "ms", errors.Count == 0 ? 0 : Math.Abs(Distribution.Of(errors).P50), Target: 0.5));
        report.Add(new Metric("loopback.total", "ms", Distribution: Distribution.Of(totals)));
        report.Add(new Metric("audio.xruns", "xruns", outputStatus.Xruns, Target: 0));
        report.Add(new Metric("audio.input-overruns", "overruns", inputStatus.Overruns, Target: 0));
        report.Add(new Metric("audio.input-oversized-blocks", "blocks", inputStatus.OversizedBlocks, Target: 0));
        report.Notes.Add("model-error is capture time less the model's heard time; its spread is how much the model's timing varies, its median the offset it does not account for.");
        options.Finish(report);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Sounds a single full-scale sample at the start of the block that takes a played note, and records when the model says that block is heard.</summary>
    private sealed class ClickNode(int capacity) : AudioNode
    {
        private readonly long[] _played = new long[capacity];
        private readonly long[] _heard = new long[capacity];
        private int _count;

        public ConcurrentQueue<long> Played { get; } = new();

        public IEnumerable<(long Played, long Heard)> Clicks() => Enumerable.Range(0, Volatile.Read(ref _count)).Select(i => (_played[i], _heard[i]));

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output)
        {
            if (Played.TryDequeue(out var played) && _count < _played.Length)
            {
                output.Left[0] = 1f;
                output.Right[0] = 1f;
                _played[_count] = played;
                _heard[_count] = context.HeardTimestamp;
                Volatile.Write(ref _count, _count + 1);
            }
        }
    }

    /// <summary>Finds rising edges above a threshold in the first captured channel and records when each was captured.</summary>
    private sealed class OnsetDetector(int capacity, float threshold)
    {
        private readonly long[] _onsets = new long[capacity * 2];
        private int _count;
        private bool _above;

        public IEnumerable<long> Onsets() => _onsets.Take(Volatile.Read(ref _count));

        public void Capture(ReadOnlySpan<float> interleaved, int frames, int channels, in AudioCaptureBlock block)
        {
            for (var i = 0; i < frames; i++)
            {
                var above = Math.Abs(interleaved[i * channels]) >= threshold;
                if (above && !_above && _count < _onsets.Length)
                {
                    _onsets[_count] = block.CapturedTimestamp + (i * Stopwatch.Frequency / block.SampleRate);
                    Volatile.Write(ref _count, _count + 1);
                }

                _above = above;
            }
        }
    }
}
