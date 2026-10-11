using System.Globalization;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;

namespace Bluestone.Benchmarks;

/// <summary>
/// Plays dense material for real on the playback thread with the system clock and reports dispatch
/// lateness. This measures the managed scheduler and OS wake-up behaviour, not any MIDI driver.
/// </summary>
internal static class JitterProbe
{
    public static void Run(TimeSpan duration, EndpointCapabilities delivery, Action? onThreadStart = null)
    {
        var sequence = Workloads.Dense(16, 2_000);
        var clock = SystemMonotonicClock.Instance;
        var output = new CountingOutput(delivery);
        using var engine = new PlaybackEngine(clock, sequence.TempoMap);
        engine.SetOutputs([output]);
        engine.Load(Workloads.Compile(Workloads.Routed(sequence)));

        var gcBefore = GC.CollectionCount(0);
        using (new PlaybackThread(engine, onThreadStart))
        {
            engine.Play(Tick.Zero);
            Thread.Sleep(duration);
            engine.Stop();
            Thread.Sleep(50);
        }

        var stats = engine.Statistics.Snapshot();
        var ms = (TimeSpan t) => t.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture);
        Console.WriteLine($"Delivery class      : {(delivery.HasFlag(EndpointCapabilities.ScheduledDelivery) ? "scheduled (20 ms look-ahead)" : "immediate (sent when due)")}");
        Console.WriteLine($"Duration            : {duration.TotalSeconds:0} s, {stats.Dispatched} messages ({stats.Dispatched / duration.TotalSeconds:0}/s)");
        Console.WriteLine($"Lateness p50/p95/p99: ≤ {ms(stats.Percentile(50))} / ≤ {ms(stats.Percentile(95))} / ≤ {ms(stats.Percentile(99))} ms");
        Console.WriteLine($"Lateness max        : {ms(stats.MaxLateness)} ms; late (> 2 ms): {stats.Late}; dropped: {stats.Dropped}; skipped: {stats.SkippedLateNotes}");
        Console.WriteLine($"Gen0 GCs during run : {GC.CollectionCount(0) - gcBefore}");
        Console.WriteLine($"Histogram (≤0.25, 0.5, 1, 2, 5, 10, 20, 50, >50 ms): {string.Join(", ", stats.Histogram)}");
    }
}
