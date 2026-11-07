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
    public void EventsAfterAnInstrument_StillCarryTheNotesItPassesOn()
    {
        // ADR 0037: an instrument plays what it handles and passes it on (ChainRunner does), so even an instrument that
        // handles every class leaves events to send from its tap and from the track's output.
        var takesAll = TestDevices.Synth with { Id = new DeviceDefinitionId("test.takes-all"), Handles = EventClass.Transmitted };
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithInstrument(out var mu);
        project = project.WithChain(project.ChainOf(piano.Id)! with { Devices = [DeviceInstance.Create(takesAll.ToReference())] });
        var afterSynth = SignalNode.Device(project.ChainOf(piano.Id)!.Devices[0].Id);
        DeviceDefinitionLookup definitions = id => id == takesAll.Id ? takesAll : null;

        Assert.Empty(project.Connect(SignalKind.Events, afterSynth, SignalNode.ExternalPart(mu.Id)).Validate(definitions: definitions));
        Assert.Empty(project.Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id)).Validate(definitions: definitions));
    }

    [Fact]
    public void EventsAfterAnInstrumentThatEndsTheNotePath_HaveNothingToCarry()
    {
        // A specialized instrument with EventThru = None takes everything it handles out of the chain.
        var sink = TestDevices.Synth with { Id = new DeviceDefinitionId("test.sink"), Handles = EventClass.Transmitted, EventThru = EventClass.None };
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano).WithInstrument(out var mu);
        project = project.WithChain(project.ChainOf(piano.Id)! with { Devices = [DeviceInstance.Create(sink.ToReference())] });
        var afterSynth = SignalNode.Device(project.ChainOf(piano.Id)!.Devices[0].Id);
        DeviceDefinitionLookup definitions = id => id == sink.Id ? sink : null;

        var error = Assert.Single(project.Connect(SignalKind.Events, afterSynth, SignalNode.ExternalPart(mu.Id)).Validate(definitions: definitions));
        Assert.Equal(RoutingIssueCode.NothingToCarry, error.Code);
        error = Assert.Single(project.Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id)).Validate(definitions: definitions));
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

    [Fact]
    public void ChainInput_CarriesWhatArrivesBeforeTheFirstDevice()
    {
        // Notes reach the chain's input even though the instrument would end them at its output if it did not pass them on.
        var piano = NoteTrack("Piano");
        var other = NoteTrack("Other");
        var project = Empty().WithTrackChain(piano, TestDevices.Synth).WithTrackChain(other);
        var chain = project.ChainOf(piano.Id)!;
        project = project
            .Connect(SignalKind.Events, SignalNode.ChainInput(chain.Id), SignalNode.Track(other.Id))
            .Connect(SignalKind.Audio, SignalNode.ChainInput(chain.Id), SignalNode.Master);

        var error = Assert.Single(project.Errors());
        Assert.Equal(RoutingIssueCode.NothingToCarry, error.Code);
        Assert.Contains("input of \"Piano\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainInput_CannotBeADestination_AndAMissingChainIsUnknown()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.MidiFx).WithRack(out var rack, "Rack", TestDevices.Synth);
        project = project
            .Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ChainInput(rack.Id))
            .Connect(SignalKind.Events, SignalNode.ChainInput(new DeviceChainId(Guid.NewGuid())), SignalNode.Rack(rack.Id));

        Assert.Equal([RoutingIssueCode.InvalidDestination, RoutingIssueCode.UnknownSource], project.Errors().Select(e => e.Code));
    }

    [Fact]
    public void DeviceInput_TakesItsPortsKind_FromAnotherTrack()
    {
        var drums = NoteTrack("Drums", TrackRole.Hybrid);
        var bass = NoteTrack("Bass");
        var project = Empty().WithTrackChain(drums, TestDevices.Synth).WithTrackChain(bass, TestDevices.Synth, TestDevices.Ducker);
        var ducker = project.ChainOf(bass.Id)!.Devices[1];
        var sidechain = SignalNode.DeviceInput(ducker.Id, DeviceInputPort.Sidechain);

        Assert.Empty(project.Connect(SignalKind.Audio, SignalNode.Track(drums.Id), sidechain).Validate());
        Assert.Equal(RoutingIssueCode.KindMismatch, Assert.Single(project.Connect(SignalKind.Events, SignalNode.Track(drums.Id), sidechain).Errors()).Code);
        Assert.Equal(RoutingIssueCode.UnknownPort, Assert.Single(project.Connect(SignalKind.Audio, SignalNode.Track(drums.Id), SignalNode.DeviceInput(ducker.Id, "other")).Errors()).Code);
        Assert.Equal(RoutingIssueCode.UnknownDestination, Assert.Single(project.Connect(SignalKind.Audio, SignalNode.Track(drums.Id), SignalNode.DeviceInput(DeviceId.New(), DeviceInputPort.Sidechain)).Errors()).Code);
    }

    [Fact]
    public void DeviceInput_OfADeviceThatIsNotInstalled_IsKept()
    {
        var drums = NoteTrack("Drums", TrackRole.Hybrid);
        var bass = NoteTrack("Bass");
        var project = Empty().WithTrackChain(drums, TestDevices.Synth).WithTrackChain(bass, TestDevices.Synth, TestDevices.Ducker);
        var ducker = project.ChainOf(bass.Id)!.Devices[1];
        project = project.Connect(SignalKind.Audio, SignalNode.Track(drums.Id), SignalNode.DeviceInput(ducker.Id, "anything"));

        Assert.DoesNotContain(project.Validate(id => id == TestDevices.Ducker.Id ? null : TestDevices.Lookup(id)), i => i.Severity == RoutingSeverity.Error);
    }

    [Fact]
    public void DeviceInput_InTheSameChain_TakesOnlyFromEarlierPoints()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.MidiFx, TestDevices.EventInlet, TestDevices.Synth);
        var chain = project.ChainOf(piano.Id)!;
        var inlet = SignalNode.DeviceInput(chain.Devices[1].Id, DeviceInputPort.Receive);

        // Before it (the chain's input, after the effect, and what arrives at it) is fine and not a cycle between owners.
        Assert.Empty(project.Connect(SignalKind.Events, SignalNode.ChainInput(chain.Id), inlet).Validate());
        Assert.Empty(project.Connect(SignalKind.Events, SignalNode.Device(chain.Devices[0].Id), inlet).Validate());

        // Its own output, anything after it, and the chain's output would feed back.
        foreach (var later in new[] { SignalNode.Device(chain.Devices[1].Id), SignalNode.Device(chain.Devices[2].Id), SignalNode.Track(piano.Id) })
        {
            var error = Assert.Single(project.Connect(SignalKind.Events, later, inlet).Errors());
            Assert.Equal(RoutingIssueCode.Cycle, error.Code);
        }
    }

    [Fact]
    public void DeviceInput_FedThroughAnotherTrack_ClosesALoop()
    {
        // A → B's inlet, and B's output → A: the owners feed each other.
        var a = NoteTrack("A");
        var b = NoteTrack("B");
        var project = Empty().WithTrackChain(a).WithTrackChain(b, TestDevices.EventInlet);
        var inlet = SignalNode.DeviceInput(project.ChainOf(b.Id)!.Devices[0].Id, DeviceInputPort.Receive);
        project = project
            .Connect(SignalKind.Events, SignalNode.Track(a.Id), inlet)
            .Connect(SignalKind.Events, SignalNode.Track(b.Id), SignalNode.Track(a.Id));

        var error = Assert.Single(project.Errors());
        Assert.Equal(RoutingIssueCode.Cycle, error.Code);
        Assert.Equal(2, error.Connections.Length);
    }
}
