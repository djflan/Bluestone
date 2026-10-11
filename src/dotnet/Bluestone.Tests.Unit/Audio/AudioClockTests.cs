using System.Diagnostics;
using Bluestone.Application.Audio;
using Bluestone.Audio;
using Bluestone.Domain.Time;
using Bluestone.Playback;
using Bluestone.Tests.Unit.Playback;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>
/// One clock for MIDI and audio (ADR 0032): the audio engine follows the MIDI engine's transport for the moment each
/// block is heard, and the MIDI engine is nudged towards the audio device's clock. At 500 PPQN and 120 BPM one tick
/// is one millisecond.
/// </summary>
public sealed class AudioClockTests
{
    private const int Rate = 48_000;
    private const int Block = 64;

    [Fact]
    public void SongTime_FollowsTheAnchor_BetweenTicks()
    {
        using var fixture = new PlaybackFixture();
        fixture.Load(PlaybackFixture.Note(0, 10));
        fixture.Engine.Play(new Tick(1000));
        fixture.PumpAt(0);

        Assert.True(fixture.Engine.TryGetSongTimeAt(PlaybackFixture.Start + TimeSpan.FromMilliseconds(250.5), out var position));
        Assert.Equal(TimeSpan.FromMilliseconds(1250.5), position.SongTime);
        Assert.Equal(120, position.BeatsPerMinute, 6);
    }

    [Fact]
    public void SongTime_WrapsInTheLoop_AndIsFalseWhenStopped()
    {
        using var fixture = new PlaybackFixture();
        fixture.Load(PlaybackFixture.Note(0, 10));
        fixture.Engine.SetLoop(new LoopRegion(new Tick(100), new Tick(300)));
        fixture.Engine.Play(new Tick(0));
        fixture.PumpAt(0);

        Assert.True(fixture.Engine.TryGetSongTimeAt(PlaybackFixture.Start + TimeSpan.FromMilliseconds(350), out var position));
        Assert.Equal(TimeSpan.FromMilliseconds(150), position.SongTime);
        Assert.Equal((TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)), (position.LoopStart, position.LoopEnd));

        fixture.Engine.Stop();
        fixture.PumpAt(400);
        Assert.False(fixture.Engine.TryGetSongTimeAt(PlaybackFixture.Start + TimeSpan.FromMilliseconds(400), out _));
    }

    [Fact]
    public void Nudge_MovesTheSongPositionAtEveryLaterMoment()
    {
        using var fixture = new PlaybackFixture();
        fixture.Load(PlaybackFixture.Note(0, 10));
        fixture.Engine.Play(new Tick(0));
        fixture.PumpAt(0);

        fixture.Engine.Nudge(TimeSpan.FromMilliseconds(2));
        fixture.PumpAt(10);

        Assert.True(fixture.Engine.TryGetSongTimeAt(PlaybackFixture.Start + TimeSpan.FromMilliseconds(100), out var position));
        Assert.Equal(TimeSpan.FromMilliseconds(102), position.SongTime);
    }

    [Fact]
    public void Audio_PlaysTheSongPositionTheMidiEngineHasForTheMomentItIsHeard()
    {
        using var fixture = new PlaybackFixture();
        fixture.Load(PlaybackFixture.Note(0, 10));
        var (engine, recorder, driver) = Follow(fixture);

        // The first audio block is heard 50 ms after playback starts at tick 2000 (2 s).
        fixture.Engine.Play(new Tick(2000));
        fixture.PumpAt(0);
        driver.StartTimestamp = Timestamp(PlaybackFixture.Start + TimeSpan.FromMilliseconds(50));
        driver.Render(Block * 4, Block);

        Assert.Equal(98_400L, recorder.Contexts[0].SongFrame);
        Assert.True(recorder.Contexts[0].Jumped);
        Assert.Equal(98_400L + (3 * Block), recorder.Contexts[3].SongFrame);
        engine.Dispose();
    }

    [Fact]
    public void Audio_StopsWithTheMidiEngine()
    {
        using var fixture = new PlaybackFixture();
        fixture.Load(PlaybackFixture.Note(0, 10));
        var (engine, recorder, driver) = Follow(fixture);
        fixture.Engine.Play(new Tick(0));
        fixture.PumpAt(0);
        driver.StartTimestamp = Timestamp(PlaybackFixture.Start);
        driver.Render(Block * 2, Block);

        fixture.Engine.Stop();
        fixture.PumpAt(5);
        driver.Render(Block, Block);

        Assert.False(recorder.Contexts[^1].Playing);
        engine.Dispose();
    }

    [Fact]
    public void ADeviceClockFasterThanTheMidiClock_IsMeasured_AndANudgeBringsTheMidiEngineToIt()
    {
        using var fixture = new PlaybackFixture();
        fixture.Load(PlaybackFixture.Note(0, 10));
        var (engine, _, driver) = Follow(fixture);
        fixture.Engine.Play(new Tick(0));
        fixture.PumpAt(0);
        driver.StartTimestamp = Timestamp(PlaybackFixture.Start);

        // The device plays one second of audio in 0.999 s of the system clock (1000 ppm fast).
        var buffer = new float[Block * 2];
        for (var i = 0; i < Rate / Block; i++)
        {
            driver.StartTimestamp -= (long)(Stopwatch.Frequency * 0.001 * Block / Rate);
            driver.Render(buffer, Block);
        }

        var (ahead, callbacks) = engine.Transport.TakeDrift();
        Assert.True(callbacks > 100);
        Assert.InRange(ahead, 10, 40); // about 24 frames on average over a second that ends 48 frames ahead

        fixture.Engine.Nudge(TimeSpan.FromSeconds(48.0 / Rate));
        fixture.PumpAt(10);
        Assert.True(fixture.Engine.TryGetSongTimeAt(PlaybackFixture.Start + TimeSpan.FromMilliseconds(999), out var position));
        Assert.Equal(1.0, position.SongTime.TotalSeconds, 4);
        engine.Dispose();
    }

    private static long Timestamp(TimeSpan clock) => (long)(clock.Ticks * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));

    private static (AudioEngine Engine, Recorder Recorder, OfflineAudioDriver Driver) Follow(PlaybackFixture fixture)
    {
        var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var recorder = new Recorder();
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(recorder));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        engine.Transport.Follow(new PlaybackTimeline(fixture.Engine), toleranceFrames: Rate / 100);
        return (engine, recorder, new OfflineAudioDriver(engine));
    }

    private sealed class Recorder : AudioNode
    {
        public List<AudioRenderContext> Contexts { get; } = [];

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output) => Contexts.Add(context);
    }
}
