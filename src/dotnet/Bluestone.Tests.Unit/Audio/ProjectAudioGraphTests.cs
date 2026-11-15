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

/// <summary>A project's audio clips, track-owned strips, and routes as a render graph, rendered offline.</summary>
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
    public void AClipOnATrack_ThroughItsAudioStrip_PlaysAtItsSongPosition_WithTheStripsGain()
    {
        var (project, track) = ProjectWithClip(start: 480, length: 480, offset: 0);
        track = track.WithAudioStrip(new AudioStrip { GainDecibels = -6.0206 });
        project = project with { Sequence = project.Sequence.WithTrack(track) };

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
        var (project, _) = ProjectWithClip(start: 480, length: 240, offset: 240);

        var output = Render(project, Rate, out _);

        Assert.Equal(_tone[12_000], Left(output)[24_000], 4);
        Assert.Equal(_tone[12_000 + 5000], Left(output)[29_000], 4);
    }

    [Fact]
    public void AMutedTrack_IsSilent_AndSoloOverridesMute()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var muted = project with { Sequence = project.Sequence.WithTrack(track.WithMuted(true)) };
        Assert.All(Render(muted, Rate / 2, out _), s => Assert.Equal(0f, s));

        var other = Track.Create("Other", TrackRole.Audio);
        var soloedElsewhere = project with { Sequence = project.Sequence.WithTrack(other.WithSoloed(true)) };
        Assert.All(Render(soloedElsewhere, Rate / 2, out _), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void ATrackRoutedIntoAMutedGroup_IsSilent_AndIntoAnOpenGroup_IsHeard()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var bus = Track.Create("Bus", TrackRole.Group).WithAudioStrip(new AudioStrip { IsMuted = true });
        track = track.WithGroup(bus.Id);
        var routed = project with
        {
            Sequence = project.Sequence.WithTrack(track).WithTrack(bus),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Track(bus.Id)),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(bus.Id), SignalNode.Master),
            ],
        };
        Assert.All(Render(routed, Rate / 4, out _), s => Assert.Equal(0f, s));

        var open = routed with { Sequence = routed.Sequence.WithTrack(bus.WithAudioStrip(AudioStrip.Default)) };
        Assert.Contains(Render(open, Rate / 4, out _), s => s != 0f);
    }

    [Fact]
    public void ATrackWithoutAnAudioConnection_IsNotHeard()
    {
        var (project, _) = ProjectWithClip(start: 0, length: 480, offset: 0);

        Assert.All(Render(project with { Connections = [] }, Rate / 4, out _), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void ATrackConnectedStraightToTheMaster_IsHeard()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var direct = project with { Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)] };

        Assert.Equal(_tone[100], Left(Render(direct, Rate / 4, out _))[100], 4);
    }

    [Fact]
    public void SoloingAnEffectTrack_KeepsItsIncomingSendAudible_WithoutTheSenderDryOutput()
    {
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var effect = Track.Create("Reverb", TrackRole.Effect)
            .WithAudioStrip(new AudioStrip { IsSoloed = true });
        var receiver = AudioReceiver(0);
        var chain = DeviceChain.Create(ChainOwner.ForTrack(effect.Id)) with { Devices = [receiver] };
        project = project with
        {
            Sequence = project.Sequence.WithTrack(vocal).WithTrack(effect),
            Chains = [chain],
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Track(effect.Id)) with { AudioPurpose = AudioRoutePurpose.Send },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(effect.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(chain.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
            ],
        };

        var output = Left(Render(project, Rate / 4, out var result));

        Assert.Empty(result.Problems);
        Assert.Equal(_tone[100], output[100], 4);
    }

    [Fact]
    public void AnInternalTrackOutputToMaster_IsAssociatedWithAValidationError()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var invalid = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master) with
        {
            AudioPurpose = AudioRoutePurpose.Internal,
        };
        project = project with { Connections = [invalid] };

        var errors = SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(project, DeviceCatalog.BuiltIn.Lookup));

        Assert.Contains(errors, issue => issue.Connections.Contains(invalid.Id));
    }

    [Fact]
    public void AnInternalTrackOutputToMaster_DoesNotBypassTheTrackStrip()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        track = track.WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var invalid = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master) with
        {
            AudioPurpose = AudioRoutePurpose.Internal,
        };
        project = project with
        {
            Sequence = project.Sequence.WithTrack(track),
            Connections = [invalid],
        };

        var output = Render(project, Rate / 4, out var result);

        Assert.NotEmpty(result.Problems);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void TrackOutputAndSend_UseTrackStripsAndChains_ThenTheMasterChainFeedsMasterControls()
    {
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        vocal = vocal.WithAudioStrip(new AudioStrip { GainDecibels = -3, Pan = -0.25 });
        var effect = Track.Create("Reverb", TrackRole.Effect)
            .WithAudioStrip(new AudioStrip { GainDecibels = -6, Pan = 0.25 });

        var vocalReceiver = AudioReceiver(-3);
        var effectReceiver = AudioReceiver(-6);
        var masterReceiver = AudioReceiver(-9);
        var vocalChain = DeviceChain.Create(ChainOwner.ForTrack(vocal.Id)) with { Devices = [vocalReceiver] };
        var effectChain = DeviceChain.Create(ChainOwner.ForTrack(effect.Id)) with { Devices = [effectReceiver] };
        var masterChain = DeviceChain.Create(ChainOwner.Master) with { Devices = [masterReceiver] };
        var projectWithRoutes = project with
        {
            Sequence = project.Sequence.WithTrack(vocal).WithTrack(effect),
            Chains = [vocalChain, effectChain, masterChain],
            MasterStrip = new AudioStrip { GainDecibels = -1.5, Pan = 0.5 },
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Track(effect.Id)) with
                {
                    AudioPurpose = AudioRoutePurpose.Send,
                    SendLevelDecibels = -12,
                    SendPreFader = true,
                },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(effect.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(vocalChain.Id), SignalNode.DeviceInput(vocalReceiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(effectChain.Id), SignalNode.DeviceInput(effectReceiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(masterChain.Id), SignalNode.DeviceInput(masterReceiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
            ],
        };
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);

        var (description, problems) = graphs.Describe(
            projectWithRoutes,
            SignalGraph.Evaluate(projectWithRoutes, DeviceCatalog.BuiltIn),
            DeviceCatalog.BuiltIn,
            _directory);

        Assert.Empty(problems);
        var vocalStrip = Assert.Single(description.Strips, strip => strip.Key == vocal.Id.Value);
        var effectStrip = Assert.Single(description.Strips, strip => strip.Key == effect.Id.Value);
        var vocalStripIndex = description.Strips.IndexOf(vocalStrip);
        var effectStripIndex = description.Strips.IndexOf(effectStrip);
        var masterInput = Assert.Single(description.Nodes.Select((node, index) => (node, index)),
            item => item.node.Name == "Master input");
        var effectInput = Assert.Single(description.Nodes.Select((node, index) => (node, index)),
            item => item.node.Name == effect.Name && item.node.Kind == DescribedNodeKind.Gain);

        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.RouteStripToNode && step.A == vocalStripIndex && step.B == masterInput.index);
        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.SendStripToNode && step.A == vocalStripIndex && step.B == effectInput.index);
        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.RouteStripToNode && step.A == effectStripIndex && step.B == masterInput.index);
        var effectReceiverNode = Assert.Single(description.Nodes.Select((node, index) => (node, index)),
            item => item.node.Kind == DescribedNodeKind.Gain && Math.Abs(item.node.Gain - Math.Pow(10, -6 / 20d)) < 0.0001f);
        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.ConnectToStrip && step.A == effectReceiverNode.index && step.B == effectStripIndex);

        var vocalReceiverNode = Assert.Single(description.Nodes.Select((node, index) => (node, index)),
            item => item.node.Kind == DescribedNodeKind.Gain && Math.Abs(item.node.Gain - Math.Pow(10, -3 / 20d)) < 0.0001f);
        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.ConnectToStrip && step.A == vocalReceiverNode.index && step.B == vocalStripIndex);
        var masterReceiverNode = Assert.Single(description.Nodes.Select((node, index) => (node, index)),
            item => item.node.Kind == DescribedNodeKind.Gain && Math.Abs(item.node.Gain - Math.Pow(10, -9 / 20d)) < 0.0001f);
        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.Connect && step.A == masterInput.index && step.B == masterReceiverNode.index);
        Assert.Contains(description.Steps, step =>
            step.Kind == DescribedStepKind.ConnectToMaster && step.A == masterReceiverNode.index);
        Assert.Equal((-1.5, 0.5), (description.MasterGainDecibels, description.MasterPan));
    }

    [Fact]
    public void TrackAndMasterFaderPanAutomation_IsDescribedAtSongSampleFrames()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 960, offset: 0);
        var points = new[]
        {
            new AutomationPoint(Tick.Zero, ControlValue.FromFraction(5.0 / 6)),
            new AutomationPoint(new Tick(480), ControlValue.Max, AutomationCurve.Hold),
        };
        var trackGain = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForAudioStripGain(), points);
        var trackPan = Lane(AutomationTarget.ForAudioStripPan(), ControlValue.Min, ControlValue.Max);
        track = track.WithAutomation([trackGain, trackPan]);
        var masterGain = Lane(AutomationTarget.ForMasterGain(), ControlValue.Max, ControlValue.FromFraction(5.0 / 6));
        var masterPan = Lane(AutomationTarget.ForMasterPan(), ControlValue.Max, ControlValue.Min);
        project = project with
        {
            Sequence = project.Sequence.WithTrack(track),
            MasterAutomation = [masterGain, masterPan],
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)],
        };
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);

        var (description, problems) = graphs.Describe(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);

        Assert.Empty(problems);
        var strip = Assert.Single(description.Strips, described => described.Key == track.Id.Value);
        Assert.Equal([(0L, 0d, true), (24_000L, 24d, false)],
            strip.GainAutomation.Select(point => (point.Frame, Math.Round(point.Value), point.Linear)));
        Assert.Equal([(0L, -1d, true), (24_000L, 1d, false)],
            strip.PanAutomation.Select(point => (point.Frame, point.Value, point.Linear)));
        Assert.Equal([(0L, 24d, true), (24_000L, 0d, false)],
            description.MasterGainAutomation.Select(point => (point.Frame, Math.Round(point.Value), point.Linear)));
        Assert.Equal([(0L, 1d, true), (24_000L, -1d, false)],
            description.MasterPanAutomation.Select(point => (point.Frame, point.Value, point.Linear)));
    }

    [Fact]
    public void TrackFaderAndPanAutomation_InterpolateAtIndividualSamplesWithinAnAudioBlock()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var startGain = ControlValue.FromFraction(5.0 / 6);
        var gain = new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForAudioStripGain(),
            [
                new AutomationPoint(Tick.Zero, startGain),
                new AutomationPoint(new Tick(2), ControlValue.Max, AutomationCurve.Hold),
            ]);
        var pan = new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForAudioStripPan(),
            [
                new AutomationPoint(Tick.Zero, ControlValue.Min),
                new AutomationPoint(new Tick(2), ControlValue.Max, AutomationCurve.Hold),
            ]);
        track = track.WithAutomation([gain, pan]);
        project = project with
        {
            Sequence = project.Sequence.WithTrack(track),
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)],
        };

        var output = Render(project, Block, out var result);
        var left = Left(output);
        var right = Right(output);
        var gainAt12 = AudioStrip.MinGainDecibels + (startGain.ToFraction() + ((1 - startGain.ToFraction()) * 0.12)) * (AudioStrip.MaxGainDecibels - AudioStrip.MinGainDecibels);

        Assert.Empty(result.Problems);
        Assert.Equal(_tone[12] * Math.Pow(10, gainAt12 / 20) * Math.Cos(0.76 * Math.PI / 2), right[12], 4);
        Assert.Equal(0f, left[112], 5);
        Assert.Equal(_tone[112] * Math.Pow(10, AudioStrip.MaxGainDecibels / 20), right[112], 3);
    }

    [Fact]
    public void MasterFaderAndPanAutomation_InterpolateAtIndividualSamplesWithinAnAudioBlock()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var startGain = ControlValue.FromFraction(5.0 / 6);
        var gain = new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForMasterGain(),
            [
                new AutomationPoint(Tick.Zero, startGain),
                new AutomationPoint(new Tick(2), ControlValue.Max, AutomationCurve.Hold),
            ]);
        var pan = new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForMasterPan(),
            [
                new AutomationPoint(Tick.Zero, ControlValue.Min),
                new AutomationPoint(new Tick(2), ControlValue.Max, AutomationCurve.Hold),
            ]);
        project = project with
        {
            Sequence = project.Sequence.WithTrack(track),
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)],
            MasterAutomation = [gain, pan],
        };

        var output = Render(project, Block, out var result);
        var left = Left(output);
        var right = Right(output);
        var gainAt12 = AudioStrip.MinGainDecibels + (startGain.ToFraction() + ((1 - startGain.ToFraction()) * 0.12)) * (AudioStrip.MaxGainDecibels - AudioStrip.MinGainDecibels);

        Assert.Empty(result.Problems);
        Assert.Equal(_tone[12] * Math.Pow(10, gainAt12 / 20) * Math.Cos(0.76 * Math.PI / 2), right[12], 4);
        Assert.Equal(0f, left[112], 5);
        Assert.Equal(_tone[112] * Math.Pow(10, AudioStrip.MaxGainDecibels / 20), right[112], 3);
    }


    [Fact]
    public void AMissingFile_IsAProblem_NotAFailure()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        File.Delete(Path.Combine(_directory, "tone.wav"));

        var output = Render(project, Block * 4, out var result);

        Assert.Equal($"{track.Name}: tone.wav: the file is missing", Assert.Single(result.Problems));
        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void AnEditKeepsTheClipNodeAndTheStripState_WhenTheyDidNotChange()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        var first = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        var strip = graphs.TrackStripOf(track.Id);

        var louderTrack = track.WithAudioStrip(new AudioStrip { GainDecibels = 3 });
        var louder = project with { Sequence = project.Sequence.WithTrack(louderTrack) };
        var second = graphs.Build(louder, SignalGraph.Evaluate(louder, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);

        Assert.Same(strip, graphs.TrackStripOf(track.Id));
        Assert.Same(
            first.Graph.Nodes.OfType<ClipPlayerNode>().Single(),
            second.Graph.Nodes.OfType<ClipPlayerNode>().Single());

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
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        vocal = vocal.WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var reverb = Track.Create("Reverb", TrackRole.Effect);
        project = project with
        {
            Sequence = project.Sequence.WithTrack(vocal).WithTrack(reverb),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Track(reverb.Id)) with
                {
                    AudioPurpose = AudioRoutePurpose.Send,
                    SendLevelDecibels = -6.0206,
                    SendPreFader = true,
                },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(reverb.Id), SignalNode.Master),
            ],
        };

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
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var reverb = Track.Create("Reverb", TrackRole.Effect);
        var delay = Track.Create("Delay", TrackRole.Effect);
        project = project with
        {
            Sequence = project.Sequence.WithTrack(reverb).WithTrack(delay),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Track(reverb.Id)) with { AudioPurpose = AudioRoutePurpose.Send },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(reverb.Id), SignalNode.Track(delay.Id)) with { AudioPurpose = AudioRoutePurpose.Send },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(delay.Id), SignalNode.Track(reverb.Id)) with { AudioPurpose = AudioRoutePurpose.Send },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(reverb.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(delay.Id), SignalNode.Master),
            ],
        };

        var output = Render(project, Rate / 4, out var result);

        Assert.Contains(result.Problems, problem => problem.Contains("feedback", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2 * _tone[100], Left(output)[100], 4);
    }

    [Fact]
    public void ClipGainAndFades_FromTheProject_AreMeasuredInTicks()
    {
        // A fade-in of 240 ticks is a quarter second (12 000 frames) at 120 BPM; the clip is 6 dB down.
        var (project, track) = ProjectWithClip(start: 0, length: 960, offset: 0);
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
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
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

        static ClipPlayerNode Node(ProjectAudioGraphResult result) => result.Graph.Nodes.OfType<ClipPlayerNode>().Single();
    }

    [Fact]
    public void ANodeTheEngineClosedWhileItsTrackWasMuted_IsOpenedAgainWhenTheTrackPlays()
    {
        var (project, _) = ProjectWithClip(start: 0, length: 480, offset: 0);
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        var first = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        var node = first.Graph.Nodes.OfType<ClipPlayerNode>().Single();
        node.Dispose();

        var again = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        var reopened = again.Graph.Nodes.OfType<ClipPlayerNode>().Single();

        Assert.NotSame(node, reopened);
        Assert.False(reopened.IsClosed);
    }

    [Fact]
    public void ANodeReusedByANewBuild_IsNotClosedByADiskPassOverAnOlderGraph()
    {
        // A three-second clip: it streams past its one-second preload.
        var long_ = TestAudioFiles.Sine(Rate * 3, 1, 1000, Rate);
        TestAudioFiles.Wave(_directory, "long.wav", "float32", Rate, 1, long_);
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var clip = new AudioClip(ClipId.New(), new Tick(0), new TickSpan(2880), new TickSpan(0), new AudioSource("long.wav", Rate, Rate * 3, 1));
        var playing = project with { Sequence = project.Sequence.WithTrack(track.RemoveClip(track.Clips[0].Id).WithClip(clip)) };
        var muted = playing with { Sequence = playing.Sequence.WithTrack(playing.Sequence.FindTrack(track.Id)!.WithMuted(true)) };
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(Build(playing).Graph);
        engine.ServiceStreams();
        var node = engine.Graph.Nodes.OfType<ClipPlayerNode>().Single();

        // Muted, then unmuted: the new build reuses the node. A disk pass that read the muted graph runs before the
        // new graph is published.
        engine.Publish(Build(muted).Graph);
        var unmuted = Build(playing).Graph;
        engine.ServiceStreams();
        engine.Publish(unmuted);
        engine.Transport.Play(0);
        var output = Left(new OfflineAudioDriver(engine).Render(Rate * 2, Block));

        Assert.Same(node, unmuted.Nodes.OfType<ClipPlayerNode>().Single());
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

    [Fact]
    public void EveryJunctionOfAChainItReaches_HasAMeter_MeasuredOnlyWhileWatched()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var transpose = BuiltInDevices.CreateTranspose(12);
        var chain = DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [transpose] };
        project = project.WithChain(chain);
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        graphs.WatchJunctions([chain.Id.Value]);
        var result = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(result.Graph);
        engine.Transport.Play(0);

        new OfflineAudioDriver(engine).Render(Rate / 10, Block);

        // The clips arrive at the chain's input, and pass the note device unchanged to the point after it.
        var input = graphs.JunctionMeterOf(chain.Id.Value)!;
        var afterTranspose = graphs.JunctionMeterOf(transpose.Id.Value)!;
        Assert.Equal(_tone.Take(Rate / 10).Max(), input.Read().PeakLeft, 3);
        Assert.Equal(default, afterTranspose.Read());

        graphs.WatchJunctions([transpose.Id.Value]);
        Assert.True(afterTranspose.IsWatched);
        Assert.False(input.IsWatched);
    }

    [Fact]
    public void AChainsInput_CanBeRoutedAsAudio()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var receiver = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference());
        var chain = DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [receiver] };
        var routed = project.WithChain(chain) with
        {
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(chain.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master),
            ],
        };

        var output = Left(Render(routed, Rate / 4, out var result));

        Assert.Empty(result.Problems);
        Assert.Equal(2 * _tone[100], output[100], 3);
    }

    [Theory]
    [InlineData(true, 0.5)]
    [InlineData(false, 1.5)]
    public void AnAudioReceiver_BringsAnotherTracksAudioIn_AtItsGain_MixedOrReplacing(bool replace, double expected)
    {
        // Vocal's clip plays only through Bus's receiver at -6 dB; Bus also plays the same clip itself.
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        vocal = vocal.WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var receiver = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference())
            .WithParameter(BuiltInDevices.ReceiverReplace, replace ? ControlValue.Max : ControlValue.Min)
            .WithParameter(BuiltInDevices.ReceiverGain, BuiltInDevices.AudioReceiver.Parameters[1].ToStored(-6.0206));
        var bus = Track.Create("Bus", TrackRole.Audio).WithClip(project.Sequence.FindTrack(vocal.Id)!.Clips[0].CopyTo(Tick.Zero));
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(bus.Id)) with { Devices = [receiver] }) with
        {
            Sequence = project.Sequence.WithTrack(vocal).WithTrack(bus),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(bus.Id), SignalNode.Master),
            ],
        };

        var output = Left(Render(project, Rate / 4, out var result));

        Assert.Empty(result.Problems);
        for (var n = 0; n < Rate / 4; n += 101)
        {
            Assert.Equal(expected * _tone[n], output[n], 3);
        }
    }

    [Fact]
    public void AnAudioReceiversGain_IsOneNodePerSource_SoAMonitoredSourceNeverSharesOneWithACompensatedSource()
    {
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var receiver = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference())
            .WithParameter(BuiltInDevices.ReceiverGain, BuiltInDevices.AudioReceiver.Parameters[1].ToStored(-6.0206));
        vocal = vocal.WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var other = Track.Create("Other", TrackRole.Audio).WithClip(project.Sequence.FindTrack(vocal.Id)!.Clips[0].CopyTo(Tick.Zero))
            .WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var bus = Track.Create("Bus", TrackRole.Audio);
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(bus.Id)) with { Devices = [receiver] }) with
        {
            Sequence = project.Sequence.WithTrack(vocal).WithTrack(other).WithTrack(bus),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(other.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(other.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(bus.Id), SignalNode.Master),
            ],
        };
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);

        var (description, problems) = graphs.Describe(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);

        Assert.Empty(problems);
        var gains = description.Nodes
            .Select((node, index) => (Node: node, Index: index))
            .Where(item => item.Node.Kind == DescribedNodeKind.Gain && item.Node.Name == receiver.DisplayName)
            .ToList();
        Assert.Equal(2, gains.Count);
        Assert.Equal(2, gains.Select(g => g.Node.Key).Distinct().Count());
        Assert.All(gains, g => Assert.Single(description.Steps, s => s.Kind == DescribedStepKind.Connect && s.B == g.Index));
        Assert.Equal(_tone[100], Left(Render(project, Rate / 4, out _))[100], 3);
    }

    [Fact]
    public void AnAudioReceiver_MixingItsOwnChainsInput_AtUnityGain_HearsItTwice_AsAtAnyOtherGain()
    {
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var receiver = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference());
        var chain = DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [receiver] };
        project = project.WithChain(chain) with
        {
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(chain.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master),
            ],
        };

        Assert.Equal(2 * _tone[100], Left(Render(project, Rate / 4, out _))[100], 3);
    }

    [Fact]
    public void TwoConnectionsIntoAReceiver_ThatReachTheSameAudio_AreEachHeard()
    {
        // The chain's input and the point after a Transpose carry the same clip; the receiver takes both and mixes them in.
        var (project, track) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var transpose = BuiltInDevices.CreateTranspose(12);
        var receiver = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference());
        var chain = DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [transpose, receiver] };
        var inlet = SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive);
        project = project.WithChain(chain) with
        {
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.ChainInput(chain.Id), inlet) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Device(transpose.Id), inlet) with { AudioPurpose = AudioRoutePurpose.Internal },
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master),
            ],
        };

        Assert.Equal(3 * _tone[100], Left(Render(project, Rate / 4, out _))[100], 3);
    }

    [Fact]
    public void RemovingOneOfAReceiversSources_LeavesTheOtherSourcesGainNode_AsItWas()
    {
        var (project, vocal) = ProjectWithClip(start: 0, length: 480, offset: 0);
        var receiver = DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference())
            .WithParameter(BuiltInDevices.ReceiverGain, BuiltInDevices.AudioReceiver.Parameters[1].ToStored(-6.0206));
        vocal = vocal.WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var other = Track.Create("Other", TrackRole.Audio).WithClip(project.Sequence.FindTrack(vocal.Id)!.Clips[0].CopyTo(Tick.Zero))
            .WithAudioStrip(new AudioStrip { GainDecibels = AudioStrip.MinGainDecibels });
        var bus = Track.Create("Bus", TrackRole.Audio);
        var fromVocal = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with
        {
            AudioPurpose = AudioRoutePurpose.Internal,
        };
        var fromOther = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(other.Id), SignalNode.DeviceInput(receiver.Id, DeviceInputPort.Receive)) with
        {
            AudioPurpose = AudioRoutePurpose.Internal,
        };
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(bus.Id)) with { Devices = [receiver] }) with
        {
            Sequence = project.Sequence.WithTrack(vocal).WithTrack(other).WithTrack(bus),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Master),
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(other.Id), SignalNode.Master),
                fromVocal,
                fromOther,
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(bus.Id), SignalNode.Master),
            ],
        };
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        Guid GainOnOther(Project p)
        {
            var description = graphs.Describe(p, SignalGraph.Evaluate(p, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory).Description;
            var source = description.Nodes.ToList().FindIndex(n => n.Key == other.Id.Value);
            var gain = description.Steps.Single(s => s.Kind == DescribedStepKind.Connect && s.A == source && description.Nodes[s.B].Kind == DescribedNodeKind.Gain).B;
            return description.Nodes[gain].Key;
        }

        var before = GainOnOther(project);
        var after = GainOnOther(project with { Connections = project.Connections.Remove(fromVocal) });

        Assert.Equal(before, after);
    }

    private static float[] Left(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 == 0)];

    private static float[] Right(float[] interleaved) => [.. interleaved.Where((_, i) => i % 2 != 0)];

    private static DeviceInstance AudioReceiver(double gainDecibels) =>
        DeviceInstance.Create(BuiltInDevices.AudioReceiver.ToReference())
            .WithParameter(BuiltInDevices.ReceiverReplace, ControlValue.Max)
            .WithParameter(BuiltInDevices.ReceiverGain, BuiltInDevices.AudioReceiver.Parameters[1].ToStored(gainDecibels));

    private static AutomationLane Lane(AutomationTarget target, ControlValue start, ControlValue end) =>
        new AutomationLane(
            AutomationLaneId.New(),
            target,
            [
                new AutomationPoint(Tick.Zero, start),
                new AutomationPoint(new Tick(480), end, AutomationCurve.Hold),
            ]);

    private float[] Render(Project project, int frames, out ProjectAudioGraphResult result)
    {
        using var graphs = new ProjectAudioGraph(Rate, Block, _ => null);
        result = graphs.Build(project, SignalGraph.Evaluate(project, DeviceCatalog.BuiltIn), DeviceCatalog.BuiltIn, _directory);
        using var engine = new AudioEngine(Rate, Block, streamFromDisk: false);
        engine.Publish(result.Graph);
        engine.Transport.Play(0);
        return new OfflineAudioDriver(engine).Render(frames, Block);
    }

    // A one-second 1 kHz tone file; an audio track holding a clip of it and routed to the Master.
    private (Project Project, Track Track) ProjectWithClip(long start, long length, long offset)
    {
        TestAudioFiles.Wave(_directory, "tone.wav", "float32", Rate, 1, _tone);
        var clip = new AudioClip(ClipId.New(), new Tick(start), new TickSpan(length), new TickSpan(offset), new AudioSource("tone.wav", Rate, Rate, 1));
        var track = Track.Create("Vocal", TrackRole.Audio).WithClip(clip);
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Resolution).WithTrack(track),
            Connections = [SignalConnection.Create(SignalKind.Audio, SignalNode.Track(track.Id), SignalNode.Master)],
        };
        return (project, track);
    }

    private readonly record struct ScheduledParameterChangeView(long Frame, uint Parameter, double Value);
}
