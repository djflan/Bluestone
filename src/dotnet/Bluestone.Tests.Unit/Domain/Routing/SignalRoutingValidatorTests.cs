using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using static Bluestone.Tests.Unit.Domain.Routing.RoutingFixture;

namespace Bluestone.Tests.Unit.Domain.Routing;

public sealed class SignalRoutingValidatorTests
{
    // Scenario 1 and 4: several tracks feed one shared instrument, which has one audio output.
    [Fact]
    public void FourTracks_CanFeedOneSharedInstrument_WhichFeedsOneMixerChannel()
    {
        var project = Empty().WithRack(out var xg, "XG", TestDevices.Synth).WithMixerChannel(out var channel, "XG out");
        var tracks = Enumerable.Range(1, 4).Select(i => NoteTrack($"Part {i}", channel: i)).ToList();
        foreach (var track in tracks)
        {
            project = project.WithTrackChain(track).Connect(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.Rack(xg.Id), ChannelMapping.ForceTo(MidiChannel.FromNumber(tracks.IndexOf(track) + 1)));
        }

        project = project.Connect(SignalKind.Audio, SignalNode.Rack(xg.Id), SignalNode.Mixer(channel.Id));

        Assert.Empty(project.Validate());
        Assert.Single(project.Chains, c => c.Devices.Any(d => d.Definition.Id == TestDevices.Synth.Id));
        Assert.Single(project.Mixer.Channels);
    }

