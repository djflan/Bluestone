using Bluestone.Application.Audio;
using Bluestone.Audio;
using Bluestone.Audio.Clips;
using Bluestone.Audio.Plugins;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Signal;
using CsCheck;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>
/// The project's audio graph described as plain data, written as an image, read back, and realized elsewhere (ADR 0044)
/// must render exactly what the graph built in one place renders, and a damaged image must be refused.
/// </summary>
public sealed class AudioGraphImageTests : IDisposable
{
    private const int Rate = 48_000;
    private const int Block = 256;
    private static readonly Ppqn Resolution = new(480);
    private readonly string _directory = TestAudioFiles.Directory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AGraphRealizedFromItsImage_RendersExactlyWhatTheProjectsGraphRenders()
    {
        var project = RichProject();
        var signal = SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn);

        using var direct = new ProjectAudioGraph(Rate, Block, _ => null);
        var built = direct.Build(project, signal, DeviceCatalog.BuiltIn, _directory);
        var expected = Render(built.Graph, Rate * 3);

        using var describing = new ProjectAudioGraph(Rate, Block, _ => null);
        var (description, problems) = describing.Describe(project, signal, DeviceCatalog.BuiltIn, _directory);
        using var realizer = new AudioGraphRealizer(Rate, Block, _ => null);
        var realized = realizer.Realize(AudioGraphImage.Read(AudioGraphImage.ToArray(description)));
        var actual = Render(realized.Graph, Rate * 3);

        Assert.Empty(built.Problems);
        Assert.Empty(problems);
        Assert.Empty(realized.Problems);
        Assert.Equal(built.Graph.LatencyFrames, realized.Graph.LatencyFrames);
        Assert.Contains(expected, s => s != 0f);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ADescription_RoundTrips_WithPluginSchedulesAndEveryKindOfStep()
    {
        var description = RichDescription();

        var image = AudioGraphImage.ToArray(description);
        var read = AudioGraphImage.Read(image);

        AssertSame(description, read);
        Assert.Equal(image, AudioGraphImage.ToArray(read));
        Assert.Equal(image.Length, AudioGraphImage.Measure(description));
    }

    [Fact]
    public void AnEmptyDescription_RoundTrips()
    {
        var read = AudioGraphImage.Read(AudioGraphImage.ToArray(AudioGraphDescription.Empty));

        AssertSame(AudioGraphDescription.Empty, read);
    }

    [Fact]
    public void Write_RefusesADestinationThatIsTooSmall()
    {
        var description = RichDescription();

        Assert.Throws<ArgumentException>(() => AudioGraphImage.Write(description, new byte[AudioGraphImage.Measure(description) - 1]));
    }

    [Fact]
    public void DamagedOrForeignImages_AreRefused()
    {
        var image = AudioGraphImage.ToArray(RichDescription());

        Assert.Throws<InvalidDataException>(() => AudioGraphImage.Read(image.AsSpan(0, 100)));
        Assert.Throws<InvalidDataException>(() => AudioGraphImage.Read(With(image, 0, 0)));
        Assert.Throws<InvalidDataException>(() => AudioGraphImage.Read(WithInt(image, 4, 2)));
        Assert.Throws<InvalidDataException>(() => AudioGraphImage.Read(image.AsSpan(0, image.Length - 8)));
        Assert.Throws<InvalidDataException>(() => AudioGraphImage.Read(WithDouble(image, 120, double.NaN)));
    }

    [Fact]
    public void AnyCorruptedByte_IsEitherReadOrRefused_NeverAnotherFailure()
    {
        var image = AudioGraphImage.ToArray(RichDescription());
        Gen.Select(Gen.Int[0, image.Length - 1], Gen.Byte).Sample((at, value) =>
        {
            try
            {
                _ = AudioGraphImage.Read(With(image, at, value));
            }
            catch (InvalidDataException)
            {
            }
        }, iter: 5_000);
    }

