using Bluestone.Application.Audio;
using Bluestone.Audio;
using Bluestone.Audio.Clips;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;
using PluginEventKind = Bluestone.Plugins.Protocol.Exchange.PluginEventKind;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>A project's audio clips, mixer, and audio connections as a render graph, rendered offline.</summary>
public sealed class ProjectAudioGraphTests : IDisposable
{
    private const int Rate = 48_000;
    private const int Block = 256;

    // 480 PPQN at 120 BPM: 960 ticks per second, so tick 480 is frame 24 000.
    private static readonly Ppqn Resolution = new(480);
    private readonly string _directory = TestAudioFiles.Directory();
    private readonly double[] _tone = TestAudioFiles.Sine(Rate, 1, 1000, Rate, amplitude: 0.5);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AClipOnATrack_ThroughAMixerChannel_PlaysAtItsSongPosition_WithTheChannelsGain()
    {
        var (project, _, channel) = ProjectWithClip(start: 480, length: 480, offset: 0);
        project = project with { Mixer = project.Mixer.With(channel with { GainDecibels = -6.0206 }) };

        var output = Render(project, Rate * 3 / 2, out var result);

        Assert.Empty(result.Problems);
        Assert.All(Left(output)[..24_000], s => Assert.Equal(0f, s));
        for (var n = 24_000; n < 48_000; n += 101)
        {
            Assert.Equal(0.5 * _tone[n - 24_000], Left(output)[n], 4);
        }

        Assert.All(Left(output)[48_000..], s => Assert.Equal(0f, s));
    }

    [Fact]
    public void ATrimmedClip_StartsInsideItsSource()
    {
        // The clip's left edge moved 240 ticks (a quarter second) into the file.
        var (project, _, _) = ProjectWithClip(start: 480, length: 240, offset: 240);

        var output = Render(project, Rate, out _);

        Assert.Equal(_tone[12_000], Left(output)[24_000], 4);
        Assert.Equal(_tone[12_000 + 5000], Left(output)[29_000], 4);
    }

