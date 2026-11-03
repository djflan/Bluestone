using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Application.Engine;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Audio.Devices;
using Bluestone.Audio.Files;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Playback;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Profiles;
using Bluestone.Signal;

namespace Bluestone.Benchmarks;

/// <summary>
/// ADR 0043's measurements through the real engine process (ADR 0044 step 8): this process plays Bluestone's role. It
/// starts <c>Bluestone.Engine</c> with <see cref="EngineSupervisor"/> as the app does, sends it plans, graphs, and
/// input through the same clients (<see cref="EnginePlayback"/>, <see cref="EngineAudio"/>), and makes the requested
/// pressure here, where the app's allocations would be. Every run also reports the engine's own collections and pauses
/// from its heartbeats.
/// </summary>
/// <remarks>
/// <para>
/// Run (seconds right after the probe's name): <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks --
/// --jitter|--audio|--live|--audio-loopback [seconds] --engine [--engine-path PATH] [--pressure none|app|edit|churn|full]
/// [--buffer 128] [--device NAME] [--repeat N] [--report DIR]</c>.
/// </para>
/// <para>
/// The engine is the NativeAOT build at <c>--engine-path</c>, else at <c>src/dotnet/artifacts/engine-aot/Bluestone.Engine</c>
/// (publish it with <c>dotnet publish src/dotnet/Bluestone.Engine -c Release -r osx-arm64 -o src/dotnet/artifacts/engine-aot</c>),
/// else the JIT build copied beside the probes, which is not the shipping configuration and is reported as such.
/// </para>
/// </remarks>
internal static class EngineProbes
{
    private const int SampleRate = 48_000;

    // The Core MIDI destination the sink publishes (MidiLoopbackProbe.SinkName), and the source the key process publishes
    // (MidiKeys.SourceName); named here because those types are macOS-only.
    private const string SinkName = "Bluestone Timing Sink";
    private const string MidiKeysSource = "Bluestone Probe Keys";
    private static readonly PluginIdentity Sine = new("bluestone-reference", "bluestone.reference", "reference.sine", "Reference Sine", "Bluestone", PluginKind.Instrument, "1.0.0");

    /// <summary>MIDI playback in the engine: a dense plan to an output the engine opens itself.</summary>
    public static void Jitter(ProbeOptions options)
    {
        var (path, build) = ResolveEngine(options);
        var settings = Settings(options, path, build);
        var report = new TimingReport("engine-midi-dispatch", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Engine MIDI dispatch ({build}): {options.Seconds} s; pressure {settings["pressure"]}");
        var (sequence, plan) = JitterProbe.Prepare();

        using var rig = Rig.Create(path, testAudio: false);
        rig.Start();
        using var sink = Sink.Open(report);
        var output = OpenOutput(rig, sink, report);
        rig.Playback.SetOutputs([output]);
        rig.Playback.Load(plan);
        report.Settings["output"] = sink.Endpoint is null ? "forwarded to the probe (no Core MIDI)" : $"Core MIDI destination '{SinkName}', opened by the engine (scheduled delivery)";
        report.Notes.Add("Lateness is the engine's playback thread handing each message to the output later than due (for a scheduled output such as Core MIDI, later than its send-ahead time), from the engine's own timing figures.");
        Thread.Sleep(500);

        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 200);
        using var gc = new EngineGcWatch(rig.Supervisor);
        rig.Playback.Play(Tick.Zero);

        // The window starts once the engine plays (it maps and reads the plan first).
        var waited = Stopwatch.StartNew();
        while (rig.Playback.State != TransportState.Playing && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(10);
        }

        Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
        rig.Playback.Stop();
        gc.Stop();
        Thread.Sleep(600); // the engine's last timing figures
        StopPressure(stop, threads);
        foreach (var engineProblem in rig.Playback.TakeProblems())
        {
            report.Notes.Add($"The engine reported: {engineProblem}");
        }

        if (rig.Playback.State != TransportState.Stopped && waited.Elapsed >= TimeSpan.FromSeconds(30))
        {
            report.Notes.Add("The engine did not report playing within 30 s.");
        }

        var stats = rig.Playback.SnapshotStatistics();

        // Every message the plan holds for the time played must have gone out: a run that played nothing is not a pass.
        var expected = AtLeast(sequence, TimeSpan.FromSeconds(options.Seconds) - TimeSpan.FromMilliseconds(500));
        report.Add(new Metric("midi.engine.expected", "messages", expected));
        report.Add(new Metric("midi.engine.missing", "messages", Math.Max(0, expected - stats.Dispatched), Target: 0));
        report.Add(new Metric("midi.engine.problems", "problems", report.Notes.Count(n => n.StartsWith("The engine reported", StringComparison.Ordinal)), Target: 0));
        report.Add(new Metric("midi.engine.lateness", "ms",
            Distribution: Distribution.OfHistogram(stats.FineHistogram, TimingStatistics.FineBucketWidth.TotalMilliseconds, stats.MaxLateness.TotalMilliseconds),
            Target: Targets.MidiLatenessMs));
        report.Add(new Metric("midi.engine.late-over-2ms", "messages", stats.Late));
        report.Add(new Metric("midi.engine.dropped", "messages", stats.Dropped + stats.SkippedLateNotes, Target: 0));
        report.Add(new Metric("midi.engine.rate", "messages/s", stats.Dispatched / (double)options.Seconds));
        gc.AddTo(report);
        options.Finish(report);
    }

