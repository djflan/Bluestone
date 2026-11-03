using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Platform.CoreMidi;
using Bluestone.Playback;

namespace Bluestone.Benchmarks;

/// <summary>
/// End-to-end MIDI timing through Core MIDI on one Mac, with no MIDI hardware (ADR 0043). A child process publishes a
/// virtual destination and a virtual source and timestamps everything it receives with the host clock, which every
/// process shares. This process plays dense material to that destination with the real <see cref="CoreMidiProvider"/>:
/// <list type="bullet">
/// <item><b>Scheduled</b>: Bluestone hands messages over ahead with timestamps and Core MIDI delivers them; the error is
/// the receive time minus the timestamp, so it includes Core MIDI's own scheduling.</item>
/// <item><b>Immediate</b>: messages are sent when due (the path for endpoints without scheduling, such as Bluestone's
/// virtual ports); the operating system's hop is receive time minus send time, and the playback thread's lateness
/// comes from the engine's statistics.</item>
/// <item><b>Thru</b>: the child sends notes on its source; Bluestone receives them on a Core MIDI input and sends each
/// one back to the destination with <see cref="PlaybackEngine.SendNow"/>, as live MIDI thru does, while dense material
/// plays; the child measures the round trip.</item>
/// </list>
/// </summary>
/// <remarks>Run: <c>dotnet run -c Release --project src/dotnet/Bluestone.Benchmarks -- --midi-loopback [seconds] [--pressure none|app|churn]</c>.</remarks>
[SupportedOSPlatform("macos")]
internal static class MidiLoopbackProbe
{
    internal const string SinkName = "Bluestone Timing Sink";
    internal const string EchoName = "Bluestone Timing Echo";

