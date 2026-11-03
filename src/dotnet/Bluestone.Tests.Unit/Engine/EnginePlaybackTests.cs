using Bluestone.Application.Engine;
using Bluestone.Application.Status;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Timing;
using Bluestone.Playback;

namespace Bluestone.Tests.Unit.Engine;

/// <summary>
/// Bluestone's side of MIDI in the engine (ADR 0044, migration step 5): the transport it mirrors from the engine's
/// reports maps clock times to song positions exactly as the in-process engine does, and every (re)connected engine is
/// sent everything it needs, with the transport stopped.
/// </summary>
public sealed class EnginePlaybackTests : IAsyncDisposable
{
    private static readonly Ppqn Resolution = Ppqn.Default;
    private readonly string _plans = Path.Combine(AppContext.BaseDirectory, "engine-plans", Guid.NewGuid().ToString("N"));
    private readonly EngineSupervisor _supervisor = new(new EngineSupervisorOptions(), new ActivityLog());
    private readonly EnginePlayback _playback;

    public EnginePlaybackTests() => _playback = new EnginePlayback(_supervisor, new EnginePlaybackOptions { PlanDirectory = _plans });

    private IEngineClient Client => _playback;

    public async ValueTask DisposeAsync()
    {
        _playback.Dispose();
        await _supervisor.DisposeAsync();
    }

    [Fact]
    public void TheMirroredTransport_MapsTimesToPositions_AsTheEngineDoes_AcrossTempoChangesLoopWrapsAndSeeks()
    {
        var tempo = new TempoMap(Resolution, [new TempoChange(Tick.Zero, Tempo.FromBeatsPerMinute(120)), new TempoChange(new Tick(1920), Tempo.FromBeatsPerMinute(90))]);
        var plan = PlaybackPlan.Empty(tempo);
        var clock = new VirtualClock(TimeSpan.FromSeconds(100));
        using var engine = new PlaybackEngine(clock, tempo);
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        engine.Load(plan);
        _playback.Load(plan);
        engine.SetLoop(new LoopRegion(new Tick(960), new Tick(3840)));
        engine.Play(new Tick(480));
        engine.Pump();

        var compared = 0;
        for (var ms = 0; ms <= 6000; ms += 37)
        {
            clock.AdvanceTo(TimeSpan.FromSeconds(100) + TimeSpan.FromMilliseconds(ms));
            if (ms == 3000)
            {
                engine.Seek(new Tick(2000));
            }

            engine.Pump();
            Client.Received(link, Status(engine.ReadTransport(), plan.TempoMap));
            foreach (var offset in new[] { -400, -30, 0, 15, 250, 900 })
            {
                var time = clock.Now + TimeSpan.FromMilliseconds(offset);
                Assert.Equal(engine.TryGetTickAt(time, out var expected), _playback.TryGetTickAt(time, out var mirrored));
                Assert.Equal(expected, mirrored);
                Assert.Equal(engine.TryGetSongTimeAt(time, out var expectedSong), _playback.TryGetSongTimeAt(time, out var mirroredSong));
                Assert.Equal(expectedSong, mirroredSong);
                compared++;
            }

            Assert.Equal(engine.Position, _playback.Position);
            Assert.Equal(engine.LoopWraps, _playback.LoopWraps);
            Assert.Equal(TransportState.Playing, _playback.State);
        }

        Assert.True(engine.LoopWraps >= 1);
        Assert.True(compared > 900);

        engine.Stop();
        engine.Pump();
        Client.Received(link, Status(engine.ReadTransport(), plan.TempoMap));
        Assert.Equal(TransportState.Stopped, _playback.State);
        Assert.False(_playback.TryGetTickAt(clock.Now, out _));
    }

    [Fact]
    public void AReconnectedEngine_IsSentEverything_InOrder_AndTheTransportIsStoppedWhereItWas()
    {
        var first = new RecordingLink();
        Client.Connected(first, restarted: false);
        _playback.Load(PlaybackPlan.Empty(TempoMap.Constant(Resolution, Tempo.Default)));
        _playback.SetLoop(new LoopRegion(Tick.Zero, new Tick(960)));
        _playback.SetMetronome(new MetronomeClick(0), enabled: true);
        _playback.SetThru(Guid.NewGuid());
        _playback.Seek(new Tick(480));
        Client.Received(first, new TransportStatus(true, 500, 2, 0, new AnchorSpec(true, 1, 0, 1, 0, 960), default));
        Assert.Equal(TransportState.Playing, _playback.State);

        Client.Disconnected(first);
        Assert.Equal(TransportState.Stopped, _playback.State);
        Assert.Equal(500, _playback.Position.Value);

        var second = new RecordingLink();
        Client.Connected(second, restarted: true);

        Assert.Equal(
            [typeof(SetSlots), typeof(LoadPlan), typeof(SetLoop), typeof(SetMetronome), typeof(SetThru), typeof(SetInputs), typeof(TransportSeek)],
            second.Sent.Select(m => m.GetType()));
        Assert.Equal(new TransportSeek(500), second.Sent[^1]);
        Assert.True(File.Exists(((LoadPlan)second.Sent[1]).Path));
        Assert.Equal(TransportState.Stopped, _playback.State);

        // A report from the engine that stopped is ignored.
        Client.Received(first, new TransportStatus(true, 900, 3, 0, new AnchorSpec(true, 1, 0, 1, 0, 960), default));
        Assert.Equal(TransportState.Stopped, _playback.State);
    }

    [Fact]
    public void APlanFile_IsDeleted_OnceTheEngineHasMovedPastIt()
    {
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        _playback.Load(PlaybackPlan.Empty(TempoMap.Constant(Resolution, Tempo.Default)));
        _playback.Load(PlaybackPlan.Empty(TempoMap.Constant(Resolution, Tempo.Default)));
        var loads = link.Sent.OfType<LoadPlan>().ToList();

        Client.Received(link, new PlanLoaded(loads[1].PlanId));

        Assert.False(File.Exists(loads[0].Path));
        Assert.True(File.Exists(loads[1].Path));
    }

    private static TransportStatus Status(TransportSnapshot transport, TempoMap tempo) => new(
        transport.State == TransportState.Playing,
        transport.Position.Value,
        transport.LoopWraps,
        transport.CountInUntil.Ticks,
        Anchor(transport.Current, tempo),
        Anchor(transport.Previous, tempo));

    // As the engine reports an anchor: plan 1 is the only plan loaded.
    private static AnchorSpec Anchor(TransportAnchor anchor, TempoMap tempo) =>
        new(anchor.Playing, anchor.Time.Ticks, anchor.SongTime.Ticks, ReferenceEquals(anchor.Tempo, tempo) ? 1 : -1, anchor.LoopStart, anchor.LoopEnd);

    private sealed class RecordingLink : IEngineLink
    {
        public List<EngineMessage> Sent { get; } = [];

        public bool Send(EngineMessage message)
        {
            Sent.Add(message);
            return true;
        }
    }
}
