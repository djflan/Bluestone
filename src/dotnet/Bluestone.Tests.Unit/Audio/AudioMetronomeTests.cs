using System.Diagnostics;
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
}
