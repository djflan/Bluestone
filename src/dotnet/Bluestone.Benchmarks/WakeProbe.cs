using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bluestone.Benchmarks;

/// <summary>
/// How late a thread wakes from a timed wait (ADR 0043): waits a random 2–60 ms with one primitive, then measures how
/// long after the deadline the thread runs again. This isolates the operating system's wake-up behaviour, which bounds
/// MIDI dispatch for endpoints without scheduled delivery whatever the language.
/// </summary>
/// <remarks>
/// Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --wake [seconds] [--sched default|promote|realtime]
/// [--wait waitone|sleep|mach]</c>. <c>mach</c> is <c>mach_wait_until</c> (macOS).
/// </remarks>
internal static partial class WakeProbe
{
    public static void Run(ProbeOptions options)
    {
        var primitive = ProbeOptions.Option(options.Args, "--wait") ?? "waitone";
        var settings = options.BaseSettings();
        settings["wait"] = primitive;
        var report = new TimingReport("wake", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Wake-up: {primitive} on a {options.Scheduling} thread, {options.Seconds} s, pressure {settings["pressure"]}");
        using var stop = new CancellationTokenSource();
        var pressure = options.StartPressure(stop.Token);
        var lateness = new List<double>(options.Seconds * 100);
        using var signal = new AutoResetEvent(false);
        var thread = new Thread(() =>
        {
            options.ThreadSetup()?.Invoke();
            var random = new Random(11);
            var until = Stopwatch.GetTimestamp() + (options.Seconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < until)
            {
                var wait = TimeSpan.FromMilliseconds(2 + (random.NextDouble() * 58));
                var due = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
                switch (primitive)
                {
                    case "sleep":
                        Thread.Sleep(wait);
                        break;
                    case "mach" when OperatingSystem.IsMacOS():
                        _ = mach_wait_until(HostDeadline(wait));
                        break;
                    default:
                        signal.WaitOne(wait);
                        break;
                }

                lateness.Add((Stopwatch.GetTimestamp() - due) * 1000.0 / Stopwatch.Frequency);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Highest };
        using var gc = new GcWatch();
        thread.Start();
        thread.Join();
        gc.AddTo(report);
        stop.Cancel();
        foreach (var t in pressure)
        {
            t.Join();
        }

        report.Add(new Metric("wake.lateness", "ms", Distribution: Distribution.Of(lateness)));
        report.Add(new Metric("wake.over-1ms", "wake-ups", lateness.Count(l => l > 1)));
        options.Finish(report);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static ulong HostDeadline(TimeSpan wait)
    {
        _ = SinkNative.mach_timebase_info(out var timebase);
        var ticks = (ulong)(wait.TotalMilliseconds * 1e6 * timebase.Denom / timebase.Numer);
        return SinkNative.mach_absolute_time() + ticks;
    }

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int mach_wait_until(ulong deadline);
}
