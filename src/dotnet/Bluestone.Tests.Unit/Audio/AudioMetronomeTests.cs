using System.Diagnostics;
using System.Reflection;
using Bluestone.Application.Audio;
using Bluestone.Audio;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Playback;
using Bluestone.Tests.Unit.Playback;
using static Bluestone.Tests.Unit.Playback.PlaybackFixture;

namespace Bluestone.Tests.Unit.Audio;

public sealed class AudioMetronomeTests
{
    private const int Rate = 48_000;
    private const int Block = 256;

    [Fact]
    public void AudioClick_IsRenderedBeforeMasterMuteAndWithoutMidiOutput()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var source = new PlaybackAudioClickSource(playback.Engine);

        using var audible = new AudioEngine(Rate, Block, streamFromDisk: false, audioClick: source);
        audible.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
        var heard = new OfflineAudioDriver(audible, serviceStreams: false) { StartTimestamp = Timestamp(Start) }.Render(Block, Block);

        using var muted = new AudioEngine(Rate, Block, streamFromDisk: false, audioClick: source);
        muted.Publish(new AudioGraphBuilder().Build(Block, new MixerStripState(), masterMuted: true));
        muted.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
        var silent = new OfflineAudioDriver(muted, serviceStreams: false) { StartTimestamp = Timestamp(Start) }.Render(Block, Block);

