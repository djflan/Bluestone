using Bluestone.Domain.Devices;
using Bluestone.Engine.Protocol;

namespace Bluestone.Engine;

/// <summary>
/// <c>--check-allocations</c>: plays notes from an input through thru on a live route (a transpose and a forced channel)
/// to a forwarded output while the transport plays, on one thread, as the engine's input callback, live thread, and
/// playback thread would, and checks that the thread allocates nothing once warmed up (ADR 0044's rule for real-time
/// threads). Prints the bytes allocated; exits 0 when there were none and the notes came out transposed.
/// </summary>
internal static class AllocationCheck
{
    private const int Rounds = 1000;

    public static int Run()
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
        Apply(midi, new TransportPlay(0, null));
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

        Console.WriteLine($"allocated {allocated} bytes; {transposed} notes thru, {inputs} input messages forwarded");
        return allocated == 0 && transposed == Rounds && inputs == Rounds * 2 ? 0 : 1;
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
