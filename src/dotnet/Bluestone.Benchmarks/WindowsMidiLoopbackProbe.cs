using System.Diagnostics;
using System.Runtime.Versioning;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Midi.Wire;
using Bluestone.Platform.Windows;
using Bluestone.Playback;

namespace Bluestone.Benchmarks;

[SupportedOSPlatform("windows")]
internal static class WindowsMidiLoopbackProbe
{
    private const string LoopbackName = "Bluestone Loopback";
    private const byte SourceController = 119;
    private const byte EchoStatus = 0xBF;

    public static void Run(ProbeOptions options)
    {
        using var provider = new WinMmProvider(SystemMonotonicClock.Instance);
        var endpoints = provider.GetEndpoints();
        var inputEndpoint = endpoints.FirstOrDefault(e => e.Direction == EndpointDirection.Input && e.DisplayName == LoopbackName);
        var outputEndpoint = endpoints.FirstOrDefault(e => e.Direction == EndpointDirection.Output && e.DisplayName == LoopbackName);
        if (inputEndpoint is null || outputEndpoint is null)
        {
            Console.WriteLine($"Create a Windows MIDI Services MIDI 1.0 loopback named '{LoopbackName}' to run this probe.");
            return;
        }

        var report = new TimingReport("winmm-midi-loopback", options.BaseSettings());
        report.Machine.WarnIfBusy();
        Console.WriteLine($"WinMM MIDI loopback: {LoopbackName}; {options.Seconds} s per phase; pressure {options.Pressure}; playback thread {options.Scheduling}");

        using var input = provider.OpenInputAsync(inputEndpoint.Id).AsTask().GetAwaiter().GetResult();
        using var nativeOutput = provider.OpenOutputAsync(outputEndpoint.Id).AsTask().GetAwaiter().GetResult();
        var capture = new Capture(options.Seconds * 5000);
        var output = new MeasuredOutput(nativeOutput, options.Seconds * 5000);
        input.SetReceiver(capture.Receive);

        var (sequence, plan) = JitterProbe.Prepare();
        output.Phase = 1;
        capture.Phase = 1;
        using (var engine = new PlaybackEngine(SystemMonotonicClock.Instance, sequence.TempoMap))
        {
            engine.SetOutputs([output]);
            engine.Load(plan);
            using (new PlaybackThread(engine, options.ThreadSetup(), options.PreciseSleep()))
            {
                engine.Play(Tick.Zero);
                var stop = new CancellationTokenSource();
                var pressure = options.StartPressure(stop.Token);
                Thread.Sleep(pressure.Count > 0 ? 1000 : 0);
                var before = Stopwatch.GetTimestamp();
                Thread.Sleep(TimeSpan.FromSeconds(options.Seconds));
                var dispatch = engine.Statistics.Snapshot();
                engine.Stop();
                Thread.Sleep(100);
                stop.Cancel();
                foreach (var thread in pressure)
                {
                    thread.Join();
                }

                report.Add(new Metric("midi.immediate.dispatch-lateness", "ms",
                    Distribution: Distribution.OfHistogram(dispatch.FineHistogram, TimingStatistics.FineBucketWidth.TotalMilliseconds, dispatch.MaxLateness.TotalMilliseconds),
                    Target: Targets.MidiLatenessMs));
                report.Add(new Metric("midi.immediate.late-over-2ms", "messages", dispatch.Late));
                report.Add(new Metric("midi.immediate.dropped", "messages", dispatch.Dropped + dispatch.SkippedLateNotes, Target: 0));
                report.Add(new Metric("midi.immediate.rate", "messages/s", dispatch.Dispatched / (double)options.Seconds));
                report.Notes.Add($"immediate phase ran for {Stopwatch.GetElapsedTime(before).TotalSeconds:0.##} s; WinMM does not support scheduled delivery.");
            }
        }

        capture.Phase = 0;
        output.Phase = 0;
        var sent = output.Sent;
        var received = capture.Received;
        var matched = Math.Min(sent.Length, received.Length);
        var hop = new double[matched];
        for (var i = 0; i < matched; i++)
        {
            hop[i] = Stopwatch.GetElapsedTime(sent[i], received[i]).TotalMilliseconds;
        }

        report.Add(new Metric("midi.immediate.os-hop", "ms", Distribution: Distribution.Of(hop)));
        report.Add(new Metric("midi.immediate.sent", "messages", sent.Length));
        report.Add(new Metric("midi.immediate.received", "messages", received.Length));
        if (sent.Length != received.Length)
        {
            report.Notes.Add($"WinMM loopback delivered {received.Length} of {sent.Length} immediate messages; OS-hop samples are matched in order.");
        }

        var (thruSequence, thruPlan) = JitterProbe.Prepare(15);
        using var thruEngine = new PlaybackEngine(SystemMonotonicClock.Instance, thruSequence.TempoMap);
        thruEngine.SetOutputs([output]);
        thruEngine.Load(thruPlan);
        using (new PlaybackThread(thruEngine, options.ThreadSetup(), options.PreciseSleep()))
        using (var sourceOutput = provider.OpenOutputAsync(outputEndpoint.Id).AsTask().GetAwaiter().GetResult())
        {
            capture.BeginThru(thruEngine, output);
            thruEngine.Play(Tick.Zero);
            GenerateControllerInput(sourceOutput, capture, options.Seconds);
            Thread.Sleep(100);
            thruEngine.Stop();
        }

        var roundTrips = capture.RoundTrips;
        report.Add(new Metric("midi.thru.round-trip", "ms", Distribution: Distribution.Of(roundTrips.Skip(Math.Min(3, roundTrips.Length)))));
        options.Finish(report);
    }