    // Two audio tracks: a trimmed clip with gain and equal-power fades into a channel that goes through a bus, and a
    // looping clip into a channel with a pre-fader send to a return; one more track is muted.
    private Project RichProject()
    {
        var tone = TestAudioFiles.Sine(Rate, 1, 440, Rate, amplitude: 0.5);
        var other = TestAudioFiles.Sine(Rate, 1, 660, Rate / 2, amplitude: 0.4);
        TestAudioFiles.Wave(_directory, "tone.wav", "float32", Rate, 1, tone);
        TestAudioFiles.Wave(_directory, "other.wav", "pcm24", Rate, 1, other);
        var faded = new AudioClip(ClipId.New(), new Tick(240), new TickSpan(720), new TickSpan(120), new AudioSource("tone.wav", Rate, Rate, 1))
        {
            GainDecibels = -3,
            FadeIn = new TickSpan(120),
            FadeOut = new TickSpan(240),
            FadeShape = FadeShape.EqualPower,
        };
        var looped = new AudioClip(ClipId.New(), new Tick(0), new TickSpan(1_920), TickSpan.Zero, new AudioSource("other.wav", Rate / 2, Rate, 1))
        {
            Loop = new ClipLoop(new Tick(0), new TickSpan(300)),
        };
        var vocal = Track.Create("Vocal", TrackRole.Audio).WithClip(faded);
        var pad = Track.Create("Pad", TrackRole.Audio).WithClip(looped);
        var silent = Track.Create("Muted", TrackRole.Audio).WithClip(faded with { Id = ClipId.New() }).WithMuted(true);
        var bus = MixerChannel.Create("Bus") with { GainDecibels = -2, Pan = 0.25 };
        var reverb = MixerChannel.Create("Return") with { GainDecibels = -6 };
        var vocalChannel = MixerChannel.Create("Vocal") with { Output = bus.Id, Pan = -0.5 };
        var padChannel = MixerChannel.Create("Pad") with { GainDecibels = -40, Sends = [new MixerSend(reverb.Id, -3, preFader: true)] };
        var mutedChannel = MixerChannel.Create("Muted");
        return Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Resolution).WithTrack(vocal).WithTrack(pad).WithTrack(silent),
            Mixer = Mixer.Empty.With(bus).With(reverb).With(vocalChannel).With(padChannel).With(mutedChannel) with { MasterGainDecibels = -1 },
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Mixer(vocalChannel.Id)),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(pad.Id), SignalNode.Mixer(padChannel.Id)),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(silent.Id), SignalNode.Mixer(mutedChannel.Id)),
            ],
        };
    }

    // Every kind of node and step, a plugin schedule with notes, a pitch bend, SysEx, and parameter changes, a looped
    // clip, and settings that differ from the defaults.
    private static AudioGraphDescription RichDescription()
    {
        var builder = new AudioGraphDescriptionBuilder();
        var channel = builder.AddStrip(new DescribedStrip(Guid.NewGuid(), new MixerStripSettings(-6, 0.5, Muted: false, Soloed: true)));
        var bus = builder.AddStrip(new DescribedStrip(Guid.NewGuid(), new MixerStripSettings(-1.5, -0.25, Muted: true)));
        var send = builder.AddStrip(new DescribedStrip(Guid.NewGuid(), default));
        builder.RouteStrip(channel, bus);
        builder.SendStrip(channel, send, -12, preFader: true);
        builder.SendStrip(bus, send, -3.5, preFader: false);
        PluginEvent.TryCreateSystemExclusive(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7], out var reset);
        var schedule = new PluginSchedule(
            [
                new(0, reset),
                new(100, PluginEvent.NoteOn(0, 0, 60, 100)),
                new(200, PluginEvent.PitchBend(0, 0, 10_000)),
                new(300, PluginEvent.NoteOff(0, 0, 60, 64)),
                new(300, PluginEvent.ControlChange(0, 3, 74, 90)),
                new(400, PluginEvent.ProgramChange(0, 15, 12)),
            ],
            [new(50, 7, 0.25), new(450, 7, 0.75)]);
        var synth = builder.AddNode(new DescribedNode(DescribedNodeKind.Plugin, Guid.NewGuid(), "Sÿnth ♪") { Schedule = schedule });
        var clips = builder.AddNode(new DescribedNode(DescribedNodeKind.Clips, Guid.NewGuid(), "Drums")
        {
            Regions =
            [
                new AudioClipRegion("/audio/drums.wav", 1_000, 96_000, 0.5) { Envelope = new AudioClipEnvelope(0.5f, 480, 960, EqualPower: true) },
                new AudioClipRegion("/audio/drums.wav", 100_000, 200_000, 0) { LoopStartSeconds = 0.25, LoopLengthSeconds = 1.5 },
            ],
        });
        var effect = builder.AddNode(new DescribedNode(DescribedNodeKind.Plugin, Guid.NewGuid(), "Verb"));
        builder.Monitor(synth);
        builder.Connect(synth, effect);
        builder.Connect(clips, effect);
        builder.ConnectToStrip(effect, channel);
        builder.ConnectToMaster(clips);
        return builder.Build(-2.5, [Guid.NewGuid(), Guid.NewGuid()]);
    }

    private static void AssertSame(AudioGraphDescription expected, AudioGraphDescription actual)
    {
        Assert.Equal(expected.MasterGainDecibels, actual.MasterGainDecibels);
        Assert.Equal(expected.Strips, actual.Strips);
        Assert.Equal(expected.Steps, actual.Steps);
        Assert.True(expected.Retained.SetEquals(actual.Retained));
        Assert.Equal(expected.Nodes.Length, actual.Nodes.Length);
        foreach (var (e, a) in expected.Nodes.Zip(actual.Nodes))
        {
            Assert.Equal((e.Kind, e.Key, e.Name), (a.Kind, a.Key, a.Name));
            Assert.Equal(e.Regions, a.Regions);
            Assert.True(e.Schedule.HasSameContent(a.Schedule), $"{e.Name}'s schedule differs.");
        }
    }

    private static float[] Render(AudioGraph graph, int frames)
    {
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(graph);
        engine.Transport.Play(0);
        return new OfflineAudioDriver(engine).Render(frames, Block);
    }

    private static byte[] With(byte[] image, int at, byte value)
    {
        var copy = (byte[])image.Clone();
        copy[at] = value;
        return copy;
    }

    private static byte[] WithInt(byte[] image, int at, int value)
    {
        var copy = (byte[])image.Clone();
        BitConverter.TryWriteBytes(copy.AsSpan(at), value);
        return copy;
    }

    private static byte[] WithDouble(byte[] image, int at, double value)
    {
        var copy = (byte[])image.Clone();
        BitConverter.TryWriteBytes(copy.AsSpan(at), value);
        return copy;
    }
}
