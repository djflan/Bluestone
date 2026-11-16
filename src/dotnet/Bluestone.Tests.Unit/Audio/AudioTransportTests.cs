using System.Diagnostics;
using Bluestone.Audio;

namespace Bluestone.Tests.Unit.Audio;

public sealed class AudioTransportTests
{
    private const int Rate = 48_000;
    private const int Block = 64;

    [Fact]
    public void Play_StartsAtTheLocatedFrame_AndAdvancesByEveryBlock_FlaggingOnlyTheStart()
    {
        var (engine, recorder, driver) = Setup();
        engine.Transport.Play(1000);

        driver.Render(200, 100);

        // Blocks are always 64 frames; callbacks of 100 are served from them, so four blocks cover 200 frames.
        Assert.Equal([(1000L, 64, true), (1064, 64, false), (1128, 64, false), (1192, 64, false)], recorder.Blocks);
        Assert.Equal(1256, engine.Transport.Position);
        Assert.True(engine.Transport.IsPlaying);
    }

    [Fact]
    public void Loop_WrapsInsideTheBlock_AndTheNextBlockContinuesAfterTheWrap()
    {
        var (engine, recorder, driver) = Setup();
        engine.Transport.SetLoop(100, 150);
        engine.Transport.Play(120);

        driver.Render(128, 64);

        var first = recorder.Contexts[0];
        Assert.True(first.Wraps);
        Assert.Equal((120L, 30, 100L, 30), (first.SongFrame, first.WrapOffset, first.WrapFrame, first.FirstLength));
        Assert.Equal(134, first.EndFrame);
        Assert.Equal((134L, false), (recorder.Contexts[1].SongFrame, recorder.Contexts[1].Jumped));
    }

    [Fact]
    public void Loop_ShorterThanABlock_StaysInsideTheLoop()
    {
        var (engine, recorder, driver) = Setup();
        engine.Transport.SetLoop(100, 110);
        engine.Transport.Play(100);

        driver.Render(256, 64);

        Assert.All(recorder.Contexts, c => Assert.InRange(c.SongFrame, 100, 109));
    }

    [Fact]
    public void Loop_DoesNotEngage_WhenPlayingStartsAfterItsEnd()
    {
        var (engine, recorder, driver) = Setup();
        engine.Transport.SetLoop(100, 150);
        engine.Transport.Play(200);

        driver.Render(64, 64);

        Assert.Equal([(200L, 64, true)], recorder.Blocks);
    }

    [Fact]
    public void Stop_IsAJump_AndTheStoppedPositionStays()
    {
        var (engine, recorder, driver) = Setup();
        engine.Transport.Play(0);
        driver.Render(64, 64);
        engine.Transport.Stop();
        driver.Render(128, 64);

        Assert.Equal([(0L, 64, true, true), (64, 64, false, true), (64, 64, false, false)], recorder.Contexts.Select(c => (c.SongFrame, c.Frames, c.Playing, c.Jumped)));
    }

    [Fact]
    public void ALocateAndALoopChange_BeforeTheNextBlock_BothApply()
    {
        var (engine, recorder, driver) = Setup();
        engine.Transport.Play(0);
        driver.Render(64, 64);
        engine.Transport.Locate(5000);
        engine.Transport.SetLoop(0, 10_000);
        driver.Render(64, 64);

        Assert.Equal((5000L, 64, true), recorder.Blocks[^1]);
    }

    [Fact]
    public void Follow_StartsAtTheTimelinesPosition_ForTheMomentTheBlockIsHeard()
    {
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true, Origin = 0, SongAtOrigin = 2.0 };
        driver.StartTimestamp = Stopwatch.Frequency; // the first block is heard 1 s after the timeline's origin
        engine.Transport.Follow(timeline, toleranceFrames: 480);

        driver.Render(64, 64);

