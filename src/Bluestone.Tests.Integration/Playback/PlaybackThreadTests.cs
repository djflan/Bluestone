using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Signal;

namespace Bluestone.Tests.Integration.Playback;

[Collection(TimingTests.Name)]
public sealed class PlaybackThreadTests
{
    private static readonly Ppqn Resolution = new(500); // 1 tick = 1 ms at 120 BPM

    [Theory]
    [InlineData(EndpointCapabilities.None)]
    [InlineData(EndpointCapabilities.ScheduledDelivery)]
    public async Task RealThread_DeliversEveryMessageInOrder(EndpointCapabilities capabilities)
    {
        var clock = SystemMonotonicClock.Instance;
        using var provider = new LoopbackMidiProvider(clock, capabilities);
        var port = provider.CreatePort("Bus");
        using var output = await provider.OpenOutputAsync(port.OutputId, TestContext.Current.CancellationToken);

        const int noteCount = 40;
        var notes = Enumerable.Range(0, noteCount)
            .Select(i => (TrackEvent)new NoteEvent(new Tick(i * 5), new TickSpan(3), MidiChannel.FromIndex(0), new NoteNumber(40 + i), Velocity.Max));
        var track = Track.FromEvents(TrackId.New(), "t", notes);
        var sequence = Sequence.CreateEmpty(Resolution).WithTrack(track);

        using var engine = new PlaybackEngine(clock, sequence.TempoMap);
        engine.SetOutputs([output]);
        engine.Load(PlaybackPlanCompiler.Compile(sequence, [new PlanPart(0, [.. track.ArrangedEvents.Select((e, i) => new SignalEvent(e, 0, i))])]));

        using (new PlaybackThread(engine))
        {
            engine.Play(Tick.Zero);
            var deadline = clock.Now + TimeSpan.FromSeconds(5);
            while (port.Sent.Count < noteCount * 2 && clock.Now < deadline)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            engine.Stop();
        }

        var sent = port.Sent;
        Assert.Equal(noteCount * 2, sent.Count);
        var onsInOrder = sent.Where(m => m.Bytes[0] == 0x90).Select(m => (int)m.Bytes[1]).ToList();
        Assert.Equal(Enumerable.Range(40, noteCount), onsInOrder);

        // Generous bound: this asserts the thread keeps up, not a jitter target.
        var stats = engine.Statistics.Snapshot();
        Assert.Equal(0, stats.Dropped);
        Assert.True(stats.Percentile(95) <= TimeSpan.FromMilliseconds(50), $"p95 lateness {stats.Percentile(95)}");
    }

    [Fact]
    public void Dispose_StopsAnIdleThreadPromptly()
    {
        using var engine = new PlaybackEngine(SystemMonotonicClock.Instance, TempoMap.Constant(Resolution, Tempo.Default));
        var thread = new PlaybackThread(engine);

        var started = SystemMonotonicClock.Instance.Now;
        thread.Dispose();

        Assert.True(SystemMonotonicClock.Instance.Now - started < TimeSpan.FromSeconds(1));
        Assert.Null(thread.Fault);
    }
}
