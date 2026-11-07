using System.Diagnostics;
using Bluestone.Audio;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Devices;
using Bluestone.Audio.Files;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Engine.Protocol;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Engine;

/// <summary>
/// <c>--check-allocations</c>: plays notes from an input through thru on a live route (a transpose and a forced channel)
/// to a forwarded output while the transport plays, on one thread, as the engine's input callback, live thread, and
/// playback thread would, and checks that the thread allocates nothing once warmed up (ADR 0044's rule for real-time
/// threads). Then renders audio as the device callback does (step 6): a looping clip streamed from disk and a plugin
/// whose worker is not running, into a strip, following the playing transport, while another thread reads, realizes, and
/// swaps in a new graph; and checks that the rendering thread allocates nothing. Last (step 7), live notes go into a
/// plugin instrument at depth 1, from the input path into the device callback, through a block exchange that a stand-in
/// worker thread answers. Prints the bytes allocated by each; exits 0 when there were none, the notes came out
/// transposed, the clip was heard, and the live notes were heard and measured.
/// </summary>
internal static class AllocationCheck
{
    private const int Rounds = 1000;
    private const int Rate = 48_000;
    private const int Block = 256;
    private const int Callbacks = 1500;

    public static int Run()
    {
        var midi = RunMidi();
        var audio = RunAudio();
        var live = RunLive();
        return midi && audio && live ? 0 : 1;
    }