        Assert.Contains(heard, sample => Math.Abs(sample) > 0.1f);
        Assert.All(silent, sample => Assert.Equal(0, sample));
        Assert.DoesNotContain(playback.Sent(), message => message.StartsWith("99", StringComparison.Ordinal));
    }

    [Fact]
    public void AudioClick_FollowsCountInAndLoopedTempoMeterTiming()
    {
        using var playback = new PlaybackFixture();
        var metronome = (IAudioMetronome)playback.Engine;
        var tempo = TempoMap.Constant(Resolution, Tempo.FromBeatsPerMinute(60));
        var meter = MeterMap.Constant(Resolution, new TimeSignature(3, 4));
        var sequence = new Sequence(tempo, meter, [Track.Create("Timing")], []);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(1500)));
        playback.Engine.Load(PlanDump.Compile(PlanDump.Routed(sequence)));
        playback.Engine.Play(Tick.Zero, CountIn.Bars(
            tempo,
            meter,
            Tick.Zero,
            1));
        playback.PumpAt(0);

        AssertClicks(
            metronome,
            Start,
            Start + TimeSpan.FromSeconds(7.1),
            (0, true), (1000, false), (2000, false),
            (3000, true), (4000, false), (5000, false), (6000, true), (7000, false));
    }

    [Fact]
    public void AudioClick_AlignsAcrossMidSongTempoAndMeterChanges()
    {
        using var playback = new PlaybackFixture();
        var tempo = new TempoMap(Resolution, [
            new TempoChange(Tick.Zero, Tempo.FromBeatsPerMinute(120)),
            new TempoChange(new Tick(1920), Tempo.FromBeatsPerMinute(60)),
        ]);
        var meter = new MeterMap(Resolution, [
            new MeterChange(Tick.Zero, TimeSignature.CommonTime),
            new MeterChange(new Tick(1920), new TimeSignature(3, 4)),
        ]);
        var sequence = new Sequence(tempo, meter, [Track.Create("Timing")], []);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Engine.Load(PlanDump.Compile(PlanDump.Routed(sequence)));
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);

        AssertClicks(
            (IAudioMetronome)playback.Engine,
            Start + TimeSpan.FromMilliseconds(1800),
            Start + TimeSpan.FromMilliseconds(3000),
            (1920, true), (2920, false));
    }

    [Fact]
    public void AudioCallback_WithClick_AllocatesNothing()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };
        var samples = new float[2 * Block];

        for (var warm = 0; warm < 100; warm++)
        {
            driver.Render(samples, Block);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var block = 0; block < 1000; block++)
        {
            driver.Render(samples, Block);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void AudioCallback_WithNudges_AllocatesNothing()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };
        var samples = new float[480 * 2];
        long allocated = 0;

        for (var block = 0; block < 200; block++)
        {
            playback.Engine.Nudge(TimeSpan.FromTicks(1));
            playback.PumpAt(block * 10);
            var before = GC.GetAllocatedBytesForCurrentThread();
            driver.Render(samples, 480);
            if (block >= 100)
            {
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void AudioClick_EnabledAfterABeat_WaitsForTheNextBeat()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: false, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        playback.PumpAt(1020);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.PumpAt(1020);

        Assert.False(((IAudioMetronome)playback.Engine).TryGetAudioClick(
            Start + TimeSpan.FromMilliseconds(980),
            Start + TimeSpan.FromMilliseconds(1100),
            out _));
        Assert.True(((IAudioMetronome)playback.Engine).TryGetAudioClick(
            Start + TimeSpan.FromMilliseconds(1100),
            Start + TimeSpan.FromMilliseconds(1600),
            out var click));
        Assert.Equal(Start + TimeSpan.FromMilliseconds(1500), click.Timestamp);
    }

    [Fact]
    public void AudioClick_ExtremeTempoDoesNotExhaustTheBlockCandidateBudget()
    {
        using var playback = new PlaybackFixture();
        var tempo = TempoMap.Constant(Resolution, new Tempo(1));
        var meter = MeterMap.Constant(Resolution, TimeSignature.CommonTime);
        var sequence = new Sequence(tempo, meter, [Track.Create("Timing")], []);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Engine.Load(PlanDump.Compile(PlanDump.Routed(sequence)));
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);

        using var engine = new AudioEngine(8_000, Block, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
        var rendered = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) }
            .Render(16_000, Block);
        var lateClickStart = 1_800 * 8_000 / 1_000 * 2;
        var lateClickEnd = 1_840 * 8_000 / 1_000 * 2;

        Assert.Contains(rendered[lateClickStart..lateClickEnd], sample => Math.Abs(sample) > 0.1f);
    }

    [Fact]
    public void AudioClick_FirstCountInClickPastBeforeTheFirstQueuedBlock_SoundsWholeAtBlockStart()
    {
        using var playback = new PlaybackFixture();
        var tempo = TempoMap.Constant(Resolution, Tempo.FromBeatsPerMinute(60));
        var meter = MeterMap.Constant(Resolution, TimeSignature.CommonTime);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Engine.Load(PlanDump.Compile(PlanDump.Routed(new Sequence(tempo, meter, [Track.Create("Timing")], []))));
        playback.Engine.Play(Tick.Zero, CountIn.Bars(tempo, meter, Tick.Zero, 1));
        playback.PumpAt(0);

        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
        var rendered = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start + TimeSpan.FromMilliseconds(100)) }
            .Render(Block, Block);

        Assert.Contains(rendered[..64], sample => Math.Abs(sample) > 0.1f);
    }

    [Fact]
    public void AudioClick_NudgeDuringCountIn_UsesTheNudgedPlaybackAnchor()
    {
        using var playback = new PlaybackFixture();
        var tempo = TempoMap.Constant(Resolution, Tempo.FromBeatsPerMinute(120));
        var meter = MeterMap.Constant(Resolution, TimeSignature.CommonTime);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero, CountIn.Bars(tempo, meter, Tick.Zero, 1));
        playback.PumpAt(0);
        playback.PumpAt(1000);
        playback.Engine.Nudge(TimeSpan.FromTicks(104));
        playback.PumpAt(1000);

        var metronome = playback.Engine;
        Assert.True(metronome.TryGetAudioClick(
            Start + TimeSpan.FromMilliseconds(1990),
            Start + TimeSpan.FromMilliseconds(2010),
            out var click));
        Assert.Equal(Start + TimeSpan.FromSeconds(2) - TimeSpan.FromTicks(104), click.Timestamp);
        Assert.True(click.Downbeat);
        AssertClicks(metronome, Start + TimeSpan.FromMilliseconds(1490), Start + TimeSpan.FromMilliseconds(1510), (1500, false));
    }

    [Fact]
    public void AudioClick_NudgeAcrossBlockCutoff_PlaysTheMovedBeatOnce()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };

        driver.Render(Rate / 2, 480);
        playback.PumpAt(499);
        playback.Engine.Nudge(TimeSpan.FromTicks(104));
        playback.PumpAt(499);
        var afterNudge = driver.Render(480 * 6, 480);

        Assert.Contains(afterNudge[..(480 * 2)], sample => Math.Abs(sample) > 0.1f);
        Assert.DoesNotContain(afterNudge[(480 * 4 * 2)..], sample => sample != 0);
    }

    [Fact]
    public void AudioClick_LaterNudge_DoesNotRepeatAnAlreadySoundingBeat()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };

        driver.Render((Rate / 2) + 480, 480);
        playback.PumpAt(510);
        playback.Engine.Nudge(TimeSpan.FromMilliseconds(-100));
        playback.PumpAt(510);
        var afterNudge = driver.Render(480 * 15, 480);

        Assert.Contains(afterNudge[..(480 * 3 * 2)], sample => Math.Abs(sample) > 0.01f);
        Assert.DoesNotContain(afterNudge[(480 * 4 * 2)..], sample => sample != 0);
    }

    [Fact]
    public void AudioClick_BeatMovedIntoPreviousBlock_RendersOnlyItsRemainingTail()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };

        driver.Render(Rate / 2, 480);
        playback.PumpAt(499);
        playback.Engine.Nudge(TimeSpan.FromMilliseconds(20));
        playback.PumpAt(499);
        var afterNudge = driver.Render(480 * 5, 480);
        var expectedFirstSample = (float)(0.22 * 0.25 * Math.Sin(2 * Math.PI * 1320 * 0.020));

        Assert.Equal(expectedFirstSample, afterNudge[0], precision: 4);
        Assert.DoesNotContain(afterNudge[(480 * 2 * 2)..], sample => sample != 0);
    }

    [Fact]
    public void AudioClick_NudgeBetweenTimelineAndClick_UsesTheTimelineSnapshot()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new InterleavingTimeline(new PlaybackTimeline(playback.Engine), () =>
        {
            playback.Engine.Nudge(TimeSpan.FromMilliseconds(-3));
            playback.PumpAt(500);
        });
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(timeline, Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start + TimeSpan.FromMilliseconds(500)) };

        var first = driver.Render(480, 480);
        var after = driver.Render(480 * 9, 480);
        var expected = (float)(0.22 * Math.Pow(1 - (5.0 / (Rate * 0.04)), 2) * Math.Sin(2 * Math.PI * 1320 * 5 / Rate));

        Assert.Equal(expected, first[10], precision: 4);
        Assert.DoesNotContain(after[(480 * 4 * 2)..], sample => sample != 0);
    }

    [Fact]
    public void AudioClick_SeekBetweenTimelineAndClick_FinishesOldBlockThenUsesNewSnapshot()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new InterleavingTimeline(new PlaybackTimeline(playback.Engine), () =>
        {
            playback.Engine.Seek(new Tick(1000));
            playback.PumpAt(0);
        });
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(timeline, Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };

        var beforeSeek = driver.Render(480, 480);
        var afterSeek = driver.Render(480, 480);

        Assert.Contains(beforeSeek, sample => Math.Abs(sample) > 0.1f);
        Assert.Contains(afterSeek, sample => Math.Abs(sample) > 0.1f);
    }

    [Fact]
    public void AudioClick_StopBetweenTimelineAndClick_FinishesOldBlockAndRecoversOnRestart()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new InterleavingTimeline(new PlaybackTimeline(playback.Engine), () =>
        {
            playback.Engine.Stop();
            playback.PumpAt(0);
        });
        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(timeline, Rate);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };

        var beforeStop = driver.Render(480, 480);
        var stopped = driver.Render(480, 480);
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(20);
        var restarted = driver.Render(480, 480);

        Assert.Contains(beforeStop, sample => Math.Abs(sample) > 0.1f);
        Assert.DoesNotContain(stopped, sample => sample != 0);
        Assert.Contains(restarted, sample => Math.Abs(sample) > 0.1f);
    }

    [Fact]
    public void AudioClick_RepeatedLookupInOneBlock_ReturnsTheSameEvent()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new PlaybackTimeline(playback.Engine);
        var source = new PlaybackAudioClickSource(playback.Engine);
        Assert.True(timeline.TryGetPosition(Timestamp(Start + TimeSpan.FromMilliseconds(490)), out var position));
        var from = Start + TimeSpan.FromMilliseconds(490);
        var until = Start + TimeSpan.FromMilliseconds(510);

        Assert.True(source.TryGetClick(from, until, position.ClickGeneration, out var first, out var firstReady));
        Assert.True(source.TryGetClick(from, until, position.ClickGeneration, out var second, out var secondReady));
        Assert.True(firstReady && secondReady);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioClick_PlanOrLoopChangeBetweenReads_UsesTheSameSnapshot(bool changeLoop)
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new PlaybackTimeline(playback.Engine);
        var source = new PlaybackAudioClickSource(playback.Engine);
        Assert.True(timeline.TryGetPosition(Timestamp(Start + TimeSpan.FromMilliseconds(490)), out var before));

        if (changeLoop)
        {
            playback.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(250)));
        }
        else
        {
            var tempo = TempoMap.Constant(Resolution, Tempo.FromBeatsPerMinute(60));
            playback.Engine.Load(PlaybackFixture.Plan(tempo));
        }

        playback.PumpAt(0);
        Assert.True(source.TryGetClick(
            Start + TimeSpan.FromMilliseconds(490),
            Start + TimeSpan.FromMilliseconds(510),
            before.ClickGeneration,
            out var click,
            out var ready));
        Assert.True(ready);
        Assert.Equal(Start + TimeSpan.FromMilliseconds(500), click.Timestamp);
        Assert.False(click.Downbeat);

        Assert.True(timeline.TryGetPosition(Timestamp(Start + TimeSpan.FromMilliseconds(740)), out var after));
        var found = source.TryGetClick(
            Start + TimeSpan.FromMilliseconds(740),
            Start + TimeSpan.FromMilliseconds(760),
            after.ClickGeneration,
            out click,
            out ready);
        Assert.True(ready);
        Assert.Equal(changeLoop, found);
        if (changeLoop)
        {
            Assert.Equal(Start + TimeSpan.FromMilliseconds(750), click.Timestamp);
            Assert.True(click.Downbeat);
        }
    }

    [Fact]
    public async Task AudioTimeline_AnchorWriterPaused_DoesNotWaitForIt()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new PlaybackTimeline(playback.Engine);
        var timestamp = Timestamp(Start + TimeSpan.FromMilliseconds(100));
        Assert.True(timeline.TryGetPosition(timestamp, out var previous));

        var version = typeof(PlaybackEngine).GetField("_anchorVersion", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var original = Assert.IsType<int>(version.GetValue(playback.Engine));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        version.SetValue(playback.Engine, original | 1);
        var read = Task.Run(() =>
        {
            started.SetResult();
            return timeline.TryGetPosition(timestamp, out var position) ? position : default;
        }, TestContext.Current.CancellationToken);
        var completedWhilePaused = false;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            try
            {
                await read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                completedWhilePaused = true;
            }
            catch (TimeoutException)
            {
            }
        }
        finally
        {
            version.SetValue(playback.Engine, original);
        }

        var result = await read.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(completedWhilePaused);
        Assert.Equal(previous, result);
    }

    [Fact]
    public void AudioTimeline_MetronomeWriterPaused_UsesAndThenRefreshesTheLastCompleteSnapshot()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        var timeline = new PlaybackTimeline(playback.Engine);
        var source = new PlaybackAudioClickSource(playback.Engine);
        Assert.True(timeline.TryGetPosition(Timestamp(Start + TimeSpan.FromMilliseconds(100)), out var before));
        Assert.True(source.TryGetClick(
            Start + TimeSpan.FromMilliseconds(490),
            Start + TimeSpan.FromMilliseconds(510),
            before.ClickGeneration,
            out _,
            out _));

        playback.Engine.Nudge(TimeSpan.FromMilliseconds(2));
        playback.PumpAt(1);
        var version = typeof(PlaybackEngine).GetField("_audioMetronomeVersion", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var original = Assert.IsType<int>(version.GetValue(playback.Engine));
        version.SetValue(playback.Engine, original | 1);
        try
        {
            Assert.True(timeline.TryGetPosition(Timestamp(Start + TimeSpan.FromMilliseconds(101)), out var during));
            Assert.Equal(0.101, during.SongSeconds, 6);
            Assert.True(source.TryGetClick(
                Start + TimeSpan.FromMilliseconds(990),
                Start + TimeSpan.FromMilliseconds(1010),
                during.ClickGeneration,
                out var click,
                out var ready));
            Assert.True(ready);
            Assert.Equal(Start + TimeSpan.FromMilliseconds(1000), click.Timestamp);
        }
        finally
        {
            version.SetValue(playback.Engine, original);
        }

        Assert.True(timeline.TryGetPosition(Timestamp(Start + TimeSpan.FromMilliseconds(102)), out var recovered));
        Assert.Equal(0.104, recovered.SongSeconds, 6);
    }

    [Fact]
    public void AudioClick_SeekDuringClickStartsTheNewDownbeatImmediately()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);

        using var engine = new AudioEngine(Rate, 480, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
        engine.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) };
        driver.Render(480, 480);

        playback.PumpAt(10);
        playback.Engine.Seek(Tick.Zero);
        playback.PumpAt(10);
        var afterSeek = driver.Render(480, 480);
        var expected = (float)(0.32 * Math.Pow(1 - (5.0 / (Rate * 0.04)), 2) * Math.Sin(2 * Math.PI * 1760 * 5 / Rate));

        Assert.Equal(expected, afterSeek[10], precision: 4);
    }

    [Fact]
    public void AudioClick_RetainsStartupWindowUntilMatchingSnapshotIsReady()
    {
        var startTimestamp = Timestamp(Start);
        var source = new DelayedClickSource(Start);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false, audioClick: source);
        engine.Transport.Follow(new ClockTimeline(startTimestamp, clickGeneration: 1), 0);
        var driver = new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = startTimestamp };

        driver.Render(Block, Block);
        var afterSnapshotReady = driver.Render(Block, Block);

        Assert.Contains(afterSnapshotReady[..64], sample => Math.Abs(sample) > 0.1f);
    }

    [Fact]
    public void AudioClickSource_PreservesSnapshotReadiness()
    {
        using var playback = new PlaybackFixture();
        var source = new PlaybackAudioClickSource(playback.Engine);

        var found = source.TryGetClick(Start, Start + TimeSpan.FromMilliseconds(10), clickGeneration: 1, out _, out var ready);

        Assert.False(found);
        Assert.False(ready);
    }

    [Fact]
    public void AudioClick_OverlappingClicksRenderTheSameAtAnyBlockSize()
    {
        using var playback = new PlaybackFixture();
        var tempo = TempoMap.Constant(Resolution, new Tempo(1));
        var meter = MeterMap.Constant(Resolution, TimeSignature.CommonTime);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Engine.Load(PlanDump.Compile(PlanDump.Routed(new Sequence(tempo, meter, [Track.Create("Timing")], []))));
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);

        float[] RenderWith(int block)
        {
            using var engine = new AudioEngine(8_000, block, streamFromDisk: false, audioClick: new PlaybackAudioClickSource(playback.Engine));
            engine.Transport.Follow(new PlaybackTimeline(playback.Engine), 0);
            return new OfflineAudioDriver(engine, serviceStreams: false) { StartTimestamp = Timestamp(Start) }.Render(4_096, block);
        }

        var small = RenderWith(64);
        var large = RenderWith(256);

        Assert.Equal(small.Length, large.Length);
        for (var i = 0; i < small.Length; i++)
        {
            Assert.True(Math.Abs(small[i] - large[i]) < 1e-4f, $"sample {i}: {small[i]} vs {large[i]}");
        }
    }

    [Fact]
    public void CountIn_AppliedLate_EndsFromTheAppliedStartWithoutAnEarlyBeat()
    {
        using var playback = new PlaybackFixture();
        var tempo = TempoMap.Constant(Resolution, Tempo.FromBeatsPerMinute(60));
        var meter = MeterMap.Constant(Resolution, TimeSignature.CommonTime);
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Engine.Load(PlanDump.Compile(PlanDump.Routed(new Sequence(tempo, meter, [Track.Create("Timing")], []))));
        var justAfterABeat = tempo.TickAt(TimeSpan.FromMilliseconds(1010));
        playback.Engine.Play(justAfterABeat, CountIn.Bars(tempo, meter, justAfterABeat, 1));
        playback.PumpAt(50);
        playback.PumpAt(4020);

        Assert.True(playback.Engine.IsCountingIn);
        Assert.False(((IAudioMetronome)playback.Engine).TryGetAudioClick(
            Start + TimeSpan.FromMilliseconds(3950),
            Start + TimeSpan.FromMilliseconds(4050),
            out _));
    }

    [Fact]
    public void AudioClick_SeekReschedulesAndStopSilences()
    {
        using var playback = new PlaybackFixture();
        playback.Engine.SetMetronome(null, enabled: true, audioClick: true);
        playback.Load();
        playback.Engine.Play(Tick.Zero);
        playback.PumpAt(0);
        playback.PumpAt(1200);
        playback.Engine.Seek(new Tick(1700));
        playback.PumpAt(1200);

        Assert.True(((IAudioMetronome)playback.Engine).TryGetAudioClick(
            Start + TimeSpan.FromMilliseconds(1200),
            Start + TimeSpan.FromMilliseconds(1600),
            out var click));
        Assert.Equal(Start + TimeSpan.FromMilliseconds(1500), click.Timestamp);

        playback.Engine.Stop();
        playback.PumpAt(1300);
        Assert.False(((IAudioMetronome)playback.Engine).TryGetAudioClick(
            Start + TimeSpan.FromMilliseconds(1300),
            Start + TimeSpan.FromMilliseconds(2000),
            out _));
    }

    private static void AssertClicks(IAudioMetronome metronome, TimeSpan from, TimeSpan until, params (double Milliseconds, bool Downbeat)[] expected)
    {
        var actual = new List<(double, bool)>();
        while (metronome.TryGetAudioClick(from, until, out var click))
        {
            actual.Add(((click.Timestamp - Start).TotalMilliseconds, click.Downbeat));
            from = click.Timestamp + TimeSpan.FromTicks(1);
        }

        Assert.Equal(expected, actual);
    }

    private static long Timestamp(TimeSpan time) => (long)(time.TotalSeconds * Stopwatch.Frequency);

    private sealed class ClockTimeline(long startTimestamp, long clickGeneration) : IAudioTimeline
    {
        public bool TryGetPosition(long timestamp, out TimelinePosition position)
        {
            position = new TimelinePosition(Stopwatch.GetElapsedTime(startTimestamp, timestamp).TotalSeconds, Tempo: 120, ClickGeneration: clickGeneration);
            return true;
        }
    }

    private sealed class DelayedClickSource(TimeSpan clickTimestamp) : IAudioClickSource
    {
        private bool _ready;

        public bool TryGetClick(TimeSpan from, TimeSpan until, long generation, out AudioClickEvent click, out bool ready)
        {
            if (!_ready)
            {
                _ready = true;
                click = default;
                ready = false;
                return false;
            }

            ready = true;
            click = new AudioClickEvent(clickTimestamp, Downbeat: true);
            return clickTimestamp >= from && clickTimestamp < until;
        }
    }

    private sealed class InterleavingTimeline(IAudioTimeline inner, Action afterFirstRead) : IAudioTimeline
    {
        private bool _changed;

        public bool TryGetPosition(long timestamp, out TimelinePosition position)
        {
            var playing = inner.TryGetPosition(timestamp, out position);
            if (!_changed)
            {
                _changed = true;
                afterFirstRead();
            }

            return playing;
        }
    }
}