    private static void GenerateControllerInput(IMidiOutput output, Capture capture, int seconds)
    {
        var random = new Random(7);
        var until = Stopwatch.GetTimestamp() + (seconds * Stopwatch.Frequency);
        var value = 0;
        while (Stopwatch.GetTimestamp() < until)
        {
            value = value % 127 + 1;
            capture.SetSent(value, Stopwatch.GetTimestamp());
            var message = new byte[] { 0xB0, SourceController, (byte)value };
            if (output.Send(message, MidiTimestamp.Immediate) != SendResult.Sent)
            {
                capture.SetSent(value, 0);
            }

            Thread.Sleep(20 + random.Next(80));
        }
    }

    private sealed class MeasuredOutput(IMidiOutput inner, int capacity) : IMidiOutput
    {
        private readonly long[] _sent = new long[capacity];
        private int _count;

        public int Phase;

        public EndpointDescriptor Endpoint => inner.Endpoint;

        public EndpointState State => inner.State;

        public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public long[] Sent => _sent.AsSpan(0, Math.Min(Volatile.Read(ref _count), _sent.Length)).ToArray();

        public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
        {
            var sent = Stopwatch.GetTimestamp();
            var result = inner.Send(message, timestamp);
            if (result == SendResult.Sent && Volatile.Read(ref Phase) == 1)
            {
                var index = Interlocked.Increment(ref _count) - 1;
                if (index < _sent.Length)
                {
                    _sent[index] = sent;
                }
            }

            return result;
        }

        public void Dispose()
        {
        }
    }

    private sealed class Capture(int capacity)
    {
        private readonly long[] _received = new long[capacity];
        private readonly long[] _sourceSent = new long[128];
        private readonly double[] _roundTrips = new double[capacity];
        private int _receivedCount;
        private int _roundTripCount;
        private PlaybackEngine? _engine;
        private IMidiOutput? _output;

        public int Phase;

        public long[] Received => _received.AsSpan(0, Math.Min(Volatile.Read(ref _receivedCount), _received.Length)).ToArray();

        public double[] RoundTrips => _roundTrips.AsSpan(0, Math.Min(Volatile.Read(ref _roundTripCount), _roundTrips.Length)).ToArray();

        public void SetSent(int value, long timestamp) => Volatile.Write(ref _sourceSent[value], timestamp);

        public void BeginThru(PlaybackEngine engine, IMidiOutput output)
        {
            _engine = engine;
            _output = output;
            Volatile.Write(ref Phase, 2);
        }

        public void Receive(ReadOnlySpan<byte> message, TimeSpan timestamp)
        {
            var phase = Volatile.Read(ref Phase);
            if (phase == 1)
            {
                var index = Interlocked.Increment(ref _receivedCount) - 1;
                if (index < _received.Length)
                {
                    _received[index] = Stopwatch.GetTimestamp();
                }

                return;
            }

            if (phase != 2 || message.Length != 3 || message[1] != SourceController)
            {
                return;
            }

            if (message[0] == 0xB0)
            {
                var value = message[2];
                if (Volatile.Read(ref _sourceSent[value]) != 0
                    && ChannelMessage.TryCreate(EchoStatus, SourceController, value, out var echo))
                {
                    _engine?.SendNow(_output!, echo);
                }
            }
            else if (message[0] == EchoStatus)
            {
                var value = message[2];
                var sent = Interlocked.Exchange(ref _sourceSent[value], 0);
                if (sent != 0)
                {
                    var index = Interlocked.Increment(ref _roundTripCount) - 1;
                    if (index < _roundTrips.Length)
                    {
                        _roundTrips[index] = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                    }
                }
            }
        }
    }
}
