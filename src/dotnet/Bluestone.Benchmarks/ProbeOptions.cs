using System.Diagnostics;
using System.Globalization;

namespace Bluestone.Benchmarks;

/// <summary>
/// Options shared by the timing probes (ADR 0043):
/// <c>[seconds] [--pressure none|app|edit|churn|full|cpu] [--sched default|timer|promote|realtime] [--report DIR|FILE.json]
/// [--repeat N] [--isolate] [--host WASAPI|ASIO] [--device NAME]</c>.
/// </summary>
/// <remarks>
/// <c>--isolate</c> is the "separate engine process" experiment: the probe runs again as a child process with no
/// pressure (or <c>--child-pressure</c>, such as <c>edit</c> for an engine that compiles plans) while this process
/// creates the requested pressure for the same time. The child's report is labelled <c>isolation=process</c>.
/// </remarks>
internal sealed record ProbeOptions(string[] Args, int Seconds, string Pressure, string Scheduling, string ReportDestination, int Repeat, bool Isolate, string? PressureElsewhere)
{
    public const string DefaultReportDirectory = "TestResults/timing";

    public static ProbeOptions Parse(string[] args, int defaultSeconds = 60, string defaultPressure = "app")
    {
        var seconds = args.Length > 1 && int.TryParse(args[1], CultureInfo.InvariantCulture, out var s) ? s : defaultSeconds;
        var scheduling = Option(args, "--sched")
            ?? (args.Contains("--realtime") ? "realtime" : args.Contains("--promote") ? "promote" : args.Contains("--timer") ? "timer" : "realtime");
        return new ProbeOptions(
            args,
            seconds,
            Option(args, "--pressure") ?? defaultPressure,
            scheduling,
            Option(args, "--report") ?? DefaultReportDirectory,
            int.Parse(Option(args, "--repeat") ?? "1", CultureInfo.InvariantCulture),
            args.Contains("--isolate"),
            Option(args, "--pressure-elsewhere"));
    }