    private static bool RunMidi()
    {
        using var midi = new MidiEngine(restarted: false, startThreads: false);
        var track = Guid.NewGuid();
        Apply(midi, new OpenOutput(1, "test", "out", "Out", Forwarded: true, Scheduled: false));
        Apply(midi, new SetSlots([1]));
        Apply(midi, new SetRoute(1, track, 1, 960 * 34, [
            new RouteStageSpec(BuiltInDevices.Transpose.Id.Value, -1, [new RouteParameter(BuiltInDevices.TransposeSemitones.Value, BuiltInDevices.Transpose.Parameters[0].ToStored(12).Value)]),
            new RouteStageSpec(string.Empty, 3, []),
        ]));
        Apply(midi, new SetThru(track));
        Apply(midi, new TransportPlay(0, null, 1));
        var input = midi.AddTestInput(2, "test", "Keys");

        Play(midi, input, Rounds * 4);
        while (midi.Outbound.Drain() is not null)
        {
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        Play(midi, input, Rounds);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var transposed = 0;
        var inputs = 0;
        while (midi.Outbound.Drain() is { } batch)
        {
            foreach (var e in batch.Events)
            {
                if (e.Kind == MidiEventKind.Output && e.Bytes.Span is [0x93, 72, 100])
                {
                    transposed++;
                }
                else if (e.Kind == MidiEventKind.Input)
                {
                    inputs++;
                }
            }
        }

        Console.WriteLine($"MIDI: allocated {allocated} bytes; {transposed} notes thru, {inputs} input messages forwarded");
        return allocated == 0 && transposed == Rounds && inputs == Rounds * 2;
    }

    private static bool RunAudio()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"bluestone-engine-check-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        try
        {
            var tone = Path.Combine(folder, "tone.wav");
            using (var writer = new WaveFileWriter(tone, Rate, 2))
            {
                var samples = new float[Rate * 2 * 2];
                for (var i = 0; i < Rate * 2; i++)
                {
                    samples[2 * i] = samples[(2 * i) + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * i / Rate));
                }

                writer.Write(samples);
            }

            using var midi = new MidiEngine(restarted: false, startThreads: false);
            Apply(midi, new TransportPlay(0, null, 1));
            midi.Playback.Pump();

            // Restarted, so the plugin's first block carries the sweep; started without its thread, so graphs are realized
            // where this check says.
            using var host = new AudioHost(midi.Playback, new TestAudioBackend(), restarted: true, test: false, startThread: false);
            host.Apply(new SetAudio(1, true, null, Rate, Block, 0, Block, false));
            host.Apply(new WatchJunctions([JunctionInput, JunctionAfterSynth]));
            host.Apply(new LoadAudioGraph(1, Graph(folder, 1, tone, 0), Rate, Block));
            host.RunPending();
            var engine = host.Engine ?? throw new InvalidOperationException("The test device did not open.");

            // Another thread reads, realizes, and swaps in the next graph while the callbacks run.
            using var swap = new ManualResetEventSlim();
            var swapper = new Thread(() =>
            {
                swap.Wait();
                host.Apply(new LoadAudioGraph(2, Graph(folder, 2, tone, -6), Rate, Block));
                host.RunPending();
            });
            swapper.Start();

            var buffer = new float[Block * 2];
            var frame = 0L;
            var heard = 0f;
            Render(Callbacks);
            var first = engine.Graph;
            var before = GC.GetAllocatedBytesForCurrentThread();
            Render(Callbacks / 2);
            swap.Set();
            Render(Callbacks / 2);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            swapper.Join();

            var swapped = !ReferenceEquals(engine.Graph, first) && engine.Graph.Nodes.OfType<ClipPlayerNode>().Any();
            Console.WriteLine($"audio: allocated {allocated} bytes in {Callbacks} callbacks; peak {heard:0.###}, faults {engine.Statistics.Faults}, graph swapped {swapped}");
            return allocated == 0 && heard > 0.05f && engine.Statistics.Faults == 0 && swapped;

            void Render(int callbacks)
            {
                for (var i = 0; i < callbacks; i++)
                {
                    midi.Playback.Pump();
                    buffer.AsSpan().Clear();
                    engine.Render(buffer, Block, 2, new AudioDeviceBlock(frame, Stopwatch.GetTimestamp(), TimeSpan.Zero, Rate, false));
                    frame += Block;
                    foreach (var sample in buffer)
                    {
                        heard = Math.Max(heard, Math.Abs(sample));
                    }

                    // About as often as a device asks, so the disk streamer keeps up.
                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static bool RunLive()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"bluestone-engine-check-live-{Environment.ProcessId}");
        Directory.CreateDirectory(folder);
        var shape = new ExchangeOptions(Block, 0, 2);
        var exchange = Path.Combine(folder, "synth.bsx");
        using var worker = WorkerBlockExchange.Create(exchange, shape, 1);
        using var stop = new CancellationTokenSource();
        var answering = new Thread(() => Answer(worker, stop.Token)) { IsBackground = true, Name = "Stand-in plugin worker" };
        answering.Start();
        try
        {
            var plugins = new PluginRegistry(restarted: false);
            var meter = new LiveMeter();
            using var midi = new MidiEngine(restarted: false, startThreads: false, plugins, meter);
            using var host = new AudioHost(midi.Playback, new TestAudioBackend(), restarted: false, test: false, startThread: false, plugins, meter)
            {
                InstrumentsChanged = midi.UseInstruments,
            };
            var synth = new Guid(4, 0, 0, [0, 0, 0, 0, 0, 0, 0, 4]);
            var track = new Guid(5, 0, 0, [0, 0, 0, 0, 0, 0, 0, 5]);
            var builder = new AudioGraphDescriptionBuilder();
            var node = builder.AddNode(new DescribedNode(DescribedNodeKind.Plugin, synth, "Synth")
            {
                Link = new DescribedPluginLink(shape, 1, TimeSpan.FromMilliseconds(2), false, 0, new PluginDataPlane(exchange, 1, shape)),
            });
            builder.ConnectToMaster(node);
            var graph = Path.Combine(folder, "graph.bsgr");
            File.WriteAllBytes(graph, AudioGraphImage.ToArray(builder.Build(0, [synth])));
            host.Apply(new SetAudio(1, true, null, Rate, Block, 0, Block, false));
            host.Apply(new LoadAudioGraph(1, graph, Rate, Block));
            host.RunPending();
            Apply(midi, new SetRoute(1, track, -1, 960 * 34, [], synth));
            var engine = host.Engine ?? throw new InvalidOperationException("The test device did not open.");

            var buffer = new float[Block * 2];
            var frame = 0L;
            var heard = 0;
            Render(Callbacks);
            var before = GC.GetAllocatedBytesForCurrentThread();
            Render(Callbacks);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var live = meter.Read(plugins.DeadlineMisses);
            Console.WriteLine($"live: allocated {allocated} bytes in {Callbacks} callbacks; {heard} blocks heard, {live.Notes} notes measured, {live.DeadlineMisses} depth-1 deadlines missed");
            plugins.Dispose();
            return allocated == 0 && heard > 0 && live.Notes > 0;

            void Render(int callbacks)
            {
                for (var i = 0; i < callbacks; i++)
                {
                    // A key goes down every 40 callbacks and up 20 later, as from an input callback.
                    if (i % 40 is 0 or 20)
                    {
                        midi.PostLive(track, i % 40 == 0 ? (byte)0x90 : (byte)0x80, 60, 100, Midi.Timing.SystemMonotonicClock.Instance.Now);
                    }

                    buffer.AsSpan().Clear();
                    engine.Render(buffer, Block, 2, new AudioDeviceBlock(frame, Stopwatch.GetTimestamp(), TimeSpan.Zero, Rate, false));
                    frame += Block;
                    if (buffer[0] > 0.4f)
                    {
                        heard++;
                    }

                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            stop.Cancel();
            answering.Join();
            Directory.Delete(folder, recursive: true);
        }
    }

    // A worker that answers every block at once: 0.5 on both channels while a note it was given sounds.
    private static void Answer(WorkerBlockExchange exchange, CancellationToken stop)
    {
        var sounding = false;
        var e = default(PluginEvent);
        while (!stop.IsCancellationRequested)
        {
            if (!exchange.TryBeginBlock(out var block))
            {
                Thread.SpinWait(50);
                continue;
            }

            for (var i = 0; i < block.InputEventCount; i++)
            {
                if (block.TryReadInputEvent(i, ref e) && e.Kind is PluginEventKind.NoteOn or PluginEventKind.NoteOff)
                {
                    sounding = e.Kind == PluginEventKind.NoteOn && e.Data2 > 0;
                }
            }

            block.Output(0)[..block.Frames].Fill(sounding ? 0.5f : 0f);
            block.Output(1)[..block.Frames].Fill(sounding ? 0.5f : 0f);
            exchange.CompleteBlock(ref block, BlockStatus.Ok);
        }
    }

    private static readonly Guid JunctionInput = new(4, 0, 0, [0, 0, 0, 0, 0, 0, 0, 4]);
    private static readonly Guid JunctionAfterSynth = new(5, 0, 0, [0, 0, 0, 0, 0, 0, 0, 5]);

    // A strip fed by a looping clip and by a plugin instrument whose worker is not running (its failure policy plays),
    // with both of the chain's junctions metered.
    private static string Graph(string folder, int id, string tone, double gain)
    {
        var builder = new AudioGraphDescriptionBuilder();
        var strip = builder.AddStrip(new DescribedStrip(new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), new MixerStripSettings(gain)));
        var clips = builder.AddNode(new DescribedNode(DescribedNodeKind.Clips, new Guid(2, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]), "Tone")
        {
            Regions = [new AudioClipRegion(tone, 0, long.MaxValue / 4, 0) { LoopStartSeconds = 0, LoopLengthSeconds = 1 }],
        });
        var synth = builder.AddNode(new DescribedNode(DescribedNodeKind.Plugin, new Guid(3, 0, 0, [0, 0, 0, 0, 0, 0, 0, 3]), "Synth")
        {
            Schedule = new PluginSchedule([new(0, PluginEvent.NoteOn(0, 0, 60, 100)), new(Rate / 2, PluginEvent.NoteOff(0, 0, 60, 64))], []),
            Link = new DescribedPluginLink(new ExchangeOptions(Block, 0, 2), 2, TimeSpan.FromMilliseconds(2), false, Block, null),
        });
        builder.ConnectToStrip(clips, strip);
        builder.ConnectToStrip(synth, strip);
        builder.AddMeterPoint(JunctionInput, [clips]);
        builder.AddMeterPoint(JunctionAfterSynth, [clips, synth]);
        var path = Path.Combine(folder, $"graph-{id}.bsgr");
        File.WriteAllBytes(path, AudioGraphImage.ToArray(builder.Build(0, [new Guid(2, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]), new Guid(3, 0, 0, [0, 0, 0, 0, 0, 0, 0, 3])])));
        return path;
    }

    private static void Apply(MidiEngine midi, EngineMessage message)
    {
        if (midi.Apply(message) is [var problem, ..])
        {
            throw new InvalidOperationException(problem.Message);
        }
    }

    private static void Play(MidiEngine midi, MidiEngine.InputEntry input, int rounds)
    {
        ReadOnlySpan<byte> on = [0x90, 60, 100];
        ReadOnlySpan<byte> off = [0x80, 60, 64];
        for (var i = 0; i < rounds; i++)
        {
            midi.Receive(input, on, Midi.Timing.SystemMonotonicClock.Instance.Now);
            midi.Playback.Pump();
            midi.PumpRoutes();
            midi.Receive(input, off, Midi.Timing.SystemMonotonicClock.Instance.Now);
            midi.Playback.Pump();
        }
    }
}
