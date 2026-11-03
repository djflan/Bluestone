using System.Diagnostics;
using System.Globalization;
using Bluestone.Audio;
using Bluestone.Audio.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Signal;

namespace Bluestone.Engine;

/// <summary>
/// ADR 0044, migration step 2: the engine's code published with NativeAOT. A smoke test of each real-time area, with
/// timings, so the spike shows that the code runs compiled ahead of time and how quickly the process is ready. It plays
/// nothing audible: MIDI goes to a counting output, audio is rendered offline, and devices are only listed.
/// Run: <c>Bluestone.Engine --spike</c>.
/// </summary>
internal static class AotSpike
{
    public static int Run()
    {
        var sinceStart = Stopwatch.StartNew();
        var processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        Report("main entered", (DateTime.UtcNow - processStarted).TotalMilliseconds);

        // Playback: compile a dense plan and play it on the real playback thread for one second.
        var resolution = new Ppqn(960);
        var random = new Random(1234);
        var sequence = Sequence.CreateEmpty(resolution);
        for (var t = 0; t < 16; t++)
        {
            var channel = MidiChannel.FromIndex(t);
            var events = Enumerable.Range(0, 2_000).Select(i => (TrackEvent)new NoteEvent(
                EventId.New(), new Tick(i * 240L), new TickSpan(230), channel, new NoteNumber(36 + random.Next(48)), new Velocity(40 + random.Next(87)), Velocity.DefaultRelease));
            sequence = sequence.WithTrack(Track.FromEvents(TrackId.New(), $"Track {t + 1}", events));
        }

        var parts = sequence.Tracks.Select(track => new PlanPart(0, [.. track.ArrangedEvents.Select((e, i) => new SignalEvent(e, 0, i))])).ToList();
        var plan = PlaybackPlanCompiler.Compile(sequence, parts);
        Report("plan compiled (32,000 notes)", sinceStart.Elapsed.TotalMilliseconds);

        var output = new CountingOutput();
        using (var engine = new PlaybackEngine(SystemMonotonicClock.Instance, sequence.TempoMap))
        {
            engine.SetOutputs([output]);
            engine.Load(plan);
            using (new PlaybackThread(engine))
            {
                engine.Play(Tick.Zero);
                Thread.Sleep(1000);
                engine.Stop();
                Thread.Sleep(50);
            }

            var stats = engine.Statistics.Snapshot();
            Report($"playback: {stats.Dispatched} messages in 1 s, max lateness {stats.MaxLateness.TotalMilliseconds:0.000} ms (default thread, spin)", sinceStart.Elapsed.TotalMilliseconds);
        }

        // Audio: render the probes' graph offline, 32 tone strips into 4 buses, for one second at 48 kHz.
        using (var audio = new AudioEngine(48_000, 128))
        {
            var builder = new AudioGraphBuilder();
            var buses = Enumerable.Range(0, 4).Select(_ => builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -3))).ToArray();
            for (var i = 0; i < 32; i++)
            {
                var strip = builder.AddStrip(new MixerStripState(), new MixerStripSettings(GainDecibels: -12));
                builder.RouteStrip(strip, buses[i % buses.Length]);
                builder.ConnectToStrip(builder.AddNode(new ToneNode(110 * (i + 1), 0.1)), strip);
            }

            audio.Publish(builder.Build(128, new MixerStripState(), masterGainDecibels: 0));
            var driver = new OfflineAudioDriver(audio, serviceStreams: false);
            var rendered = Stopwatch.StartNew();
            var samples = driver.Render(48_000, 128);
            Report($"audio: rendered 1 s offline in {rendered.Elapsed.TotalMilliseconds:0.0} ms, peak {samples.Max(Math.Abs):0.000}", sinceStart.Elapsed.TotalMilliseconds);

            // Steady state: the JIT has tiered up by now (it needs about 30 calls and a background compile), so this compares
            // optimized JIT code with ahead-of-time code. Ten seconds of audio, reported per 128-frame block.
            driver.Render(48_000 * 5, 128);
            rendered.Restart();
            driver.Render(48_000 * 10, 128);
            Report($"audio: steady state {rendered.Elapsed.TotalMilliseconds * 1000 / (48_000 * 10 / 128):0.0} µs per 128-frame block", sinceStart.Elapsed.TotalMilliseconds);
        }

        // Devices: list audio outputs through the Rust layer, and MIDI endpoints through the platform adapter.
        var devices = AudioDevices.IsAvailable(out var problem) ? AudioDevices.List() : [];
        Report($"audio devices: {(problem is null ? string.Join(", ", devices.Select(d => d.Name)) : problem)}", sinceStart.Elapsed.TotalMilliseconds);

        if (OperatingSystem.IsMacOS())
        {
            using var coreMidi = new Bluestone.Platform.CoreMidi.CoreMidiProvider(SystemMonotonicClock.Instance, "Bluestone engine spike");
            coreMidi.CreateVirtualOutput("Bluestone Engine Spike");
            var endpoints = coreMidi.GetEndpoints();
            Report($"Core MIDI: {endpoints.Count} endpoints, including {string.Join(", ", endpoints.Take(4).Select(e => e.DisplayName))}", sinceStart.Elapsed.TotalMilliseconds);
        }

        Report($"GC: {GC.CollectionCount(0)} gen0, {GC.CollectionCount(1)} gen1, {GC.CollectionCount(2)} gen2; heap {GC.GetTotalMemory(false) / 1048576.0:0.0} MB; JIT compiled {System.Runtime.JitInfo.GetCompiledMethodCount()} methods", sinceStart.Elapsed.TotalMilliseconds);
        return 0;
    }

    private static void Report(string what, double ms) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{ms,9:0.0} ms  {what}"));
}

/// <summary>Accepts every message and only counts.</summary>
internal sealed class CountingOutput : IMidiOutput
{
    public EndpointDescriptor Endpoint { get; } = new(new EndpointId("engine-spike", "sink"), "Sink", EndpointDirection.Output, EndpointTransport.Test, EndpointCapabilities.None);

    public EndpointState State => EndpointState.Open;

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp) => SendResult.Sent;

    public void Dispose()
    {
    }
}
