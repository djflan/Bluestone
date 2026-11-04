using Bluestone.Application.Engine;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Playback;
using Bluestone.Profiles;

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

        // The engine echoes the resync's seek, the last transport command sent.
        var applied = link.Sent.OfType<TransportSeek>().Last().Sequence;

        var compared = 0;
        for (var ms = 0; ms <= 6000; ms += 37)
        {
            clock.AdvanceTo(TimeSpan.FromSeconds(100) + TimeSpan.FromMilliseconds(ms));
            if (ms == 3000)
            {
                engine.Seek(new Tick(2000));
            }

            engine.Pump();
            Client.Received(link, Status(engine.ReadTransport(), plan.TempoMap, applied));
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
        Client.Received(link, Status(engine.ReadTransport(), plan.TempoMap, applied));
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
        Client.Received(first, new TransportStatus(true, 500, 2, 0, new AnchorSpec(true, 1, 0, 1, 0, 960), default, Applied: 2));
        Assert.Equal(TransportState.Playing, _playback.State);

        Client.Disconnected(first);
        Assert.Equal(TransportState.Stopped, _playback.State);
        Assert.Equal(500, _playback.Position.Value);

        var second = new RecordingLink();
        Client.Connected(second, restarted: true);

        Assert.Equal(
            [typeof(SetSlots), typeof(LoadPlan), typeof(SetLoop), typeof(SetMetronome), typeof(SetThru), typeof(SetInputs), typeof(TransportSeek)],
            second.Sent.Select(m => m.GetType()));
        Assert.Equal(new TransportSeek(500, 3), second.Sent[^1]);
        Assert.True(File.Exists(((LoadPlan)second.Sent[1]).Path));
        Assert.Equal(TransportState.Stopped, _playback.State);

        // A report from the engine that stopped is ignored.
        Client.Received(first, new TransportStatus(true, 900, 3, 0, new AnchorSpec(true, 1, 0, 1, 0, 960), default, Applied: 3));
        Assert.Equal(TransportState.Stopped, _playback.State);

        // Nor is the new engine's report from before it applied the resync's seek.
        Client.Received(second, new TransportStatus(false, 0, 0, 0, default, default, Applied: 0));
        Assert.Equal(500, _playback.Position.Value);
        Client.Received(second, new TransportStatus(false, 500, 0, 0, default, default, Applied: 3));
        Assert.Equal(500, _playback.Position.Value);
    }

    [Fact]
    public void AReportSentBeforeTheEngineAppliedASeek_DoesNotMoveThePlayheadBack()
    {
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        _playback.Load(PlaybackPlan.Empty(TempoMap.Constant(Resolution, Tempo.Default)));
        _playback.Play(Tick.Zero);
        var play = (TransportPlay)link.Sent[^1];
        var playing = new AnchorSpec(true, TimeSpan.FromSeconds(100).Ticks, 0, 1, 0, 0);
        Client.Received(link, new TransportStatus(true, 100, 0, 0, playing, default, play.Sequence));

        _playback.Seek(new Tick(5000));
        var seek = (TransportSeek)link.Sent[^1];
        Assert.True(seek.Sequence > play.Sequence);

        // Sent while the seek was on its way: the playhead and the time mapping stay where Bluestone put them.
        Client.Received(link, new TransportStatus(true, 120, 0, 0, playing, default, play.Sequence));
        Assert.Equal(5000, _playback.Position.Value);
        Assert.True(_playback.TryGetTickAt(TimeSpan.FromSeconds(100), out var before));
        Assert.Equal(0, before.Value);

        // Once the engine has applied the seek, its reports count again.
        var moved = new AnchorSpec(true, TimeSpan.FromSeconds(101).Ticks, TempoMap.Constant(Resolution, Tempo.Default).TimeAt(new Tick(5000)).Ticks, 1, 0, 0);
        Client.Received(link, new TransportStatus(true, 5010, 0, 0, moved, playing, seek.Sequence));
        Assert.Equal(5010, _playback.Position.Value);
        Assert.True(_playback.TryGetTickAt(TimeSpan.FromSeconds(101), out var after));
        Assert.Equal(5000, after.Value);

        _playback.Stop();
        Client.Received(link, new TransportStatus(true, 5100, 0, 0, moved, playing, seek.Sequence));
        Assert.Equal(5010, _playback.Position.Value);
        Assert.Equal(TransportState.Playing, _playback.State);
        Client.Received(link, new TransportStatus(false, 5100, 0, 0, default, moved, ((TransportStop)link.Sent[^1]).Sequence));
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

    private static TransportStatus Status(TransportSnapshot transport, TempoMap tempo, long applied) => new(
        transport.State == TransportState.Playing,
        transport.Position.Value,
        transport.LoopWraps,
        transport.CountInUntil.Ticks,
        Anchor(transport.Current, tempo),
        Anchor(transport.Previous, tempo),
        applied);

    // As the engine reports an anchor: plan 1 is the only plan loaded.
    private static AnchorSpec Anchor(TransportAnchor anchor, TempoMap tempo) =>
        new(anchor.Playing, anchor.Time.Ticks, anchor.SongTime.Ticks, ReferenceEquals(anchor.Tempo, tempo) ? 1 : -1, anchor.LoopStart, anchor.LoopEnd);

    [Fact]
    public async Task ItIsReady_OnlyWhileAnEngineIsConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await _playback.WhenReadyAsync(TimeSpan.FromMilliseconds(20), ct));

        var waiting = _playback.WhenReadyAsync(TimeSpan.FromSeconds(30), ct);
        var link = new RecordingLink();
        Client.Connected(link, restarted: false);
        Assert.True(await waiting);
        Assert.True(await _playback.WhenReadyAsync(TimeSpan.Zero, ct));

        // A crashed engine: commands would be dropped until the next one is connected.
        Client.Disconnected(link);
        Assert.False(await _playback.WhenReadyAsync(TimeSpan.FromMilliseconds(20), ct));
        Client.Connected(new RecordingLink(), restarted: true);
        Assert.True(await _playback.WhenReadyAsync(TimeSpan.Zero, ct));
    }

    [Fact]
    public async Task PlayAndRecord_WhileNoEngineIsReady_FailWithAMessage_AndRecordNothing()
    {
        using var directory = new EndpointDirectory([]);
        var session = new ProjectSession();
        await using var controller = new PlaybackController(session, directory, ProfileCatalog.Empty, _playback, startLiveThread: false)
        {
            EngineReadyWait = TimeSpan.FromMilliseconds(20),
        };
        var track = TrackId.New();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.PlayAsync(cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RecordAsync(track, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(controller.Recorder.IsRecording);
    }

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