    // Scenario 8
    [Fact]
    public void TrackToExternalPart_IsValid_AndNamesThePortAndChannel()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithInstrument(out var mu)
            .Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id, "B"), ChannelMapping.ForceTo(MidiChannel.FromNumber(4)));

        Assert.Empty(project.Validate());
        var connection = Assert.Single(project.Connections);
        Assert.Equal("B", connection.Destination.Port);
        Assert.True(connection.Mapping.TryMap(One, out var sent));
        Assert.Equal(MidiChannel.FromNumber(4), sent);
    }

    [Fact]
    public void UnknownPort_IsAnError()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithInstrument(out var mu)
            .Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id, "Z"));

        Assert.Equal(RoutingIssueCode.UnknownPort, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void EventsCannotGoToTheMixer_AndAudioCannotGoToAMidiPort()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.Synth).WithInstrument(out var mu).WithMixerChannel(out var channel, "Bus")
            .Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.Mixer(channel.Id))
            .Connect(SignalKind.Audio, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id));

        Assert.All(project.Errors(), e => Assert.Equal(RoutingIssueCode.KindMismatch, e.Code));
        Assert.Equal(2, project.Errors().Count());
    }

    [Fact]
    public void AudioFromAMidiOnlyTrack_HasNothingToCarry()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithMixerChannel(out var channel, "Bus")
            .Connect(SignalKind.Audio, SignalNode.Track(piano.Id), SignalNode.Mixer(channel.Id));

        var error = Assert.Single(project.Errors());
        Assert.Equal(RoutingIssueCode.NothingToCarry, error.Code);
        Assert.Contains("Piano", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioAfterTheSynth_CanBeTapped_ButNotBefore()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.MidiFx, TestDevices.Synth, TestDevices.Filter).WithMixerChannel(out var channel, "Bus");
        var chain = project.ChainOf(piano.Id)!;
        var beforeSynth = SignalNode.Device(chain.Devices[0].Id);
        var afterSynth = SignalNode.Device(chain.Devices[1].Id);

        Assert.Single(project.Connect(SignalKind.Audio, beforeSynth, SignalNode.Mixer(channel.Id)).Errors(), e => e.Code == RoutingIssueCode.NothingToCarry);
        Assert.Empty(project.Connect(SignalKind.Audio, afterSynth, SignalNode.Mixer(channel.Id)).Errors());
    }

    [Fact]
    public void EventsStillPass_AfterAnInstrumentThatDoesNotHandleThem()
    {
        // The synth handles notes, controllers, and programs; SysEx is not among them, so it is still in the signal.
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.Synth).WithInstrument(out var mu);
        var afterSynth = SignalNode.Device(project.ChainOf(piano.Id)!.Devices[0].Id);

        Assert.Empty(project.Connect(SignalKind.Events, afterSynth, SignalNode.ExternalPart(mu.Id)).Errors());
    }

    [Fact]
    public void EventsAfterAnInstrumentThatTookEverything_HaveNothingToCarry()
    {
        var takesAll = TestDevices.Synth with { Id = new DeviceDefinitionId("test.takes-all"), Handles = EventClass.Transmitted };
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithInstrument(out var mu);
        project = project.WithChain(project.ChainOf(piano.Id)! with { Devices = [DeviceInstance.Create(takesAll.ToReference())] });
        var afterSynth = SignalNode.Device(project.ChainOf(piano.Id)!.Devices[0].Id);

        var error = Assert.Single(project.Connect(SignalKind.Events, afterSynth, SignalNode.ExternalPart(mu.Id)).Validate(definitions: id => id == takesAll.Id ? takesAll : null));
        Assert.Equal(RoutingIssueCode.NothingToCarry, error.Code);
    }

    [Fact]
    public void ABypassedInstrument_LeavesTheSignalAlone()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.Synth).WithMixerChannel(out var channel, "Bus");
        var chain = project.ChainOf(piano.Id)!;
        project = project.WithChain(chain.Replace(chain.Devices[0] with { IsBypassed = true }))
            .Connect(SignalKind.Audio, SignalNode.Track(piano.Id), SignalNode.Mixer(channel.Id));

        Assert.Equal(RoutingIssueCode.NothingToCarry, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void AMissingPlugin_NeverMakesAConnectionAnError()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithMixerChannel(out var channel, "Bus");
        var missing = DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId("vst3:uninstalled"), "Vital"));
        project = project.WithChain(project.ChainOf(piano.Id)! with { Devices = [missing] })
            .Connect(SignalKind.Audio, SignalNode.Track(piano.Id), SignalNode.Mixer(channel.Id));

        Assert.Empty(project.Validate());
    }

    [Fact]
    public void Cycle_BetweenTwoTracks_IsReportedWithTheirNames()
    {
        var a = NoteTrack("Arp");
        var b = NoteTrack("Lead");
        var project = Empty().WithTrackChain(a).WithTrackChain(b)
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id))
            .Connect(SignalKind.Events, SignalNode.Track(b.Id), SignalNode.Track(a.Id));

        var error = Assert.Single(project.Errors());
        Assert.Equal(RoutingIssueCode.Cycle, error.Code);
        Assert.Equal(2, error.Connections.Length);
        Assert.Contains("Arp", error.Message, StringComparison.Ordinal);
        Assert.Contains("Lead", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cycle_ThroughATapAndASharedRack_IsDetected()
    {
        var track = NoteTrack("Piano");
        var project = Empty().WithTrackChain(track, TestDevices.MidiFx).WithRack(out var rack, "Shared", TestDevices.MidiFx);
        var tap = SignalNode.Device(project.ChainOf(track.Id)!.Devices[0].Id);
        project = project.Connect(SignalKind.Events, tap, SignalNode.Rack(rack.Id)).Connect(SignalKind.Events, SignalNode.Rack(rack.Id), SignalNode.Track(track.Id));

        Assert.Equal(RoutingIssueCode.Cycle, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void ATrackConnectedToItself_IsACycle()
    {
        var track = NoteTrack("Piano");
        var project = Empty().WithTrackChain(track).Connect(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.Track(track.Id));

        Assert.Equal(RoutingIssueCode.Cycle, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void MixerBusLoop_IsACycle()
    {
        var project = Empty().WithMixerChannel(out var a, "A").WithMixerChannel(out var b, "B");
        project = project with { Mixer = project.Mixer.With(a with { Output = b.Id }).With(b with { Output = a.Id }) };

        Assert.Equal(RoutingIssueCode.Cycle, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void ASendBackToAChannelThatFeedsIt_IsACycle_NamingTheChannels()
    {
        var project = Empty().WithMixerChannel(out var vocal, "Vocal").WithMixerChannel(out var reverb, "Reverb");
        project = project with { Mixer = project.Mixer.With(vocal.WithSend(new MixerSend(reverb.Id))).With(reverb with { Output = vocal.Id }) };

        var error = Assert.Single(project.Errors());
        Assert.Equal(RoutingIssueCode.Cycle, error.Code);
        Assert.Contains("Vocal", error.Message, StringComparison.Ordinal);
        Assert.Contains("Reverb", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendsFromManyChannelsToOneReturn_AreValid()
    {
        var project = Empty().WithMixerChannel(out var a, "A").WithMixerChannel(out var b, "B").WithMixerChannel(out var reverb, "Reverb");
        project = project with { Mixer = project.Mixer.With(a.WithSend(new MixerSend(reverb.Id))).With(b.WithSend(new MixerSend(reverb.Id, preFader: true)) with { Output = a.Id }) };

        Assert.Empty(project.Errors());
    }

    [Fact]
    public void ASendToAMissingChannel_IsAnError()
    {
        var project = Empty().WithMixerChannel(out var a, "A");
        project = project with { Mixer = project.Mixer.With(a.WithSend(new MixerSend(MixerChannelId.New()))) };

        Assert.Equal(RoutingIssueCode.UnknownDestination, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void FanOutAndFanIn_AreNotCycles()
    {
        var a = NoteTrack("A");
        var b = NoteTrack("B");
        var c = NoteTrack("C");
        var d = NoteTrack("D");
        var project = Empty().WithTrackChain(a).WithTrackChain(b).WithTrackChain(c).WithTrackChain(d)
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id))
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(c.Id))
            .Connect(SignalKind.Events, SignalNode.Track(b.Id), SignalNode.Track(d.Id))
            .Connect(SignalKind.Events, SignalNode.Track(c.Id), SignalNode.Track(d.Id));

        Assert.Empty(project.Validate());
    }

    [Fact]
    public void MissingAndMisplacedEnds_AreReportedPerConnection()
    {
        var ghost = TrackId.New();
        var project = Empty()
            .Connect(SignalKind.Events, SignalNode.Track(ghost), SignalNode.Track(TrackId.New()))
            .Connect(SignalKind.Events, SignalNode.Master, SignalNode.Track(ghost));

        Assert.Equal(
            [RoutingIssueCode.UnknownSource, RoutingIssueCode.UnknownDestination, RoutingIssueCode.InvalidSource, RoutingIssueCode.UnknownDestination],
            project.Validate().Select(i => i.Code));
    }

    [Fact]
    public void DuplicateConnection_IsAnError()
    {
        var a = NoteTrack("A");
        var b = NoteTrack("B");
        var project = Empty().WithTrackChain(a).WithTrackChain(b)
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id))
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id));

        Assert.Equal(RoutingIssueCode.DuplicateConnection, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void ForceAndRemapTogether_AreInconsistent()
    {
        var piano = NoteTrack("Piano");
        var bad = new ChannelMapping { Force = One, Remap = [new ChannelRemap(MidiChannel.FromNumber(2), MidiChannel.FromNumber(3))] };
        var project = Empty().WithTrackChain(piano).WithInstrument(out var mu)
            .Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id), bad);

        Assert.Equal(RoutingIssueCode.InvalidMapping, Assert.Single(project.Errors()).Code);
    }

    [Fact]
    public void AVoiceOnAnythingButAnExternalPart_IsOnlyAWarning()
    {
        var a = NoteTrack("A");
        var b = NoteTrack("B");
        var project = Empty().WithTrackChain(a).WithTrackChain(b)
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), SignalNode.Track(b.Id), voice: new VoiceAssignment("main", ProgramNumber.FromNumber(3)));

        Assert.Empty(project.Errors());
        Assert.Equal(RoutingIssueCode.VoiceNotApplicable, Assert.Single(project.Validate()).Code);
    }

    // Scenario 14: reordering keeps identity, and the routing that no longer makes sense is reported, not silently kept.
    [Fact]
    public void ReorderingADevice_KeepsItsTapAttached_AndRevalidatesIt()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.MidiFx, TestDevices.Synth, TestDevices.Filter).WithMixerChannel(out var channel, "Bus");
        var chain = project.ChainOf(piano.Id)!;
        var filter = chain.Devices[2];
        project = project.Connect(SignalKind.Audio, SignalNode.Device(filter.Id), SignalNode.Mixer(channel.Id));
        Assert.Empty(project.Validate());

        var moved = project.WithChain(chain.Move(filter.Id, 0));

        Assert.Equal(filter, moved.FindDevice(filter.Id)!.Value.Device);
        Assert.Equal(0, moved.ChainOf(piano.Id)!.IndexOf(filter.Id));
        Assert.Equal(RoutingIssueCode.NothingToCarry, Assert.Single(moved.Errors()).Code);
    }

    [Fact]
    public void AutomationOfMissingDevicesAndParameters_IsReportedAndKept()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.Filter);
        var filter = project.ChainOf(piano.Id)!.Devices[0];
        var lanes = new[]
        {
            AutomationLane.Create(AutomationTarget.ForDevice(DeviceId.New(), new ParameterId(1))),
            AutomationLane.Create(AutomationTarget.ForDevice(filter.Id, new ParameterId(99))),
            AutomationLane.Create(AutomationTarget.ForDevice(filter.Id, new ParameterId(1))),
        };
        project = project.With(project.Sequence.FindTrack(piano.Id)!.WithAutomation(lanes));

        var issues = project.Validate();

        Assert.Equal([RoutingIssueCode.AutomationDeviceMissing, RoutingIssueCode.AutomationParameterUnknown], issues.Select(i => i.Code));
        Assert.All(issues, i => Assert.Equal(RoutingSeverity.Warning, i.Severity));
        Assert.Equal(3, project.Sequence.FindTrack(piano.Id)!.Automation.Length);
    }
}