    // The engine opens the sink's destination itself; Core MIDI may not list it in the engine yet, so it is opened again
    // until the engine has it (or reports why not).
    private static IMidiOutput OpenOutput(Rig rig, Sink sink, TimingReport report)
    {
        if (sink.Endpoint is not { } endpoint)
        {
            return rig.Playback.Forward(new CountingOutput(EndpointCapabilities.None));
        }

        var problems = new List<string>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var output = rig.Playback.OpenOutput(endpoint);
            Thread.Sleep(250);
            problems.AddRange(rig.Playback.TakeProblems());
            if (output.State == EndpointState.Open)
            {
                if (attempt > 0)
                {
                    report.Notes.Add($"The engine opened the destination at attempt {attempt + 1}.");
                }

                return output;
            }

            output.Dispose();
        }

        foreach (var problem in problems.Distinct())
        {
            report.Notes.Add($"The engine reported: {problem}");
        }

        throw new InvalidOperationException($"The engine could not open '{SinkName}': {string.Join("; ", problems.Distinct())}");
    }

    // Messages the sequence's notes send before `elapsed` has played: each note's on, and its off when it ends by then.
    private static long AtLeast(Sequence sequence, TimeSpan elapsed)
    {
        var until = sequence.TempoMap.TickAt(elapsed).Value;
        var count = 0L;
        foreach (var track in sequence.Tracks)
        {
            foreach (var note in track.ArrangedEvents.OfType<NoteEvent>())
            {
                count += (note.Position.Value < until ? 1 : 0) + (note.EndPosition.Value < until ? 1 : 0);
            }
        }

        return count;
    }

    /// <summary>The engine's audio device in direct mode with a realistic graph, while the transport plays.</summary>
    public static void Audio(ProbeOptions options)
    {
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        var buffer = options.Int("--buffer", 128);
        var (deviceId, device) = options.OutputDevice();
        var (path, build) = ResolveEngine(options);
        var settings = Settings(options, path, build);
        settings["device"] = device;
        settings["buffer"] = buffer.ToString(CultureInfo.InvariantCulture);
        settings["mode"] = "direct";
        settings["graph"] = "4 looping clips into channels on a bus, the reference sine through a real worker playing notes";
        var report = new TimingReport("engine-audio", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Engine audio ({build}): {device}, {buffer} frames at {SampleRate} Hz, direct; pressure {settings["pressure"]}; {options.Seconds} s");

        using var rig = Rig.Create(path, testAudio: false);
        using var project = AudioProject.Create(rig, clips: 4, notes: true);
        rig.Start();
        if (!project.Open(deviceId, buffer, report))
        {
            return;
        }

        project.Session.Execute(LoopCommands.SetRange(new TickRange(Tick.Zero, new Tick(480 * 2 * 8)), enable: true));
        project.Controller.RefreshAsync().GetAwaiter().GetResult();
        project.Controller.PlayAsync(Tick.Zero).GetAwaiter().GetResult();
        Thread.Sleep(1000);

        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 200);
        var before = project.Telemetry();
        using var gc = new EngineGcWatch(rig.Supervisor);
        var peak = 0f;
        var until = Stopwatch.GetTimestamp() + (options.Seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            Thread.Sleep(100);
            peak = Math.Max(peak, project.Audio.MeterOf(null)?.PeakLeft ?? 0);
        }

        gc.Stop();
        Thread.Sleep(100);
        var after = project.Telemetry();
        StopPressure(stop, threads);
        project.Controller.Stop();

        var stream = project.Audio.Stream!;
        var period = (double)buffer / stream.SampleRate * 1000;
        var expected = (long)(options.Seconds * stream.SampleRate / (double)buffer);
        report.Add(new Metric("audio.period", "ms", period));
        report.Add(new Metric("audio.output-latency", "ms", after.OutputLatency.TotalMilliseconds));
        report.Add(new Metric("audio.render-max", "ms", after.RenderMax.TotalMilliseconds, Target: period));
        report.Add(new Metric("audio.master-peak", "dBFS", Bluestone.Audio.MeterReading.ToDecibels(peak)));
        report.Add(new Metric("audio.callbacks-missing", "callbacks", Math.Max(0, expected - (after.Callbacks - before.Callbacks)), Target: 0));
        report.Add(new Metric("audio.late-callbacks", "callbacks", after.LateRenders - before.LateRenders, Target: 0));
        report.Add(new Metric("audio.xruns", "xruns", after.Xruns - before.Xruns, Target: 0));
        report.Add(new Metric("audio.gaps", "callbacks", after.Discontinuities - before.Discontinuities, Target: 0));
        report.Add(new Metric("audio.render-faults", "blocks", after.RenderFaults - before.RenderFaults, Target: 0));
        report.Add(new Metric("audio.plugin-failed-blocks", "blocks", after.PluginFailedBlocks - before.PluginFailedBlocks, Target: 0));
        report.Notes.Add("render-max is the longest render since the stream opened (the native layer keeps one maximum), so it includes the first callbacks.");
        gc.AddTo(report);
        options.Finish(report);
    }

    /// <summary>Keys played into the monitored reference sine (depth 1), as the computer keyboard would, measured by the engine's meter.</summary>
    public static void Live(ProbeOptions options)
    {
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        var buffer = options.Int("--buffer", 128);
        var (deviceId, device) = options.OutputDevice();
        var (path, build) = ResolveEngine(options);
        var settings = Settings(options, path, build);
        settings["device"] = device;
        settings["buffer"] = buffer.ToString(CultureInfo.InvariantCulture);
        settings["queue"] = "0 (direct)";
        var keyInput = LiveInput(options);
        settings["input"] = keyInput == "midi" ? "MIDI from another process through a Core MIDI source the engine opens (as a keyboard)" : "forwarded from this process (as the computer keyboard)";
        var report = new TimingReport("engine-live-latency", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Engine live latency ({build}): {device}, {buffer} frames, direct, the monitored reference sine at depth 1; pressure {settings["pressure"]}; {options.Seconds} s");

        using var rig = Rig.Create(path, testAudio: false);
        using var keys = KeySource.Create(keyInput);
        using var project = AudioProject.Create(rig, clips: 0, notes: false, keys.Providers);
        rig.Start();
        if (!project.Open(deviceId, buffer, report) || !project.Monitor() || !keys.Connect(project))
        {
            Console.WriteLine("The reference sine is not played at depth 1, or the keys do not reach it.");
            return;
        }

        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 200);
        var startLive = project.Audio.LiveLatency;
        var startMisses = project.Telemetry().LiveDeadlineMisses;
        using var gc = new EngineGcWatch(rig.Supervisor);
        var played = keys.Play(project, options.Seconds);
        gc.Stop();
        Thread.Sleep(500);
        StopPressure(stop, threads);

        var live = project.Audio.LiveLatency;
        var telemetry = project.Telemetry();
        if (startLive.Count > 0)
        {
            report.Notes.Add($"The engine's meter also holds {startLive.Count} warm-up keys (sent to check the input reaches the instrument); its shortest and longest include them.");
        }

        var period = (double)buffer / SampleRate * 1000;
        var outputLatency = telemetry.OutputLatency.TotalMilliseconds;
        report.Add(new Metric("audio.period", "ms", period));
        report.Add(new Metric("audio.output-latency", "ms", outputLatency));
        report.Add(new Metric("live.played", "notes", played.Count));
        report.Add(new Metric("live.measured", "notes", live.Count - startLive.Count));
        // The average of this run's keys alone; the shortest and longest are the meter's (warm-up keys included).
        var measured = Math.Max(1, live.Count - startLive.Count);
        var average = ((live.Average.TotalMilliseconds * live.Count) - (startLive.Average.TotalMilliseconds * startLive.Count)) / measured;
        report.Add(new Metric("live.total.average", "ms", average));
        report.Add(new Metric("live.total.min", "ms", live.Min.TotalMilliseconds));
        report.Add(new Metric("live.total.max", "ms", live.Max.TotalMilliseconds));
        report.Add(new Metric("live.added.average", "ms", average - outputLatency - period));
        report.Add(new Metric("live.added.max", "ms", live.Max.TotalMilliseconds - outputLatency - period, Target: Targets.LiveAddedMs));
        report.Add(new Metric("live.depth1-misses", "blocks", telemetry.LiveDeadlineMisses - startMisses, Target: 0));
        report.Add(new Metric("audio.xruns", "xruns", telemetry.Xruns, Target: 0));
        report.Notes.Add(keyInput == "midi"
            ? "The engine's meter keeps the count, average, shortest, and longest latency, from the key's MIDI timestamp (set by the sending process) to when its block is heard: Core MIDI's hop to the engine's input is included."
            : "The engine's meter keeps the count, average, shortest, and longest latency, from the key's timestamp (taken here) to when its block is heard: the forwarded path from this process, and any pause here, is included.");
        report.Notes.Add(AddedNote);
        gc.AddTo(report);
        options.Finish(report);
    }

    /// <summary>Keys into the monitored reference sine, heard through a looped-back device (BlackHole) and captured here.</summary>
    public static void Loopback(ProbeOptions options)
    {
        if (!AudioDevices.IsAvailable(out var problem))
        {
            Console.WriteLine(problem);
            return;
        }

        var buffer = options.Int("--buffer", 128);
        var wanted = ProbeOptions.Option(options.Args, "--device") ?? "BlackHole 2ch";
        var outputs = AudioDevices.List();
        var inputs = AudioDevices.ListInputs();
        var output = outputs.FirstOrDefault(d => d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        var inputName = ProbeOptions.Option(options.Args, "--input-device") ?? wanted;
        var input = inputs.FirstOrDefault(d => d.Name.Equals(inputName, StringComparison.OrdinalIgnoreCase));
        if (output is null || input is null)
        {
            Console.WriteLine($"Skipped: the loopback needs an output and an input called '{wanted}' (BlackHole 2ch, say); found outputs {string.Join(", ", outputs.Select(d => d.Name))}.");
            return;
        }

        var (path, build) = ResolveEngine(options);
        var settings = Settings(options, path, build);
        settings["device"] = output.Name;
        settings["input-device"] = input.Name;
        settings["buffer"] = buffer.ToString(CultureInfo.InvariantCulture);
        settings["queue"] = "0 (direct)";
        var keyInput = LiveInput(options);
        settings["input"] = keyInput == "midi" ? "MIDI from another process through a Core MIDI source the engine opens (as a keyboard)" : "forwarded from this process (as the computer keyboard)";
        var report = new TimingReport("engine-audio-loopback", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"Engine audio loopback ({build}): out {output.Name}, in {input.Name}; {buffer} frames, direct; pressure {settings["pressure"]}; {options.Seconds} s");

        using var rig = Rig.Create(path, testAudio: false);
        using var keys = KeySource.Create(keyInput);
        using var project = AudioProject.Create(rig, clips: 0, notes: false, keys.Providers);
        rig.Start();
        if (!project.Open(output.Id, buffer, report) || !project.Monitor() || !keys.Connect(project))
        {
            Console.WriteLine("The reference sine is not played at depth 1, or the keys do not reach it.");
            return;
        }

        var onsets = new ConcurrentQueue<long>();
        var above = false;
        using var capture = AudioInput.Open(
            (ReadOnlySpan<float> samples, int frames, int channels, in AudioCaptureBlock block) =>
            {
                for (var i = 0; i < frames; i++)
                {
                    var loud = Math.Abs(samples[i * channels]) >= 0.02f;
                    if (loud && !above)
                    {
                        onsets.Enqueue(block.CapturedTimestamp + (i * Stopwatch.Frequency / block.SampleRate));
                    }

                    above = loud;
                }
            },
            input.Id,
            SampleRate,
            buffer);
        capture.Start();
        Thread.Sleep(300);

        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 200);
        using var gc = new EngineGcWatch(rig.Supervisor);
        var played = keys.Play(project, options.Seconds);
        gc.Stop();
        Thread.Sleep(300);
        capture.Stop();
        StopPressure(stop, threads);

        // Each key with the first onset captured after it and before the next key (keys are 100 ms or more apart).
        var captured = onsets.ToArray();
        var totals = new List<double>();
        var unmatched = 0;
        for (var k = 0; k < played.Count; k++)
        {
            var next = k + 1 < played.Count ? played[k + 1] : long.MaxValue;
            var onset = captured.FirstOrDefault(c => c > played[k] && c < next);
            if (onset == 0)
            {
                unmatched++;
            }
            else
            {
                totals.Add((onset - played[k]) * 1000.0 / Stopwatch.Frequency);
            }
        }

        var telemetry = project.Telemetry();
        var period = (double)buffer / SampleRate * 1000;
        var outputLatency = telemetry.OutputLatency.TotalMilliseconds;
        var inputLatency = capture.Status.InputLatency.TotalMilliseconds;
        report.Add(new Metric("audio.period", "ms", period));
        report.Add(new Metric("audio.output-latency", "ms", outputLatency));
        report.Add(new Metric("audio.input-latency", "ms", inputLatency));
        report.Add(new Metric("loopback.keys", "keys", played.Count));
        report.Add(new Metric("loopback.unmatched", "keys", unmatched, Target: 0));
        report.Add(new Metric("loopback.total", "ms", Distribution: Distribution.Of(totals)));
        report.Add(new Metric("loopback.added", "ms", Distribution: Distribution.Of(totals.Select(t => t - outputLatency - period)), Target: Targets.LiveAddedMs));
        report.Notes.Add(AddedNote);
        report.Notes.Add("total is from the key's timestamp (taken where it was sent) to when the onset entered the loopback input (the input's reported latency is already taken off the capture time); added also takes off the device's reported output latency and one buffer period, so it is negative when a key waits less than a whole period for its callback. The onset is the sine's first sample above 0.02, a few samples into its attack.");
        gc.AddTo(report);
        options.Finish(report);
    }

    // What the added figures mean, for every live report.
    private const string AddedNote =
        "added = key-to-heard less the device's reported output latency and one buffer period (ADR 0043's definition). A key arrives at a random moment and waits for the next callback, between none and a whole period, half on average, so a key that adds nothing comes out between minus one period and zero, about minus half a period on average (-1.3 ms at 128 frames at 48 kHz). The target judges the largest: what Bluestone adds beyond the device and the buffer.";

    // --live-input: MIDI from another process through a Core MIDI source (default on macOS), or forwarded from this one.
    private static string LiveInput(ProbeOptions options)
    {
        var input = ProbeOptions.Option(options.Args, "--live-input") ?? (OperatingSystem.IsMacOS() ? "midi" : "forwarded");
        return input switch
        {
            "midi" when OperatingSystem.IsMacOS() => "midi",
            "midi" => throw new ArgumentException("--live-input midi needs Core MIDI (macOS); use --live-input forwarded."),
            "forwarded" => "forwarded",
            _ => throw new ArgumentException("--live-input is midi or forwarded."),
        };
    }

    /// <summary>
    /// The engine to measure: <c>--engine-path</c>, else the NativeAOT build in <c>src/dotnet/artifacts/engine-aot</c>,
    /// else the JIT build beside the probes (with a printed note).
    /// </summary>
    internal static (string Path, string Build) ResolveEngine(ProbeOptions options)
    {
        var name = OperatingSystem.IsWindows() ? "Bluestone.Engine.exe" : "Bluestone.Engine";
        if (ProbeOptions.Option(options.Args, "--engine-path") is { } given)
        {
            var full = Path.GetFullPath(given);
            if (Directory.Exists(full))
            {
                full = Path.Combine(full, name);
            }

            return File.Exists(full)
                ? (full, full.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(Path.GetDirectoryName(full)!, "Bluestone.Engine.dll")) ? "JIT" : "NativeAOT")
                : throw new ArgumentException($"--engine-path: there is no engine at {full}.");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var published = Path.Combine(directory.FullName, "src", "dotnet", "artifacts", "engine-aot", name);
            if (File.Exists(published))
            {
                return (published, "NativeAOT");
            }
        }

        var beside = Path.Combine(AppContext.BaseDirectory, name);
        Console.WriteLine("NOTE: no NativeAOT engine found (--engine-path, or src/dotnet/artifacts/engine-aot); measuring the JIT build beside the probes, which is not the shipping configuration.");
        return (beside, "JIT");
    }

    private static Dictionary<string, string> Settings(ProbeOptions options, string path, string build)
    {
        if (options.Isolate)
        {
            throw new ArgumentException("--isolate does not apply with --engine: the engine is the separate process.");
        }

        var settings = options.BaseSettings();
        settings["isolation"] = "engine process";
        settings["engine"] = build;
        settings["engine-path"] = path;
        settings["pressure"] = $"{options.Pressure} (in the probe, as Bluestone)";
        return settings;
    }

    private static void StopPressure(CancellationTokenSource stop, List<Thread> threads)
    {
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }
    }

    // Keys on the monitored track at random moments 100–250 ms apart, each released after 40 ms; their timestamps.
    private static List<long> Keys(AudioProject project, int seconds)
    {
        var random = new Random(8);
        var times = new List<long>();
        var channel = MidiChannel.FromIndex(0);
        var until = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            var note = new NoteNumber(60 + random.Next(12));
            var now = Stopwatch.GetTimestamp();
            project.Controller.Live.Post(project.Synth, ChannelMessage.NoteOn(channel, note, new Velocity(127)), Stopwatch.GetElapsedTime(0, now));
            times.Add(now);
            Thread.Sleep(40);
            project.Controller.Live.Post(project.Synth, ChannelMessage.NoteOff(channel, note, Velocity.DefaultRelease), SystemMonotonicClock.Instance.Now);
            Thread.Sleep(60 + random.Next(150));
        }

        return times;
    }

    /// <summary>
    /// Where the keys of the live probes come from: forwarded from this process (the computer keyboard's path), or a
    /// child process's virtual Core MIDI source that the engine opens as an input (a MIDI keyboard's path).
    /// </summary>
    private sealed class KeySource : IDisposable
    {
        private readonly Process? _process;
        private readonly IMidiEndpointProvider? _provider;
        private EndpointDescriptor? _source;

        private KeySource(Process? process, IMidiEndpointProvider? provider)
        {
            _process = process;
            _provider = provider;
        }

        /// <summary>Endpoint providers the project lists (Core MIDI, for the source).</summary>
        public IMidiEndpointProvider[] Providers => _provider is null ? [] : [_provider];

        public static KeySource Create(string input)
        {
            if (input != "midi" || !OperatingSystem.IsMacOS())
            {
                return new KeySource(null, null);
            }

            var process = ProbeOptions.StartSelf(["--midi-keys"], redirect: true);
            if (process.StandardOutput.ReadLine() != "ready")
            {
                throw new InvalidOperationException("The MIDI key process did not start.");
            }

            return new KeySource(process, new Platform.CoreMidi.CoreMidiProvider(SystemMonotonicClock.Instance, "Bluestone engine probe keys"));
        }

        /// <summary>Makes the engine open the source and play it on the thru track; checks that keys are heard.</summary>
        public bool Connect(AudioProject project)
        {
            if (_provider is null)
            {
                return true;
            }

            for (var attempt = 0; attempt < 50 && _source is null; attempt++)
            {
                _source = _provider.GetEndpoints().FirstOrDefault(e => e.DisplayName == MidiKeysSource && e.Direction == EndpointDirection.Input);
                Thread.Sleep(_source is null ? 100 : 0);
            }

            if (_source is not { } source)
            {
                Console.WriteLine($"Core MIDI source '{MidiKeysSource}' did not appear.");
                return false;
            }

            project.Controller.SelectInputs([source.Id]);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                project.Controller.RefreshAsync().GetAwaiter().GetResult();
                var before = project.Audio.LiveLatency.Count;
                Play(project, 1);
                Thread.Sleep(300);
                if (project.Audio.LiveLatency.Count > before)
                {
                    return true;
                }
            }

            Console.WriteLine("Keys from the Core MIDI source did not reach the instrument in the engine.");
            return false;
        }

        /// <summary>Plays keys for <paramref name="seconds"/>; returns when each went down, on the Stopwatch clock.</summary>
        public List<long> Play(AudioProject project, int seconds)
        {
            if (_process is not { } process)
            {
                return Keys(project, seconds);
            }

            process.StandardInput.WriteLine(string.Create(CultureInfo.InvariantCulture, $"play {seconds}"));
            process.StandardInput.Flush();
            var times = new List<long>();
            while (process.StandardOutput.ReadLine() is { } line && line != "done")
            {
                if (line.StartsWith("key ", StringComparison.Ordinal))
                {
                    times.Add(long.Parse(line[4..], CultureInfo.InvariantCulture));
                }
            }

            return times;
        }

        public void Dispose()
        {
            if (_process is { } process)
            {
                process.StandardInput.WriteLine("stop");
                process.StandardInput.Flush();
                process.WaitForExit(5000);
                process.Dispose();
            }

            _provider?.Dispose();
        }
    }

    /// <summary>The engine and Bluestone's MIDI client, as the app composes them with BLUESTONE_ENGINE=1.</summary>
    private sealed class Rig : IDisposable
    {
        private Rig(string path, bool testAudio)
        {
            Supervisor = new EngineSupervisor(new EngineSupervisorOptions { EnginePath = path, AllowTestCommands = testAudio, TestAudio = testAudio }, new ActivityLog());
            Playback = new EnginePlayback(Supervisor, new EnginePlaybackOptions { IsEngineEndpoint = e => e.Id.Provider is "coremidi" or "winmm" or "alsa" });
        }

        public EngineSupervisor Supervisor { get; }

        public EnginePlayback Playback { get; }

        /// <summary>The clients, attached; the engine starts with <see cref="Start"/>, after every client is attached.</summary>
        public static Rig Create(string path, bool testAudio) => new(path, testAudio);

        public void Start()
        {
            Supervisor.StartAsync().GetAwaiter().GetResult();
            if (Supervisor.State != EngineState.Ready)
            {
                throw new InvalidOperationException($"The engine did not start: {string.Join("; ", Supervisor.Stops.Select(s => s.Detail))}");
            }
        }

        public void Dispose()
        {
            Supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Playback.Dispose();
        }
    }

    /// <summary>A virtual Core MIDI destination in a child process for the engine to play to (macOS), or none.</summary>
    private sealed class Sink : IDisposable
    {
        private readonly Process? _process;
        private readonly IDisposable? _provider;

        private Sink(Process? process, IDisposable? provider, EndpointDescriptor? endpoint)
        {
            _process = process;
            _provider = provider;
            Endpoint = endpoint;
        }

        public EndpointDescriptor? Endpoint { get; }

        public static Sink Open(TimingReport report)
        {
            if (!OperatingSystem.IsMacOS())
            {
                report.Notes.Add("No Core MIDI here: the engine plays to an output forwarded to the probe.");
                return new Sink(null, null, null);
            }

            var file = Path.Combine(Path.GetFullPath(ProbeOptions.DefaultReportDirectory), $"engine-midi-sink-{Environment.ProcessId}.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var process = ProbeOptions.StartSelf(["--midi-sink", file], redirect: true);
            if (process.StandardOutput.ReadLine() != "ready")
            {
                throw new InvalidOperationException("The MIDI sink process did not start.");
            }

            var provider = new Platform.CoreMidi.CoreMidiProvider(SystemMonotonicClock.Instance, "Bluestone engine probe");
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (provider.GetEndpoints().FirstOrDefault(e => e.DisplayName == SinkName && e.Direction == EndpointDirection.Output) is { } found)
                {
                    return new Sink(process, provider, found);
                }

                Thread.Sleep(100);
            }

            throw new InvalidOperationException($"Core MIDI endpoint '{SinkName}' did not appear.");
        }

        public void Dispose()
        {
            if (_process is { } process)
            {
                process.StandardInput.WriteLine("stop");
                process.StandardInput.Flush();
                process.WaitForExit(5000);
                if (process.StartInfo.ArgumentList is [.., var file] && File.Exists(file))
                {
                    File.Delete(file);
                }

                process.Dispose();
            }

            _provider?.Dispose();
        }
    }

    /// <summary>
    /// A project played through the engine: clips on channels that go through a bus, and a track playing the reference
    /// sine in a real worker; the playback controller, plugin bridge, and audio client as the app composes them.
    /// </summary>
    private sealed class AudioProject : IDisposable
    {
        private readonly PluginHostManager _plugins;
        private readonly EndpointDirectory _endpoints;
        private readonly string _files;

        private AudioProject(Rig rig, string files, IMidiEndpointProvider[] providers)
        {
            _files = files;
            _plugins = new PluginHostManager(new PluginHostOptions { DataDirectory = Path.Combine(files, "plugins") });
            _endpoints = new EndpointDirectory(providers);
            Controller = new PlaybackController(Session, _endpoints, ProfileCatalog.Empty, rig.Playback);
            Devices = new PluginDeviceHost(_plugins, Session, [Sine], SampleRate, 256);
            Controller.UseDevices(Devices.Extend(DeviceCatalog.BuiltIn));
            Controller.Live.UsePlugins(Devices.InstanceOf);
            Audio = new EngineAudio(rig.Supervisor, Controller, Session, Devices.InstanceOf, rate => Devices.SetSampleRateAsync(rate), new EngineAudioOptions { GraphDirectory = Path.Combine(files, "graphs") });
            Rig = rig;
        }

        public Rig Rig { get; }

        public ProjectSession Session { get; } = new();

        public PlaybackController Controller { get; }

        public PluginDeviceHost Devices { get; }

        public EngineAudio Audio { get; }

        public TrackId Synth { get; private set; }

        public static AudioProject Create(Rig rig, int clips, bool notes, params IMidiEndpointProvider[] providers)
        {
            var files = Path.Combine(Path.GetFullPath(ProbeOptions.DefaultReportDirectory), $"engine-probe-{Environment.ProcessId}");
            Directory.CreateDirectory(files);
            var project = new AudioProject(rig, files, providers);
            project.Build(clips, notes);
            return project;
        }

        /// <summary>Opens the device in the engine, in direct mode, and waits for the graph to play; false (and a note) when it did not open.</summary>
        public bool Open(string? deviceId, int buffer, TimingReport report)
        {
            Devices.SyncAsync(Session.Project).GetAwaiter().GetResult();
            Controller.RefreshAsync().GetAwaiter().GetResult();
            Audio.Start(new AudioSettings(true, deviceId, SampleRate, buffer) { EngineQueuedFrames = 0 });
            if (!Wait(() => Audio.IsRunning, TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine($"The engine did not open the device: {Audio.Problem}");
                return false;
            }

            // Plugins follow the stream's rate; the graph is described for it once they run.
            if (!Wait(() => Devices.InstanceOf(Session.Project.ChainOf(Synth)!.Devices[0].Id) is { Status.State: PluginInstanceState.Running } instance
                    && (int)instance.Request.SampleRate == Audio.Stream?.SampleRate, TimeSpan.FromSeconds(20)))
            {
                Console.WriteLine("The reference sine did not start at the device's rate.");
                return false;
            }

            Audio.Rebuild();
            if (!Wait(() => Audio.PlayingGraph >= Audio.DescribedGraph && Audio.GraphProblems.IsEmpty, TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine($"The engine did not play the graph: {string.Join("; ", Audio.GraphProblems)}");
                return false;
            }

            report.Settings["device-rate"] = Audio.Stream!.SampleRate.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>Makes the synth track the thru track: the engine plays its instrument at depth 1. Whether it did.</summary>
        public bool Monitor()
        {
            Controller.SetThruTrack(Synth);
            Audio.Rebuild();
            return Wait(() => Audio.PlayingGraph >= Audio.DescribedGraph && Audio.Stream?.LatencyFrames == 0, TimeSpan.FromSeconds(10));
        }

        public AudioTelemetry Telemetry() => Wait(() => Audio.Telemetry is not null, TimeSpan.FromSeconds(5)) ? Audio.Telemetry!.Value : default;

        public void Dispose()
        {
            Audio.Dispose();
            Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Devices.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _plugins.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _endpoints.Dispose();
            try
            {
                Directory.Delete(_files, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static bool Wait(Func<bool> condition, TimeSpan timeout)
        {
            var until = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (!condition())
            {
                if (Stopwatch.GetTimestamp() > until)
                {
                    return false;
                }

                Thread.Sleep(20);
            }

            return true;
        }

        private void Build(int clips, bool notes)
        {
            var ppqn = new Ppqn(480);
            var tracks = new List<Track>();
            var connections = new List<SignalConnection>();
            var bus = MixerChannel.Create("Bus") with { GainDecibels = -6 };
            // Clips play quietly; a live-only project stays loud enough for the loopback to find each key's onset.
            var mixer = Mixer.Empty.With(bus) with { MasterGainDecibels = clips > 0 ? -24 : 0 };
            for (var i = 0; i < clips; i++)
            {
                var file = Path.Combine(_files, $"clip-{i}.wav");
                using (var writer = new WaveFileWriter(file, SampleRate, 2))
                {
                    var samples = new float[SampleRate * 2 * 2];
                    for (var f = 0; f < SampleRate * 2; f++)
                    {
                        samples[2 * f] = samples[(2 * f) + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 220 * (i + 1) * f / SampleRate));
                    }

                    writer.Write(samples);
                }

                var clip = new AudioClip(ClipId.New(), Tick.Zero, new TickSpan(480 * 2 * 8), TickSpan.Zero, new AudioSource(file, SampleRate * 2, SampleRate, 2))
                {
                    Loop = new ClipLoop(Tick.Zero, new TickSpan(480 * 4)),
                };
                var track = Track.Create($"Clip {i + 1}", TrackRole.Audio).WithClip(clip);
                var channel = MixerChannel.Create($"Clip {i + 1}") with { Output = bus.Id, Pan = (i % 3) - 1 };
                mixer = mixer.With(channel);
                tracks.Add(track);
                connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Mixer(channel.Id)));
            }

            // 120 BPM, 960 ticks a second: an eighth note every quarter second while the loop plays.
            var events = notes
                ? Enumerable.Range(0, 32).Select(n => (TrackEvent)new NoteEvent(new Tick(n * 240), new TickSpan(200), MidiChannel.FromIndex(0), new NoteNumber(57 + (n % 12)), new Velocity(100))).ToArray()
                : [];
            var synth = Track.FromEvents(TrackId.New(), "Synth", events);
            var synthChannel = MixerChannel.Create("Synth") with { Output = bus.Id };
            mixer = mixer.With(synthChannel);
            tracks.Add(synth);
            connections.Add(SignalConnection.Create(SignalKind.Audio, SignalNode.Track(synth.Id), SignalNode.Mixer(synthChannel.Id)));
            Synth = synth.Id;
            var project = Session.Project with
            {
                Sequence = new Sequence(TempoMap.Constant(ppqn, Tempo.Default), MeterMap.Constant(ppqn, TimeSignature.CommonTime), [.. tracks], []),
                Mixer = mixer,
                Connections = [.. connections],
            };
            project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(synth.Id)) with { Devices = [DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Sine).ToReference())] });
            Session.Execute(new ProjectCommand("Probe project", _ => project));
        }
    }

    /// <summary>The engine's collections, pauses, and allocations over a measurement, from its heartbeats.</summary>
    private sealed class EngineGcWatch : IDisposable
    {
        private readonly EngineSupervisor _supervisor;

        // This process, which plays Bluestone's role: a pause here delays what it forwards to the engine.
        private readonly GcWatch _probe = new();
        private readonly Lock _gate = new();
        private readonly List<double> _pauses = [];
        private readonly long _started = Stopwatch.GetTimestamp();
        private EngineTelemetry? _first;
        private EngineTelemetry? _last;
        private bool _stopped;

        public EngineGcWatch(EngineSupervisor supervisor)
        {
            _supervisor = supervisor;
            _first = supervisor.Telemetry;
            supervisor.TelemetryReceived += OnTelemetry;
        }

        public void Stop()
        {
            lock (_gate)
            {
                _stopped = true;
            }
        }

        public void AddTo(TimingReport report)
        {
            EngineTelemetry first, last;
            List<double> pauses;
            lock (_gate)
            {
                first = _first ?? default;
                last = _last ?? first;
                pauses = [.. _pauses];
            }

            var elapsed = Math.Max(1e-3, Stopwatch.GetElapsedTime(_started).TotalSeconds);
            report.Add(new Metric("engine.gc.collections", "collections", last.Collections - first.Collections));
            report.Add(new Metric("engine.gc.pause-total", "ms", (last.PauseTotal - first.PauseTotal).TotalMilliseconds));
            report.Add(new Metric("engine.gc.pause", "ms", Distribution: Distribution.Of(pauses), Target: 1.0));
            report.Add(new Metric("engine.allocated", "KB/s", (last.AllocatedBytes - first.AllocatedBytes) / 1024.0 / elapsed));
            report.Add(new Metric("engine.working-set", "MB", last.WorkingSetBytes / 1048576.0));
            report.Add(new Metric("engine.jit-methods", "methods", last.JitCompiledMethods));
            report.Notes.Add("engine.gc.pause has one entry per heartbeat (every 250 ms) that saw a collection: the latest collection's pause; pause-total counts every collection. "
                + "engine.allocated is mostly the engine's sender thread (status, meters, and MIDI messages for Bluestone; ADR 0044), not its real-time threads.");
            if (last.JitCompiledMethods > 0)
            {
                report.Notes.Add($"The engine compiled {last.JitCompiledMethods} methods at run time: it is not the NativeAOT build.");
            }

            _probe.AddTo(report, "probe");
            report.Notes.Add("probe.gc.* are this process's collections and pauses (it plays Bluestone, and makes the --pressure load): a pause here delays what it forwards to the engine (computer-keyboard keys, commands), not what the engine does on its own.");
        }

        public void Dispose()
        {
            _supervisor.TelemetryReceived -= OnTelemetry;
            _probe.Dispose();
        }

        private void OnTelemetry(object? sender, EngineTelemetry telemetry)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _first ??= telemetry;
                _last = telemetry;
                if (telemetry.LastPause > TimeSpan.Zero)
                {
                    _pauses.Add(telemetry.LastPause.TotalMilliseconds);
                }
            }
        }
    }
}
