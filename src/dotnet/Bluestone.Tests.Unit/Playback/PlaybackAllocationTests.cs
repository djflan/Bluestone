using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using static Bluestone.Tests.Unit.Playback.PlaybackFixture;

namespace Bluestone.Tests.Unit.Playback;

/// <summary>
/// The playback thread allocates nothing (ADR 0044): not while playing, and not when it applies a command (load, new
/// outputs, seek while stopped or playing, play with a count-in, loop, stop, panic). The work that needs memory, such as
/// computing chased state, is done by whoever gives the command.
/// </summary>
public sealed class PlaybackAllocationTests
{
    [Fact]
    public void ThePlaybackThread_AllocatesNothing_ApplyingCommandsOrPlaying()
    {
        var clock = new VirtualClock(Start);
        var plan = Plan(
            Program(0, 5),
            Cc(0, 7, 100),
            Cc(0, 64, 127),
            Cc(500, 1, 60),
            Note(0, 400), Note(500, 400, 64), Note(1_000, 400, 67), Note(1_500, 400, 72),
            Cc(1_200, 64, 0));
        using var engine = new PlaybackEngine(clock, plan.TempoMap);
        var outputs = new IMidiOutput[] { new SilentOutput(EndpointCapabilities.None), new SilentOutput(EndpointCapabilities.ScheduledDelivery) };
        var countIn = new CountIn(TimeSpan.FromMilliseconds(500), [new CountInClick(TimeSpan.Zero, true), new CountInClick(TimeSpan.FromMilliseconds(250), false)]);
        engine.SetMetronome(new MetronomeClick(1), enabled: true);
        var pumped = 0L;
        var ms = 0.0;

        void Elsewhere(Action command) => Task.Run(command, TestContext.Current.CancellationToken).Wait(TestContext.Current.CancellationToken);

        void Pump(double milliseconds)
        {
            for (var end = ms + milliseconds; ms <= end; ms += 1)
            {
                clock.AdvanceTo(Start + TimeSpan.FromMilliseconds(ms));
                var before = GC.GetAllocatedBytesForCurrentThread();
                engine.Pump();
                pumped += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        // Allocations made by the code repeat every round; one-time runtime work (tier-up, a late static initializer)
        // happens once, and on a slow machine it can land in any round. So a later round must allocate nothing.
        var rounds = new List<long>();
        for (var round = 0; round < 8 && (rounds.Count < 2 || rounds[^1] != 0); round++)
        {
            pumped = 0;
            Elsewhere(() => engine.SetOutputs(outputs));
            Elsewhere(() => engine.Load(plan));
            Pump(5);
            Elsewhere(() => engine.Seek(new Tick(600)));
            Pump(5);
            Elsewhere(() => engine.Play(new Tick(250), countIn));
            Pump(1_200);
            Elsewhere(() => engine.SetLoop(new LoopRegion(new Tick(500), new Tick(1_500))));
            Pump(1_500);
            Elsewhere(() => engine.Seek(new Tick(900)));
            Pump(300);
            Elsewhere(() => engine.Load(plan));
            Pump(300);
            Elsewhere(() => engine.SetLoop(null));
            Elsewhere(engine.Panic);
            Pump(50);
            Elsewhere(engine.Stop);
            Pump(20);
            Elsewhere(() => engine.Seek(new Tick(1_100)));
            Pump(5);
            rounds.Add(pumped);
        }

        Assert.True(rounds[^1] == 0, $"Bytes allocated on the playback thread per round: {string.Join(", ", rounds)}.");
        Assert.True(outputs.Sum(o => ((SilentOutput)o).Count) > 50, "Too little was sent for the test to mean anything.");
    }

    /// <summary>Accepts and counts every message, allocating nothing.</summary>
    private sealed class SilentOutput(EndpointCapabilities capabilities) : IMidiOutput
    {
        public long Count;

        public EndpointDescriptor Endpoint { get; } = new(new EndpointId("test", Guid.NewGuid().ToString()), "Silent", EndpointDirection.Output, EndpointTransport.Test, capabilities | EndpointCapabilities.SystemExclusive);

        public EndpointState State => EndpointState.Open;

        public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
        {
            Count++;
            return SendResult.Sent;
        }

        public void Dispose()
        {
        }
    }
}
