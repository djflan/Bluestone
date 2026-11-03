using BenchmarkDotNet.Running;
using Bluestone.Benchmarks;

// Timing probes (ADR 0043): each prints its metrics and writes a JSON report (--report DIR|FILE.json, default
// TestResults/timing). Common options: [seconds] [--pressure none|app|churn|full] [--sched default|timer|promote|realtime]
// [--repeat N] [--isolate]. Anything else runs the BenchmarkDotNet micro-benchmarks.
if (args is ["--midi-sink", var sinkFile, ..])
{
    if (OperatingSystem.IsMacOS())
    {
        MidiSink.Run(sinkFile);
    }

    return 0;
}

if (args is ["--compare", var before, var after, ..])
{
    TimingReport.Compare(before, after);
    return 0;
}

Action<ProbeOptions>? probe = args switch
{
    ["--jitter", ..] => JitterProbe.Run,
    ["--live", ..] => LiveLatencyProbe.Run,
    ["--audio", ..] => AudioCallbackProbe.Run,
    ["--wake", ..] => WakeProbe.Run,
    ["--audio-loopback", ..] => AudioLoopbackProbe.Run,
    ["--midi-loopback", ..] when OperatingSystem.IsMacOS() => MidiLoopbackProbe.Run,
    ["--midi-loopback", ..] => _ => Console.WriteLine("The MIDI loopback probe needs Core MIDI (macOS)."),
    _ => null,
};

if (probe is null)
{
    BenchmarkSwitcher.FromAssembly(typeof(Workloads).Assembly).Run(args);
    return 0;
}

var options = ProbeOptions.Parse(args);
if (args.Contains("--sustained"))
{
    System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
}

for (var run = 1; run <= options.Repeat; run++)
{
    if (options.Repeat > 1)
    {
        Console.WriteLine($"--- run {run} of {options.Repeat}");
    }

    if (options.Isolate)
    {
        var code = options.RunIsolated();
        if (code != 0)
        {
            return code;
        }
    }
    else
    {
        try
        {
            probe(options);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}

return 0;
