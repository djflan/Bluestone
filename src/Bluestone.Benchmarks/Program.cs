using BenchmarkDotNet.Running;
using Bluestone.Benchmarks;
using Bluestone.Midi.Endpoints;

if (args is ["--jitter", ..])
{
    var seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 10;
    var mode = args.Contains("--realtime") ? "realtime" : args.Contains("--promote") ? "promote" : args.Contains("--timer") ? "timer" : "default";
    Action? setup = mode switch { "realtime" => Realtime, "promote" => Promote, "timer" => Timer, _ => null };
    Console.WriteLine($"Playback thread: {mode}");
    JitterProbe.Run(TimeSpan.FromSeconds(seconds), EndpointCapabilities.None, setup);
    Console.WriteLine();
    JitterProbe.Run(TimeSpan.FromSeconds(seconds), EndpointCapabilities.ScheduledDelivery, setup);
    return;
}

if (args is ["--audio", ..])
{
    AudioCallbackProbe.Run(args);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Workloads).Assembly).Run(args);

static void Realtime()
{
    if (OperatingSystem.IsMacOS())
    {
        Bluestone.Platform.CoreMidi.ThreadScheduling.MakeCurrentThreadRealtime(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5));
    }
    else if (OperatingSystem.IsWindows())
    {
        Bluestone.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
        Bluestone.Platform.Windows.ThreadScheduling.JoinProAudioTask();
    }
}

static void Promote()
{
    if (OperatingSystem.IsMacOS())
    {
        Bluestone.Platform.CoreMidi.ThreadScheduling.PromoteCurrentThread();
    }
    else if (OperatingSystem.IsWindows())
    {
        Bluestone.Platform.Windows.ThreadScheduling.JoinProAudioTask();
    }
}

static void Timer()
{
    if (OperatingSystem.IsWindows())
    {
        Bluestone.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
    }
}