    public static void Run(ProbeOptions options)
    {
        var settings = options.BaseSettings();
        var report = new TimingReport("midi-loopback", settings);
        report.Machine.WarnIfBusy();
        Console.WriteLine($"MIDI loopback through Core MIDI: {options.Seconds} s per phase; pressure {settings["pressure"]}; playback thread {options.Scheduling}");
        var file = Path.Combine(Path.GetTempPath(), $"bluestone-midi-sink-{Environment.ProcessId}.bin");
        using var sink = ProbeOptions.StartSelf(["--midi-sink", file], redirect: true);
        if (sink.StandardOutput.ReadLine() != "ready")
        {
            Console.WriteLine("The sink process did not start.");
            return;
        }

        using var provider = new CoreMidiProvider(SystemMonotonicClock.Instance, "Bluestone timing probe");
        var destination = Find(provider, SinkName, EndpointDirection.Output);
        var source = Find(provider, EchoName, EndpointDirection.Input);
        using var coreOutput = provider.OpenOutputAsync(destination.Id).AsTask().GetAwaiter().GetResult();
        using var coreInput = provider.OpenInputAsync(source.Id).AsTask().GetAwaiter().GetResult();

        var (thruSequence, thruPlan) = JitterProbe.Prepare(15); // channels 1-15; the echo notes use channel 16
        var (sequence, plan) = JitterProbe.Prepare();
        using var stop = new CancellationTokenSource();
        var threads = options.StartPressure(stop.Token);
        Thread.Sleep(threads.Count > 0 ? 1000 : 0);
        using var gc = new GcWatch();

        Command(sink, "phase 1");
        Play(report, "scheduled", new SinkOutput(coreOutput, EndpointCapabilities.ScheduledDelivery, 0), options, sequence, plan);
        var immediate = new SinkOutput(coreOutput, EndpointCapabilities.None, options.Seconds * 1000);
        Command(sink, "phase 2");
        Play(report, "immediate", immediate, options, sequence, plan);

        // Thru, as the app does it: the input handler (on Core MIDI's thread) hands each message to the playback thread
        // with PlaybackEngine.SendNow while dense material plays. The handler also notes how long after the child's
        // send timestamp it ran. Nothing here allocates per message.
        var arrival = new long[options.Seconds * 200];
        var arrivals = 0;
        var thruOutput = new SinkOutput(coreOutput, EndpointCapabilities.ScheduledDelivery, 0);
        using var thruEngine = new PlaybackEngine(SystemMonotonicClock.Instance, thruSequence.TempoMap);
        thruEngine.SetOutputs([thruOutput]);
        thruEngine.Load(thruPlan);
        using var thruThread = new PlaybackThread(thruEngine, options.ThreadSetup(), options.PreciseSleep());
        coreInput.SetReceiver((message, timestamp) =>
        {
            if (arrivals < arrival.Length)
            {
                arrival[arrivals++] = (SystemMonotonicClock.Instance.Now - timestamp).Ticks;
            }

            if (message.Length == 3 && ChannelMessage.TryCreate(message[0], message[1], message[2], out var thru))
            {
                thruEngine.SendNow(thruOutput, thru);
            }
        });
        thruEngine.Play(Tick.Zero);
        Command(sink, "phase 3");
        Command(sink, $"echo {options.Seconds}");
        _ = sink.StandardOutput.ReadLine(); // "echoed"
        thruEngine.Stop();
        coreInput.SetReceiver(null);
        gc.AddTo(report);
        stop.Cancel();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        Command(sink, "stop");
        sink.WaitForExit();
        var records = SinkRecord.Read(file);
        File.Delete(file);

        var scheduled = records.Where(r => r.Phase == 1 && r.Timestamp != 0).Select(r => HostMs(r.Received) - HostMs(r.Timestamp)).ToArray();
        report.Add(new Metric("midi.scheduled.delivery-error", "ms", Distribution: Distribution.Of(scheduled.Select(Math.Abs)), Target: Targets.MidiLatenessMs));
        report.Add(new Metric("midi.scheduled.delivery-signed", "ms", Distribution: Distribution.Of(scheduled)));
        report.Add(new Metric("midi.scheduled.received", "messages", scheduled.Length));

        var received = records.Where(r => r.Phase == 2).ToArray();
        var sent = immediate.Sent;
        var hops = received.Zip(sent, (r, s) => HostMs(r.Received) - HostMs(s)).ToArray();
        report.Add(new Metric("midi.immediate.os-hop", "ms", Distribution: Distribution.Of(hops)));
        if (received.Length != sent.Count)
        {
            report.Notes.Add($"immediate phase: {sent.Count} sent, {received.Length} received; hops are matched in order");
        }

        var echoes = records.Where(r => r.Phase == 3 && r.RoundTrip > 0).Select(r => HostMs(r.RoundTrip)).ToArray();
        var hop = Distribution.Of(hops).P50;
        report.Add(new Metric("midi.thru.round-trip", "ms", Distribution: Distribution.Of(echoes)));
        // The sink's first 50 echoes warm the path up and are not counted; skip their arrivals here too.
        report.Add(new Metric("midi.thru.input-to-handler", "ms", Distribution: Distribution.Of(arrival.Take(arrivals).Skip(50).Select(t => TimeSpan.FromTicks(t).TotalMilliseconds))));
        report.Add(new Metric("midi.thru.added", "ms", Distribution: Distribution.Of(echoes.Select(e => e - (2 * hop))), Target: Targets.ThruAddedMs));
        report.Notes.Add($"midi.thru.added is the round trip less two median OS hops ({hop:0.###} ms each)");
        options.Finish(report);
    }

    private static void Play(TimingReport report, string name, SinkOutput output, ProbeOptions options, Sequence sequence, PlaybackPlan plan)
    {
        using var engine = new PlaybackEngine(SystemMonotonicClock.Instance, sequence.TempoMap);
        engine.SetOutputs([output]);
        engine.Load(plan);
        using (new PlaybackThread(engine, options.ThreadSetup(), options.PreciseSleep()))
        {
            engine.Play(Tick.Zero);
            Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
            engine.Stop();
            Thread.Sleep(100);
        }

        var stats = engine.Statistics.Snapshot();
        report.Add(new Metric($"midi.{name}.dispatch-lateness", "ms",
            Distribution: Distribution.OfHistogram(stats.FineHistogram, TimingStatistics.FineBucketWidth.TotalMilliseconds, stats.MaxLateness.TotalMilliseconds)));
        Thread.Sleep(200); // let the last scheduled messages arrive
    }

