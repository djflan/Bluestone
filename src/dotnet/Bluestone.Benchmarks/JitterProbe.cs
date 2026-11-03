using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;

namespace Bluestone.Benchmarks;

/// <summary>
/// Plays dense material for real on the playback thread with the system clock and reports dispatch lateness: how much
/// later than due each message was handed to the endpoint. This measures the managed scheduler, garbage collection
/// and OS wake-up behaviour, not any MIDI driver (<see cref="MidiLoopbackProbe"/> measures through Core MIDI).
/// </summary>
/// <remarks>
/// Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --jitter [seconds] [--pressure none|app|churn]
/// [--sched default|timer|promote|realtime]</c>. Targets: ADR 0043.
/// </remarks>
internal static class JitterProbe
{
    public static void Run(ProbeOptions options)
    {
        var settings = options.BaseSettings();
        var report = new TimingReport("midi-dispatch", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"MIDI dispatch: {options.Seconds} s per delivery class; pressure {settings["pressure"]}; playback thread {options.Scheduling}");
        var (sequence, plan) = Prepare();
        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 0); // let the pressure reach its steady state
        using var gc = new GcWatch();
        Measure(report, "immediate", EndpointCapabilities.None, options, sequence, plan);
        Measure(report, "scheduled", EndpointCapabilities.ScheduledDelivery, options, sequence, plan);
        gc.AddTo(report);
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        options.Finish(report);
    }

    /// <summary>
    /// The material and its compiled plan, made before measuring and followed by a full collection, so the probe's own
    /// set-up garbage is not collected during the run (the pressure options stand for the application's allocations).
    /// </summary>
    internal static (Sequence Sequence, PlaybackPlan Plan) Prepare(int tracks = 16)
    {
        var sequence = Workloads.Dense(tracks, 20_000);
        var plan = Workloads.Compile(Workloads.Routed(sequence));
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return (sequence, plan);
    }

    private static void Measure(TimingReport report, string name, EndpointCapabilities delivery, ProbeOptions options, Sequence sequence, PlaybackPlan plan)
    {
        var clock = SystemMonotonicClock.Instance;
        var output = new CountingOutput(delivery);
        using var engine = new PlaybackEngine(clock, sequence.TempoMap);
        engine.SetOutputs([output]);
        engine.Load(plan);
        using (new PlaybackThread(engine, options.ThreadSetup(), options.PreciseSleep()))
        {
            engine.Play(Tick.Zero);
            Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
            engine.Stop();
            Thread.Sleep(50);
        }

        var stats = engine.Statistics.Snapshot();
        report.Add(new Metric($"midi.{name}.lateness", "ms",
            Distribution: Distribution.OfHistogram(stats.FineHistogram, TimingStatistics.FineBucketWidth.TotalMilliseconds, stats.MaxLateness.TotalMilliseconds),
            Target: Targets.MidiLatenessMs));
        report.Add(new Metric($"midi.{name}.late-over-2ms", "messages", stats.Late));
        report.Add(new Metric($"midi.{name}.dropped", "messages", stats.Dropped + stats.SkippedLateNotes, Target: 0));
        report.Add(new Metric($"midi.{name}.rate", "messages/s", stats.Dispatched / (double)options.Seconds));
    }
}