    [Fact]
    public void AMutedTrack_IsSilent_AndSoloOverridesMute()
    {
        var (project, track, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var muted = project with { Sequence = project.Sequence.WithTrack(track.WithMuted(true)) };
        Assert.All(Render(muted, Rate / 2, out _), s => Assert.Equal(0f, s));

        var other = Track.Create("Other", TrackRole.Audio);
        var soloedElsewhere = project with { Sequence = project.Sequence.WithTrack(other.WithSoloed(true)) };
        Assert.All(Render(soloedElsewhere, Rate / 2, out _), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void AChannelRoutedIntoAMutedBus_IsSilent_AndIntoAnOpenBus_IsHeard()
    {
        var (project, _, channel) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var bus = MixerChannel.Create("Bus");
        var routed = project with { Mixer = project.Mixer.With(bus with { IsMuted = true }).With(channel with { Output = bus.Id }) };
        Assert.All(Render(routed, Rate / 4, out _), s => Assert.Equal(0f, s));

        var open = routed with { Mixer = routed.Mixer.With(bus) };
        Assert.Contains(Render(open, Rate / 4, out _), s => s != 0f);
    }

    [Fact]
    public void ATrackWithoutAnAudioConnection_IsNotHeard()
    {
        var (project, _, _) = ProjectWithClip(start: 0, length: 480, offset: 0);

        Assert.All(Render(project with { Connections = [] }, Rate / 4, out _), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void ATrackConnectedStraightToTheMaster_IsHeard()
    {
        var (project, track, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var direct = project with { Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)] };

        Assert.Equal(_tone[100], Left(Render(direct, Rate / 4, out _))[100], 4);
    }

    [Fact]
    public void AMissingFile_IsAProblem_NotAFailure()
    {
        var (project, track, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        File.Delete(Path.Combine(_directory, "tone.wav"));

        var output = Render(project, Block * 4, out var result);

        Assert.Equal($"{track.Name}: tone.wav: the file is missing", Assert.Single(result.Problems));
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void AnEditKeepsTheClipNodeAndTheStripState_WhenTheyDidNotChange()
    {
        var (project, _, channel) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        var first = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        var strip = graphs.StripOf(channel.Id);

        var louder = project with { Mixer = project.Mixer.With(channel with { GainDecibels = 3 }) };
        var second = graphs.Build(louder, SignalGraph.Evaluate(louder, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);

        Assert.Same(strip, graphs.StripOf(channel.Id));
        Assert.Same(first.Graph.Nodes.Single(), second.Graph.Nodes.Single());

        // No engine served this node; the graph builder that opened it closes it (Windows cannot delete an open file).
        graphs.Dispose();
    }

    [Fact]
    public void Schedule_PlacesNotesOnSongFrames_ReleasesFirstAtOneFrame_AndConvertsAutomation()
    {
        var tempo = TempoMap.Constant(Resolution, Tempo.Default);
        var channel = MidiChannel.FromIndex(0);
        var first = new NoteEvent(new Tick(0), new TickSpan(480), channel, new NoteNumber(60), new Velocity(100));
        var second = new NoteEvent(new Tick(480), new TickSpan(240), channel, new NoteNumber(60), new Velocity(90));
        var change = new ParameterChange(new Tick(960), DeviceId.New(), new ParameterId(3), ControlValue.Max);

        var schedule = ProjectAudioGraph.Schedule([new SignalEvent(first, 0, 0), new SignalEvent(second, 0, 1)], [change], tempo, Rate);

        Assert.Equal(
            [(0L, PluginEventKind.NoteOn), (24_000, PluginEventKind.NoteOff), (24_000, PluginEventKind.NoteOn), (36_000, PluginEventKind.NoteOff)],
            schedule.Events.Select(e => (e.Frame, e.Event.Kind)));
        Assert.Equal(new ScheduledParameterChangeView(48_000, 3, 1.0), new ScheduledParameterChangeView(schedule.Parameters[0].Frame, schedule.Parameters[0].Parameter, schedule.Parameters[0].Value));
    }

    [Fact]
    public void APreFaderSendToAReturn_IsHeardAtTheSendLevel_WithTheChannelFaderDown()
    {
        var (project, _, channel) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var reverb = MixerChannel.Create("Reverb");
        project = project with { Mixer = project.Mixer.With(channel with { GainDecibels = -120, Sends = [new MixerSend(reverb.Id, -6.0206, preFader: true)] }).With(reverb) };

        var output = Render(project, Rate / 4, out var result);

        Assert.Empty(result.Problems);
        for (var n = 0; n < 12_000; n += 97)
        {
            Assert.Equal(0.5 * _tone[n], Left(output)[n], 4);
        }
    }

    [Fact]
    public void ASendLoopInAFile_IsLeftOut_WithAProblem_AndTheRestPlays()
    {
        var (project, _, channel) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var reverb = MixerChannel.Create("Reverb") with { Output = channel.Id };
        project = project with { Mixer = project.Mixer.With(channel.WithSend(new MixerSend(reverb.Id))).With(reverb) };

        var output = Render(project, Rate / 4, out var result);

        Assert.Equal("Vocal: a send would feed back on itself, so it is silent.", Assert.Single(result.Problems));
        Assert.Equal(_tone[100], Left(output)[100], 4);
    }

    [Fact]
    public void ClipGainAndFades_FromTheProject_AreMeasuredInTicks()
    {
        // A fade-in of 240 ticks is a quarter second (12 000 frames) at 120 BPM; the clip is 6 dB down.
        var (project, track, _) = ProjectWithClip(start: 0, length: 960, offset: 0);
        var clip = (AudioClip)track.Clips[0] with { GainDecibels = -6.0206, FadeIn = new TickSpan(240), FadeOut = new TickSpan(480), FadeShape = FadeShape.EqualPower };
        project = project with { Sequence = project.Sequence.WithTrack(track.WithClip(clip)) };

        var output = Left(Render(project, Rate, out _));

        Assert.Equal(0f, output[0]);
        Assert.Equal(0.5 * _tone[6000] * Math.Sin(0.5 * Math.PI / 2), output[6000], 4);
        Assert.Equal(0.5 * _tone[18_000], output[18_000], 4);
        Assert.Equal(0.5 * _tone[36_000] * Math.Sin(((47_999 - 36_000) / 24_000.0) * Math.PI / 2), output[36_000], 4);
    }

    [Fact]
    public void AGainChange_KeepsTheClipNode_AMove_ReplacesIt_AndTheReplacedNodeIsClosedAtTheNextBuild()
    {
        var (project, track, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        var first = Node(graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory));
        var clip = (AudioClip)track.Clips[0];

        var quieter = project with { Sequence = project.Sequence.WithTrack(track.WithClip(clip with { GainDecibels = -12 })) };
        Assert.Same(first, Node(graphs.Build(quieter, SignalGraph.Evaluate(quieter, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory)));

        var moved = project with { Sequence = project.Sequence.WithTrack(track.RemoveClip(clip.Id).WithClip(clip with { Start = new Tick(960) })) };
        var second = Node(graphs.Build(moved, SignalGraph.Evaluate(moved, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory));
        Assert.NotSame(first, second);
        Assert.False(first.IsClosed);

        graphs.Build(moved, SignalGraph.Evaluate(moved, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        Assert.True(first.IsClosed);
        Assert.False(second.IsClosed);

        graphs.Dispose();
        Assert.True(second.IsClosed);

        static ClipPlayerNode Node(ProjectAudioGraphResult result) => Assert.IsType<ClipPlayerNode>(result.Graph.Nodes.Single());
    }

    [Fact]
    public void ANodeTheEngineClosedWhileItsTrackWasMuted_IsOpenedAgainWhenTheTrackPlays()
    {
        var (project, _, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        var first = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        var node = Assert.IsType<ClipPlayerNode>(first.Graph.Nodes.Single());
        node.Dispose();

        var again = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);

        Assert.NotSame(node, again.Graph.Nodes.Single());
        Assert.False(((ClipPlayerNode)again.Graph.Nodes.Single()).IsClosed);
    }

    [Fact]
    public void ANodeReusedByANewBuild_IsNotClosedByADiskPassOverAnOlderGraph()
    {
        // A three-second clip: it streams past its one-second preload.
        var long_ = TestAudioFiles.Sine(Rate * 3, 1, 1000, Rate);
        TestAudioFiles.Wave(_directory, "long.wav", "float32", Rate, 1, long_);
        var (project, track, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var clip = new AudioClip(ClipId.New(), new Tick(0), new TickSpan(2880), new TickSpan(0), new AudioSource("long.wav", Rate, Rate * 3, 1));
        var playing = project with { Sequence = project.Sequence.WithTrack(track.RemoveClip(track.Clips[0].Id).WithClip(clip)) };
        var muted = playing with { Sequence = playing.Sequence.WithTrack(playing.Sequence.FindTrack(track.Id)!.WithMuted(true)) };
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(Build(playing).Graph);
        engine.ServiceStreams();
        var node = Assert.IsType<ClipPlayerNode>(engine.Graph.Nodes.Single());

        // Muted, then unmuted: the new build reuses the node. A disk pass that read the muted graph runs before the
        // new graph is published.
        engine.Publish(Build(muted).Graph);
        var unmuted = Build(playing).Graph;
        engine.ServiceStreams();
        engine.Publish(unmuted);
        engine.Transport.Play(0);
        var output = Left(new OfflineAudioDriver(engine).Render(Rate * 2, Block));

        Assert.Same(node, unmuted.Nodes.Single());
        Assert.False(node.IsClosed);
        Assert.Equal(0, node.UnderrunFrames);
        for (var n = 0; n < Rate * 2; n += 97)
        {
            Assert.Equal(long_[n], output[n], 6);
        }

        engine.Dispose();
        graphs.Dispose();
        Assert.True(node.IsClosed);

        ProjectAudioGraphResult Build(Project p) => graphs.Build(p, SignalGraph.Evaluate(p, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
    }

    private static float[] Left(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 == 0)];

    private float[] Render(Project project, int frames, out ProjectAudioGraphResult result)
    {
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        result = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(result.Graph);
        engine.Transport.Play(0);
        return new OfflineAudioDriver(engine).Render(frames, Block);
    }

    // A one-second 1 kHz tone file; an audio track holding a clip of it; the track → a mixer channel → the master.
    private (Project Project, Track Track, MixerChannel Channel) ProjectWithClip(long start, long length, long offset)
    {
        TestAudioFiles.Wave(_directory, "tone.wav", "float32", Rate, 1, _tone);
        var clip = new AudioClip(ClipId.New(), new Tick(start), new TickSpan(length), new TickSpan(offset), new AudioSource("tone.wav", Rate, Rate, 1));
        var track = Track.Create("Vocal", TrackRole.Audio).WithClip(clip);
        var channel = MixerChannel.Create("Vocal");
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Resolution).WithTrack(track),
            Mixer = Mixer.Empty.With(channel),
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Mixer(channel.Id))],
        };
        return (project, track, channel);
    }

    private readonly record struct ScheduledParameterChangeView(long Frame, uint Parameter, double Value);
}
