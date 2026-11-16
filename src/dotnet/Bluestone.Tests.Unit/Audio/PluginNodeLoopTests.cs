using System.Diagnostics;
using Bluestone.Audio;
using Bluestone.Audio.Plugins;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>
/// A plugin instrument in a one-bar loop that follows a timeline a few frames away from the audio (the render-ahead
/// estimate): every pass plays the loop's note exactly once, on time, and releases it before it starts again.
/// </summary>
public sealed class PluginNodeLoopTests
{
    private const int Rate = 48_000;
    private const int Block = 64;
    private const int Loop = 480;

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(-40)]
    public void ALoopFollowingAnOffsetTimeline_PlaysTheNoteOncePerPass(int offsetFrames)
    {
        var processor = new RecordingProcessor();
        var node = new PluginNode(processor, "Instrument", Block)
        {
            Schedule = new PluginSchedule(
                [new ScheduledEvent(0, PluginEvent.NoteOn(0, 0, 60, 100)), new ScheduledEvent(Loop, PluginEvent.NoteOff(0, 0, 60))],
                []),
        };
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        var builder = new AudioGraphBuilder();
        builder.ConnectToMaster(builder.AddNode(node));
        engine.Publish(builder.Build(Block, new MixerStripState()));
        var timeline = new LoopTimeline { LoopEnd = (double)Loop / Rate };
        engine.Transport.Follow(timeline, toleranceFrames: 48);
        var driver = new OfflineAudioDriver(engine);

        driver.Render(128, Block);
        timeline.Offset = (double)offsetFrames / Rate;
        driver.Render((5 * Loop) - 128 + Block, Block);

        // Output frames: on at the start of every pass, off at its end, the off before the next on.
        var expected = new List<(long, PluginEventKind)> { (0, PluginEventKind.NoteOn) };
        for (var pass = 1; pass <= 5; pass++)
        {
            expected.Add((pass * Loop, PluginEventKind.NoteOff));
            expected.Add((pass * Loop, PluginEventKind.NoteOn));
        }

        Assert.Equal(expected, processor.Notes);
    }

    // Song time = Offset + seconds since timestamp 0, wrapped in [0, LoopEnd) like the playback engine's.
    private sealed class LoopTimeline : IAudioTimeline
    {
        public double Offset { get; set; }

        public double LoopEnd { get; init; }

        public bool TryGetPosition(long timestamp, out TimelinePosition position)
        {
            var song = Offset + ((double)timestamp / Stopwatch.Frequency);
            if (song >= LoopEnd)
            {
                song %= LoopEnd;
            }

            position = new TimelinePosition(song, 0, LoopEnd);
            return true;
        }
    }

    private sealed class RecordingProcessor : IPluginProcessor
    {
        private long _frames;

        public event EventHandler<NotificationKind>? Changed
        {
            add { }
            remove { }
        }

        public List<(long Frame, PluginEventKind Kind)> Notes { get; } = [];

        public int MaxBlockFrames => Block;

        public int InputChannels => 0;

        public int OutputChannels => 2;

        public int SidechainChannels => 0;

        public int EventCapacity => 64;

        public int ReportedLatencyFrames => 0;

        public BlockOutcome ProcessBlock(int frames, ReadOnlySpan<float> input, Span<float> output, ReadOnlySpan<PluginEvent> inputEvents, Span<PluginEvent> outputEvents, in TransportState transport, ReadOnlySpan<float> sidechain = default)
        {
            foreach (var e in inputEvents)
            {
                if (e.Kind is PluginEventKind.NoteOn or PluginEventKind.NoteOff)
                {
                    Notes.Add((_frames + e.SampleOffset, e.Kind));
                }
            }

            _frames += frames;
            output.Clear();
            return new BlockOutcome(ProcessOutcome.Processed, 0, false);
        }

        public bool QueueParameterChange(uint parameterId, double normalizedValue, int sampleOffset) => true;
    }
}