        Assert.Equal((3 * Rate, 64, true), recorder.Blocks[0]);
    }

    [Fact]
    public void Follow_KeepsItsOwnContinuousPosition_WithinTheTolerance_AndReportsTheDrift()
    {
        var (engine, recorder, driver) = Setup();
        // The timeline runs 100 ppm slower than the device: audio drifts ahead by 4.8 frames per second.
        var timeline = new FakeTimeline { Playing = true, Origin = 0, SongAtOrigin = 0, Rate = 0.9999 };
        engine.Transport.Follow(timeline, toleranceFrames: 480);

        driver.Render(Rate, 64);

        Assert.All(recorder.Blocks.Skip(1), b => Assert.False(b.Jumped));
        Assert.Equal(Rate - 64, recorder.Blocks[^1].Frame);
        var (average, callbacks) = engine.Transport.TakeDrift();
        Assert.Equal(Rate / 64 - 1, callbacks);
        Assert.InRange(average, 2.0, 3.0);
        Assert.Equal((0, 0), engine.Transport.TakeDrift());
    }

    [Fact]
    public void Follow_Relocates_WhenTheTimelineJumpsBeyondTheTolerance()
    {
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true };
        engine.Transport.Follow(timeline, toleranceFrames: 480);
        driver.Render(128, 64);

        timeline.SongAtOrigin = 10;
        driver.Render(64, 64);

        Assert.Equal((10 * Rate + 128, 64, true), recorder.Blocks[^1]);
    }

    [Fact]
    public void Follow_Stops_WhenTheTimelineStops()
    {
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true };
        engine.Transport.Follow(timeline, toleranceFrames: 480);
        driver.Render(64, 64);
        timeline.Playing = false;
        driver.Render(64, 64);

        Assert.False(recorder.Contexts[^1].Playing);
        Assert.True(recorder.Contexts[^1].Jumped);
        Assert.False(engine.Transport.IsPlaying);
    }

    [Fact]
    public void Follow_WrapsAtTheTimelinesLoop_WithoutRelocatingAfterwards()
    {
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true, LoopStart = 0, LoopEnd = 0.01 };
        engine.Transport.Follow(timeline, toleranceFrames: 48);

        driver.Render(1024, 64);

        // 480 frames per pass: the block at 448 wraps after 32 frames and the next starts at 32; the second pass ends
        // exactly on a block boundary. The only jumps are the start and that boundary wrap; none is a relocation.
        Assert.Contains(recorder.Contexts, c => c.SongFrame == 448 && c.WrapOffset == 32 && c.WrapFrame == 0);
        Assert.Contains((32L, 64, false), recorder.Blocks);
        Assert.Equal(2, recorder.Blocks.Count(b => b.Jumped));
        Assert.All(recorder.Blocks.Where(b => b.Jumped), b => Assert.Equal(0, b.Frame));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(-40)]
    public void Follow_ATimelineAFewFramesAwayAcrossTheWrap_DoesNotRelocate(int offsetFrames)
    {
        // The timeline's position is already wrapped; when a block starts within the offset of the loop end, the audio
        // and the timeline sit on opposite sides of the wrap, a loop length apart in raw frames but only the offset apart
        // in the loop.
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true, LoopStart = 0, LoopEnd = 0.01 };
        engine.Transport.Follow(timeline, toleranceFrames: 48);
        driver.Render(128, 64);

        timeline.SongAtOrigin = (double)offsetFrames / Rate;
        engine.Transport.TakeDrift();
        driver.Render(1024 * 2, 64);

        var blocks = recorder.Contexts.Where(c => c.Playing).ToList();
        for (var i = 1; i < blocks.Count; i++)
        {
            var end = blocks[i - 1].EndFrame;
            Assert.Equal(end >= 480 ? end - 480 : end, blocks[i].SongFrame);
        }

        Assert.All(blocks.Where(b => b.Jumped), b => Assert.Equal(0, b.SongFrame));
        // The drift is measured in the loop too: the audio is the offset behind (or ahead of) the timeline throughout.
        Assert.Equal(-offsetFrames, engine.Transport.TakeDrift().AverageFrames, 0);
    }

    [Fact]
    public void Follow_Relocates_WhenTheTimelineJumpsWithinTheLoop()
    {
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true, LoopStart = 0, LoopEnd = 0.01 };
        engine.Transport.Follow(timeline, toleranceFrames: 48);
        driver.Render(128, 64);

        // A seek from frame 128 to 328, both inside the loop.
        timeline.SongAtOrigin = 200.0 / Rate;
        driver.Render(64, 64);

        Assert.Equal((328L, 64, true), recorder.Blocks[^1]);
    }

    [Fact]
    public void Follow_ATimelineThatRoundsToTheLoopEnd_IsTheWrap_AndDoesNotRelocatePastTheEnd()
    {
        var (engine, recorder, driver) = Setup();
        // A standing timeline: the first block starts at 428 and wraps, so the audio is at 12.
        var timeline = new FakeTimeline { Playing = true, Rate = 0, SongAtOrigin = 428.0 / Rate, LoopStart = 0, LoopEnd = 0.01 };
        engine.Transport.Follow(timeline, toleranceFrames: 48);
        driver.Render(64, 64);

        // 479.70 frames is inside the loop, but it rounds to frame 480, the loop end: that is frame 0, 12 from the audio.
        timeline.SongAtOrigin = 479.70 / Rate;
        driver.Render(64, 64);

        Assert.Equal([(428L, 64, true), (12, 64, false)], recorder.Blocks);
    }

    [Fact]
    public void Follow_Relocates_WhenTheTimelineJumpsWithinALoopNoLongerThanTwiceTheTolerance()
    {
        var (engine, recorder, driver) = Setup();
        var timeline = new FakeTimeline { Playing = true, Rate = 0, LoopStart = 0, LoopEnd = 80.0 / Rate };
        engine.Transport.Follow(timeline, toleranceFrames: 48);
        driver.Render(64, 64);

        // A seek from frame 64 to 4: 60 frames, but only 20 around the 80-frame loop.
        timeline.SongAtOrigin = 4.0 / Rate;
        driver.Render(64, 64);

        Assert.Equal((4L, 64, true), recorder.Blocks[^1]);
    }

    private static (AudioEngine Engine, RecordingNode Recorder, OfflineAudioDriver Driver) Setup()
    {
        var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var recorder = new RecordingNode();
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(recorder));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        return (engine, recorder, new OfflineAudioDriver(engine));
    }

    private sealed class RecordingNode : AudioNode
    {
        public List<AudioRenderContext> Contexts { get; } = [];

        public List<(long Frame, int Frames, bool Jumped)> Blocks => [.. Contexts.Where(c => c.Playing).Select(c => (c.SongFrame, c.Frames, c.Jumped))];

        public override void Render(in AudioRenderContext context, StereoBuffer input, StereoBuffer output) => Contexts.Add(context);
    }

    // A timeline whose song time is SongAtOrigin + (t - Origin) * Rate, wrapped in a loop like the playback engine's.
    private sealed class FakeTimeline : IAudioTimeline
    {
        public bool Playing { get; set; }

        public long Origin { get; set; }

        public double SongAtOrigin { get; set; }

        public double Rate { get; set; } = 1;

        public double LoopStart { get; set; }

        public double LoopEnd { get; set; }

        public bool TryGetPosition(long timestamp, out TimelinePosition position)
        {
            var song = SongAtOrigin + ((double)(timestamp - Origin) / Stopwatch.Frequency * Rate);
            if (LoopEnd > LoopStart && song >= LoopEnd)
            {
                song = LoopStart + ((song - LoopEnd) % (LoopEnd - LoopStart));
            }

            position = new TimelinePosition(song, LoopStart, LoopEnd);
            return Playing;
        }
    }
}