    public static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public int Int(string name, int fallback) => int.Parse(Option(Args, name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public string? OutputHost() => Option(Args, "--host");

    /// <summary>
    /// The output device named by <c>--device</c> (exact name, or a unique part of it, ignoring case), or the system
    /// default without it. Naming the device keeps a measurement series on one device even if the default changes, for
    /// example when a headset is plugged in.
    /// </summary>
    public (string? Id, string Name) OutputDevice()
    {
        var devices = Bluestone.Audio.Devices.AudioDevices.List();
        var host = Option(Args, "--host");
        if (host is not null)
        {
            devices = [.. devices.Where(d => d.Host.Equals(host, StringComparison.OrdinalIgnoreCase))];
            if (devices.IsEmpty)
            {
                throw new ArgumentException($"--host {host} has no output devices. Hosts: {string.Join(", ", Bluestone.Audio.Devices.AudioDevices.ListHosts())}.");
            }
        }

        if (Option(Args, "--device") is not { } wanted)
        {
            var defaultName = devices.FirstOrDefault(d => d.IsDefault)?.Name
                ?? (host is null ? "default" : devices[0].Name);
            return (null, defaultName);
        }

        var matches = devices.Where(d => d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            matches = [.. devices.Where(d => d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];
        }

        return matches.Count == 1
            ? (matches[0].Id, matches[0].Name)
            : throw new ArgumentException($"--device {wanted} matches {(matches.Count == 0 ? "no" : "more than one")} output device. Devices: {string.Join(", ", devices.Select(d => $"{d.Name} ({d.Host})"))}.");
    }

    /// <summary>Settings every report carries.</summary>
    public Dictionary<string, string> BaseSettings() => new()
    {
        ["seconds"] = Seconds.ToString(CultureInfo.InvariantCulture),
        ["pressure"] = PressureElsewhere is { } elsewhere ? $"{Pressure} here; {elsewhere} in another process" : Pressure,
        ["isolation"] = PressureElsewhere is null ? "in-process" : "process",
        ["scheduling"] = Scheduling,
        ["final-approach"] = PreciseSleep() is null ? "spin" : "precise sleep",
        ["host"] = Option(Args, "--host") ?? "system default",
    };

    /// <summary>Starts the pressure threads for <see cref="Pressure"/>; cancel the token and join them to stop.</summary>
    public List<Thread> StartPressure(CancellationToken stop)
    {
        var threads = new List<Thread>();
        if (Pressure == "app")
        {
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.Application(stop)));
        }

        if (Pressure == "edit")
        {
            threads.Add(AudioCallbackProbe.Start(() => Edits(stop)));
        }

        if (Pressure is "churn" or "full")
        {
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.Churn(stop)));
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.Churn(stop)));
        }

        if (Pressure == "full")
        {
            threads.Add(AudioCallbackProbe.Start(() => AudioCallbackProbe.FullCollections(stop)));
        }

        // Every core busy at normal priority, as a build or a browser makes it: an audio thread that the scheduler does
        // not treat as audio waits behind these.
        if (Pressure == "cpu")
        {
            for (var i = 0; i < Environment.ProcessorCount; i++)
            {
                threads.Add(AudioCallbackProbe.Start(() => Spin(stop)));
            }
        }

        return threads;
    }

    private static void Spin(CancellationToken stop)
    {
        var x = 1.0;
        while (!stop.IsCancellationRequested)
        {
            for (var i = 0; i < 100_000; i++)
            {
                x = Math.Sqrt(x + i);
            }
        }

        GC.KeepAlive(x);
    }

    // What Bluestone itself does while a musician edits during loop playback: every edit evaluates the routing and
    // recompiles the playback plan (ADR 0012: about 8 MB for 16 x 1,000 notes). Four edits a second.
    private static void Edits(CancellationToken stop)
    {
        var project = Workloads.Routed(Workloads.Dense(16, 1_000));
        while (!stop.IsCancellationRequested)
        {
            GC.KeepAlive(Workloads.Compile(project));
            stop.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
        }
    }

    /// <summary>
    /// Runs this probe in a child process without pressure while this process makes the pressure; returns the
    /// child's exit code. The child writes its own report.
    /// </summary>
    public int RunIsolated()
    {
        var childArgs = new List<string>();
        for (var i = 0; i < Args.Length; i++)
        {
            if (Args[i] == "--isolate")
            {
                continue;
            }

            if (Args[i] is "--pressure" or "--child-pressure" or "--repeat")
            {
                i++;
                continue;
            }

            childArgs.Add(Args[i]);
        }

        childArgs.AddRange(["--pressure", Option(Args, "--child-pressure") ?? "none", "--pressure-elsewhere", Pressure]);
        Console.WriteLine($"Isolated: the probe runs in a child process; this process makes '{Pressure}' pressure.");
        using var stop = new CancellationTokenSource();
        var threads = StartPressure(stop.Token);
        using var child = StartSelf(childArgs);
        child.WaitForExit();
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        return child.ExitCode;
    }

    /// <summary>Starts this benchmark program again with <paramref name="args"/>, sharing the console.</summary>
    public static Process StartSelf(IEnumerable<string> args, bool redirect = false)
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = redirect, RedirectStandardInput = redirect };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(ProbeOptions).Assembly.Location);
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the child process.");
    }

    /// <summary>The playback-thread setup named by <see cref="Scheduling"/>, for this platform.</summary>
    public Action? ThreadSetup() => Scheduling switch
    {
        "realtime" => ThreadModes.Realtime,
        "promote" => ThreadModes.Promote,
        "timer" => ThreadModes.Timer,
        _ => null,
    };

    /// <summary>
    /// The playback thread's final approach to a due time: <c>--final sleep</c> uses the platform's precise sleep,
    /// <c>--final spin</c> spins as before ADR 0043.
    /// </summary>
    public Action<TimeSpan>? PreciseSleep()
    {
        if ((Option(Args, "--final") ?? "sleep") != "sleep")
        {
            return null;
        }

        return OperatingSystem.IsMacOS()
            ? Bluestone.Platform.CoreMidi.ThreadScheduling.SleepPrecisely
            : OperatingSystem.IsWindows() ? Bluestone.Platform.Windows.ThreadScheduling.SleepPrecisely : null;
    }

    /// <summary>Prints and writes the report.</summary>
    public void Finish(TimingReport report)
    {
        report.Print();
        Console.WriteLine($"  report: {report.Write(ReportDestination)}");
    }

    internal static class ThreadModes
    {
        public static void Realtime()
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

        public static void Promote()
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

        public static void Timer()
        {
            if (OperatingSystem.IsWindows())
            {
                Bluestone.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
            }
        }
    }
}