    private static EndpointDescriptor Find(CoreMidiProvider provider, string name, EndpointDirection direction)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (provider.GetEndpoints().FirstOrDefault(e => e.DisplayName == name && e.Direction == direction) is { } found)
            {
                return found;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"Core MIDI endpoint '{name}' did not appear.");
    }

    private static void Command(Process sink, string command)
    {
        sink.StandardInput.WriteLine(command);
        sink.StandardInput.Flush();
        Thread.Sleep(100);
    }

    private static readonly double NanosecondsPerHostTick = HostTimebase();

    internal static double HostMs(long host) => host * NanosecondsPerHostTick / 1e6;

    private static double HostTimebase()
    {
        _ = SinkNative.mach_timebase_info(out var timebase);
        return (double)timebase.Numer / timebase.Denom;
    }

    /// <summary>Forwards to the Core MIDI output under the benchmark's endpoint id, recording send times for immediate sends.</summary>
    private sealed class SinkOutput(IMidiOutput inner, EndpointCapabilities capabilities, int capacity) : IMidiOutput
    {
        private readonly long[] _sent = new long[capacity];
        private int _count;

        public EndpointDescriptor Endpoint { get; } = Workloads.Sink with { Capabilities = capabilities | EndpointCapabilities.SystemExclusive };

        public EndpointState State => inner.State;

        public IReadOnlyList<long> Sent => _sent.Take(_count).ToArray();

        public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
        {
            if (_count < _sent.Length && timestamp.IsImmediate)
            {
                _sent[_count++] = (long)SinkNative.mach_absolute_time();
            }

            return inner.Send(message, timestamp);
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>One message the sink received: host times, the packet's timestamp, and for thru echoes the round trip.</summary>
internal readonly record struct SinkRecord(int Phase, long Received, long Timestamp, long RoundTrip)
{
    public const int Size = 4 * sizeof(long);

    public static List<SinkRecord> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var longs = MemoryMarshal.Cast<byte, long>(bytes);
        var result = new List<SinkRecord>(longs.Length / 4);
        for (var i = 0; i + 3 < longs.Length; i += 4)
        {
            result.Add(new SinkRecord((int)longs[i], longs[i + 1], longs[i + 2], longs[i + 3]));
        }

        return result;
    }
}

/// <summary>
/// The child side of <see cref="MidiLoopbackProbe"/>: a Core MIDI virtual destination that timestamps every message on
/// arrival, and a virtual source that sends numbered notes for the thru round trip. It allocates nothing while
/// receiving, so its own garbage collector cannot delay a timestamp. Commands arrive on standard input:
/// <c>phase N</c>, <c>echo SECONDS</c>, <c>stop</c>.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MidiSink
{
    private const int Capacity = 2_000_000;
    private static readonly long[] Records = new long[Capacity * 4];
    private static readonly long[] EchoSent = new long[127 * 128];
    private static int _count;
    private static int _phase;

    public static void Run(string file)
    {
        var name = CreateString("Bluestone timing sink");
        Check(SinkNative.MIDIClientCreate(name, IntPtr.Zero, IntPtr.Zero, out var client), "MIDIClientCreate");
        Check(SinkNative.MIDIDestinationCreate(client, CreateString(MidiLoopbackProbe.SinkName), &OnRead, IntPtr.Zero, out _), "MIDIDestinationCreate");
        Check(SinkNative.MIDISourceCreate(client, CreateString(MidiLoopbackProbe.EchoName), out var source), "MIDISourceCreate");
        Console.WriteLine("ready");
        while (Console.ReadLine() is { } line)
        {
            var parts = line.Split(' ');
            switch (parts[0])
            {
                case "phase":
                    Volatile.Write(ref _phase, int.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
                case "echo":
                    Echo(source, int.Parse(parts[1], CultureInfo.InvariantCulture));
                    Console.WriteLine("echoed");
                    break;
                case "stop":
                    Thread.Sleep(200);
                    var count = Volatile.Read(ref _count);
                    File.WriteAllBytes(file, MemoryMarshal.AsBytes(Records.AsSpan(0, count * 4)).ToArray());
                    _ = SinkNative.MIDIClientDispose(client);
                    return;
                default:
                    break;
            }
        }
    }

    // Numbered note-ons on channel 16, at random intervals, each stamped with its send time.
    private static void Echo(uint source, int seconds)
    {
        var random = new Random(7);
        var packetList = stackalloc byte[64];
        var until = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        var measuring = Volatile.Read(ref _phase);
        Volatile.Write(ref _phase, 0); // the first notes warm up the thru path (JIT, first-call costs) and are not counted
        for (var seq = 0; Stopwatch.GetTimestamp() < until && seq < EchoSent.Length; seq++)
        {
            if (seq == 50)
            {
                Thread.Sleep(100);
                Volatile.Write(ref _phase, measuring);
            }

            var now = SinkNative.mach_absolute_time();
            EchoSent[seq] = (long)now;
            *(uint*)packetList = 1;
            *(ulong*)(packetList + 4) = now;
            *(ushort*)(packetList + 12) = 3;
            packetList[14] = 0x9F;
            packetList[15] = (byte)(seq & 0x7F);
            packetList[16] = (byte)((seq >> 7) + 1); // a velocity of 1 to 127, so it stays a note-on
            _ = SinkNative.MIDIReceived(source, packetList);
            Thread.Sleep(5 + random.Next(25));
        }

        Thread.Sleep(200);
    }

    [UnmanagedCallersOnly]
    private static void OnRead(IntPtr packetList, IntPtr readRefCon, IntPtr sourceRefCon)
    {
        var received = (long)SinkNative.mach_absolute_time();
        var phase = Volatile.Read(ref _phase);
        var packets = *(uint*)packetList;
        var packet = (byte*)packetList + 4;
        for (var p = 0; p < packets; p++)
        {
            var timestamp = *(long*)packet;
            var length = *(ushort*)(packet + 8);
            var data = packet + 10;
            for (var i = 0; i < length;)
            {
                var status = data[i];
                var size = status switch
                {
                    >= 0xF0 => 1,
                    >= 0xC0 and < 0xE0 => 2,
                    >= 0x80 => 3,
                    _ => 1,
                };
                long roundTrip = 0;
                if (phase == 3 && status == 0x9F && i + 2 < length)
                {
                    var seq = data[i + 1] | ((data[i + 2] - 1) << 7);
                    if (seq < EchoSent.Length && EchoSent[seq] != 0)
                    {
                        roundTrip = received - EchoSent[seq];
                    }
                }

                if (status >= 0x80 && _count < Capacity)
                {
                    var at = _count * 4;
                    Records[at] = phase;
                    Records[at + 1] = received;
                    Records[at + 2] = timestamp;
                    Records[at + 3] = roundTrip;
                    Volatile.Write(ref _count, _count + 1);
                }

                i += size;
            }

            var next = (nuint)(data + length);
            next = (next + 3) & ~(nuint)3; // MIDIPacketNext: 4-byte aligned on ARM
            packet = (byte*)next;
        }
    }

    private static IntPtr CreateString(string value)
    {
        fixed (char* chars = value)
        {
            return SinkNative.CFStringCreateWithCharacters(IntPtr.Zero, chars, value.Length);
        }
    }

    private static void Check(int status, string call)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"{call} failed: {status}");
        }
    }
}

[SupportedOSPlatform("macos")]
internal static unsafe partial class SinkNative
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct Timebase
    {
        public uint Numer;
        public uint Denom;
    }

    [LibraryImport(CoreMidi)]
    public static partial int MIDIClientCreate(IntPtr name, IntPtr notifyProc, IntPtr notifyRefCon, out uint client);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIClientDispose(uint client);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIDestinationCreate(uint client, IntPtr name, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> readProc, IntPtr refCon, out uint destination);

    [LibraryImport(CoreMidi)]
    public static partial int MIDISourceCreate(uint client, IntPtr name, out uint source);

    [LibraryImport(CoreMidi)]
    public static partial int MIDIReceived(uint source, byte* packetList);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, nint length);

    [LibraryImport(LibSystem)]
    public static partial ulong mach_absolute_time();

    [LibraryImport(LibSystem)]
    public static partial int mach_timebase_info(out Timebase info);
}
