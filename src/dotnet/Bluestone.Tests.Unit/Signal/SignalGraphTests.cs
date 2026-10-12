using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Signal;
using static Bluestone.Tests.Unit.Signal.SignalFixture;

namespace Bluestone.Tests.Unit.Signal;

/// <summary>Acceptance scenarios 2, 3, 4, 6, and 10 (docs/architecture.md) through the signal graph, plus its routing rules.</summary>
public sealed class SignalGraphTests
{
    private static readonly MidiChannel Two = MidiChannel.FromNumber(2);
    private static readonly MidiChannel Four = MidiChannel.FromNumber(4);

    private static Track Chord(string name, long length = 1440) =>
        TrackOf(name, Note(0, 60, length: length), Note(0, 64, length: length), Note(0, 67, length: length));

    [Fact]
    public void Scenario2_ATrackMidiEffect_TransformsNotesBeforeTheSynth()
    {
        var arp = BuiltInDevices.CreateArpeggiator(rate: 2);
        var transpose = BuiltInDevices.CreateTranspose(12);
        var synth = Instance(TestDevices.Synth);
        var project = NewProject().With(Chord("Keys"), arp, transpose, synth);

        var result = project.Evaluate();

        var notes = result.InstrumentFor(synth).Events.Notes().ToList();
        Assert.Equal(12, notes.Count);
        Assert.Equal([72, 76, 79, 72, 76, 79, 72, 76, 79, 72, 76, 79], notes.Select(n => (int)n.Note.Value));
        Assert.Contains(result.Diagnostics, d => d.Code == SignalDiagnosticCode.NotAudible && d.Device == synth.Id);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == SignalDiagnosticCode.Unrouted);
    }

    [Fact]
    public void Scenario2_ATrackMidiEffect_TransformsNotesBeforeExternalHardware()
    {
        var keys = Chord("Keys", length: 480);
        var project = NewProject()
            .With(keys, BuiltInDevices.CreateTranspose(-12))
            .WithInstrument(out var mu2000)
            .Connect(out var route, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id), ChannelMapping.ForceTo(Four));

        var part = project.Evaluate().PartFor(route);

        Assert.Equal(mu2000.Id, part.Instrument);
        Assert.Equal(ExternalInstrument.DefaultPortId, part.Port);
        Assert.Equal(Four, part.ForcedChannel);
        Assert.Equal([48, 52, 55], part.Events.Notes().Select(n => (int)n.Note.Value));
        Assert.All(part.Events.Notes(), n => Assert.Equal(Four, n.Channel));
    }

    [Fact]
    public void Scenario3_EventsAMidiEffectGenerates_AreReceivedByAnotherTrack()
    {
        var arp = BuiltInDevices.CreateArpeggiator(rate: 2);
        var synth = Instance(TestDevices.Synth);
        var lead = Chord("Lead");
        var harmony = TrackOf("Harmony", Note(1440, 48));
        var project = NewProject()
            .With(lead, arp, synth)
            .With(harmony, BuiltInDevices.CreateTranspose(12))
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Device(arp.Id), SignalNode.Track(harmony.Id))
            .Connect(out var toHardware, SignalNode.Track(harmony.Id), SignalNode.ExternalPart(mu2000.Id, "B"), ChannelMapping.ForceTo(Two));

        var result = project.Evaluate();

        // The lead's synth still gets its twelve notes; the tap after the arpeggiator gives the harmony track
        // the same twelve, which it transposes and plays along with its own note.
        Assert.Equal(12, result.InstrumentFor(synth).Events.Notes().Count());
        var part = result.PartFor(toHardware);
        Assert.Equal("B", part.Port);
        Assert.Equal([72, 76, 79, 72, 76, 79, 72, 76, 79, 72, 76, 79, 60], part.Events.Notes().Select(n => (int)n.Note.Value));
        Assert.All(part.Events.Notes(), n => Assert.Equal(Two, n.Channel));
        Assert.Empty(result.Diagnostics.Where(d => d.Code is SignalDiagnosticCode.InvalidConnection or SignalDiagnosticCode.Feedback));
    }

    [Fact]
    public void Scenario3_TheEndOfATrackChain_CanFeedAnotherTrack()
    {
        var source = TrackOf("Source", Note(0, 60));
        var follower = TrackOf("Follower");
        var project = NewProject()
            .With(source, BuiltInDevices.CreateTranspose(5))
            .With(follower, BuiltInDevices.CreateTranspose(5))
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(source.Id), SignalNode.Track(follower.Id))
            .Connect(out var out1, SignalNode.Track(follower.Id), SignalNode.ExternalPart(mu2000.Id));

        var part = project.Evaluate().PartFor(out1);

        Assert.Equal(70, Assert.Single(part.Events.Notes()).Note.Value);
    }

    [Fact]
    public void Scenario4_SeveralTracks_ShareOneProcessingChain()
    {
        var piano = TrackOf("Piano", Note(0, 60, channel: 1), Note(480, 62, channel: 1));
        var strings = TrackOf("Strings", Note(0, 55, channel: 2), Note(240, 57, channel: 2));
        var bass = TrackOf("Bass", Note(0, 36, channel: 3));
        var project = NewProject()
            .With(piano)
            .With(strings)
            .With(bass)
            .WithRack(out var rack, "Shared FX", BuiltInDevices.CreateTranspose(12))
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(piano.Id), SignalNode.Rack(rack.Id))
            .Connect(SignalNode.Track(strings.Id), SignalNode.Rack(rack.Id))
            .Connect(SignalNode.Track(bass.Id), SignalNode.Rack(rack.Id))
            .Connect(out var output, SignalNode.Rack(rack.Id), SignalNode.ExternalPart(mu2000.Id));

        var result = project.Evaluate();

        // One rack, one output: every track's notes, transposed once, merged in canonical order. At tick 0
        // the tracks' order breaks the tie, and each note keeps its own channel.
        var part = result.PartFor(output);
        Assert.Equal([(0L, 72, 1), (0L, 67, 2), (0L, 48, 3), (240L, 69, 2), (480L, 74, 1)], part.Events.Notes().Select(n => (n.Position.Value, (int)n.Note.Value, n.Channel.Number)));
        Assert.Equal([0, 1, 2, 1, 0], part.Events.Select(e => e.Origin));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == SignalDiagnosticCode.Unrouted);
    }

    [Fact]
    public void Scenario4_ASharedChainHasOneState_SoAnArpeggiatorPlaysEveryTracksNotesTogether()
    {
        var low = TrackOf("Low", Note(0, 48, length: 480));
        var high = TrackOf("High", Note(0, 72, length: 480));
        var synth = Instance(TestDevices.Synth);
        var project = NewProject()
            .With(low)
            .With(high)
            .WithRack(out var rack, "Shared arp", BuiltInDevices.CreateArpeggiator(rate: 2), synth)
            .Connect(SignalNode.Track(low.Id), SignalNode.Rack(rack.Id))
            .Connect(SignalNode.Track(high.Id), SignalNode.Rack(rack.Id));

        var feed = project.Evaluate().InstrumentFor(synth);

        Assert.Equal(rack.Id, feed.Chain);
        Assert.Equal([48, 72, 48, 72], feed.Events.Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void Scenario6_ANoteProcessor_DoesNotDestroySysExOrControllers()
    {
        var sysEx = XgOn(0);
        var volume = Volume(0, 100);
        var program = Program(0, 48);
        var bend = new PitchBendEvent(new Tick(240), MidiChannel.FromNumber(1), ControlValue.Center);
        var split = new RawMidiEvent(EventId.New(), new Tick(480), ByteBlock.Copy([0xF0, 0x41, 0x10]));
        var keys = TrackOf("Keys", sysEx, volume, program, Note(0, 60, length: 480), bend, split);
        var project = NewProject()
            .With(keys, BuiltInDevices.CreateArpeggiator(rate: 2), BuiltInDevices.CreateTranspose(12))
            .WithInstrument(out var mu2000)
            .Connect(out var route, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id));

        var events = project.Evaluate().PartFor(route).Events.Select(e => e.Event).ToList();

        Assert.Same(sysEx, events[0]);
        Assert.Same(program, events[1]);
        Assert.Same(volume, events[2]);
        Assert.Contains(bend, events);
        Assert.Same(split, events[^1]);
        Assert.Equal([72, 72, 72, 72], events.OfType<NoteEvent>().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void Scenario6_OnlyAnExplicitFilter_RemovesSystemExclusive()
    {
        var keys = TrackOf("Keys", XgOn(0), Volume(0, 100), Note(0, 60));
        var project = NewProject()
            .With(keys, BuiltInDevices.CreateEventFilter(EventClass.SystemExclusive))
            .WithInstrument(out var mu2000)
            .Connect(out var route, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id));

        var events = project.Evaluate().PartFor(route).Events.Select(e => e.Event).ToList();

        Assert.DoesNotContain(events, e => e is SysExEvent);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Scenario6_TheBundleGoesPastASoftwareInstrument_ToWhateverFollowsTheTrack()
    {
        var sysEx = XgOn(0);
        var synth = Instance(TestDevices.Synth);
        var keys = TrackOf("Keys", sysEx, Note(0, 60));
        var project = NewProject()
            .With(keys, synth)
            .WithInstrument(out var mu2000)
            .Connect(out var route, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id));

        var result = project.Evaluate();

        // The plugin plays the note and passes it on with the SysEx, so the hardware part layers with it (ADR 0037).
        Assert.Equal(2, result.PartFor(route).Events.Length);
        Assert.Contains(sysEx, result.PartFor(route).Events.Select(e => e.Event));
        Assert.Equal(60, Assert.Single(result.PartFor(route).Events.Notes()).Note.Value);
        Assert.Equal(60, Assert.Single(result.InstrumentFor(synth).Events.Notes()).Note.Value);
    }

    [Fact]
    public void TwoSoftwareInstrumentsOnOneTrack_BothPlayItsNotes()
    {
        var first = Instance(TestDevices.Synth);
        var second = Instance(TestDevices.Synth);
        var keys = TrackOf("Keys", Note(0, 60));
        var project = NewProject().With(keys, first, second);

        var result = project.Evaluate();

        Assert.Equal(60, Assert.Single(result.InstrumentFor(first).Events.Notes()).Note.Value);
        Assert.Equal(60, Assert.Single(result.InstrumentFor(second).Events.Notes()).Note.Value);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == SignalDiagnosticCode.Unrouted);
    }

    [Fact]
    public void Scenario10_AFeedbackLoop_IsDetected_AndLeftOut_WhileTheRestStillPlays()
    {
        var a = TrackOf("A", Note(0, 60));
        var b = TrackOf("B", Note(0, 62));
        var c = TrackOf("C", Note(0, 64));
        var project = NewProject()
            .With(a)
            .With(b)
            .With(c)
            .WithInstrument(out var mu2000)
            .Connect(out var aToB, SignalNode.Track(a.Id), SignalNode.Track(b.Id))
            .Connect(out var bToA, SignalNode.Track(b.Id), SignalNode.Track(a.Id))
            .Connect(out var cOut, SignalNode.Track(c.Id), SignalNode.ExternalPart(mu2000.Id));

        var result = project.Evaluate();

        Assert.Contains(project.Validate(), i => i.Code == RoutingIssueCode.Cycle);
        var leftOut = result.Diagnostics.Where(d => d.Code == SignalDiagnosticCode.InvalidConnection).Select(d => d.Connection).ToList();
        Assert.Contains(aToB.Id, leftOut);
        Assert.Contains(bToA.Id, leftOut);
        Assert.Contains("feed back", result.Diagnostics.First(d => d.Connection == bToA.Id).Message, StringComparison.Ordinal);
        Assert.Equal(64, Assert.Single(result.PartFor(cOut).Events.Notes()).Note.Value);
    }

    [Fact]
    public void Scenario10_AnInvalidConnection_IsDetected_AndLeftOut()
    {
        var keys = TrackOf("Keys", Note(0));
        var synth = Instance(TestDevices.Synth);
        var project = NewProject()
            .With(keys, synth)
            .WithMixerChannel(out var channel)
            .Connect(out var eventsToMixer, SignalNode.Track(keys.Id), SignalNode.Mixer(channel.Id))
            .Connect(out var audioToHardware, SignalNode.Track(keys.Id), SignalNode.ExternalPart(ExternalInstrumentId.New()), kind: SignalKind.Audio);

        var result = project.Evaluate();

        var leftOut = result.Diagnostics.Where(d => d.Code == SignalDiagnosticCode.InvalidConnection).ToList();
        Assert.Contains(leftOut, d => d.Connection == eventsToMixer.Id && d.Message.Contains("does not take events", StringComparison.Ordinal));
        Assert.Empty(result.ExternalParts);
        Assert.Equal(60, Assert.Single(result.InstrumentFor(synth).Events.Notes()).Note.Value);
        Assert.Contains(leftOut, d => d.Connection == audioToHardware.Id);
    }

    [Fact]
    public void Scenario10_AFeedbackLoopThroughADeviceTap_IsDetected()
    {
        var arp = BuiltInDevices.CreateArpeggiator();
        var a = TrackOf("A", Note(0, 60));
        var b = TrackOf("B");
        var project = NewProject()
            .With(a, arp)
            .With(b)
            .Connect(SignalNode.Device(arp.Id), SignalNode.Track(b.Id))
            .Connect(SignalNode.Track(b.Id), SignalNode.Track(a.Id));

        var result = project.Evaluate();

        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == SignalDiagnosticCode.InvalidConnection));
    }

    [Fact]
    public void AConnectionsMapping_FiltersAndRemapsChannels_WithoutTouchingTheClips()
    {
        var multi = TrackOf("Imported", Note(0, 60, channel: 1), Note(0, 62, channel: 2), Note(0, 64, channel: 10), XgOn(0));
        var mapping = new ChannelMapping { Only = [MidiChannel.FromNumber(1), MidiChannel.FromNumber(10)], Remap = [new ChannelRemap(MidiChannel.FromNumber(1), Four)] };
        var project = NewProject()
            .With(multi)
            .WithInstrument(out var mu2000)
            .Connect(out var route, SignalNode.Track(multi.Id), SignalNode.ExternalPart(mu2000.Id), mapping);

        var part = project.Evaluate().PartFor(route);

        Assert.Equal([(60, 4), (64, 10)], part.Events.Notes().Select(n => ((int)n.Note.Value, n.Channel.Number)));
        Assert.Single(part.Events, e => e.Event is SysExEvent);
        Assert.Null(part.ForcedChannel);
        Assert.Equal([1, 2, 10], project.Sequence.Tracks[0].ArrangedEvents.OfType<NoteEvent>().Select(n => n.Channel.Number));
    }

    [Fact]
    public void OneTrack_CanFeedSeveralDestinations()
    {
        var keys = TrackOf("Keys", Note(0, 60));
        var synth = Instance(TestDevices.Synth);
        var project = NewProject()
            .With(keys)
            .WithRack(out var rack, "Softsynth", synth)
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(keys.Id), SignalNode.Rack(rack.Id))
            .Connect(out var hardware, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id), ChannelMapping.ForceTo(Four));

        var result = project.Evaluate();

        Assert.Equal(1, Assert.Single(result.InstrumentFor(synth).Events.Notes()).Channel.Number);
        Assert.Equal(4, Assert.Single(result.PartFor(hardware).Events.Notes()).Channel.Number);
    }

    [Fact]
    public void ChannelOverride_IsTheOneChannelTheTracksConnectionsForce()
    {
        var keys = TrackOf("Keys", Note(0));
        var project = NewProject().With(keys).WithInstrument(out var mu2000);
        var single = project.Connect(SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id), ChannelMapping.ForceTo(Four));
        var two = single.Connect(SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id, "B"), ChannelMapping.ForceTo(Two));
        var preserving = project.Connect(SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id));

        Assert.Equal(Four, SignalGraph.ChannelOverride(single.Connections, keys.Id));
        Assert.Null(SignalGraph.ChannelOverride(two.Connections, keys.Id));
        Assert.Null(SignalGraph.ChannelOverride(preserving.Connections, keys.Id));
    }

    [Fact]
    public void ChannelOverride_IgnoresConnectionsThatAreLeftOut()
    {
        // A lane for CC 7 on channel 4 replaces the clip's CC 7 on channel 1 only when the track's events go
        // out on channel 4. The second, invalid connection (to an instrument that does not exist) forces
        // channel 2; it is left out, so it must not cancel the override.
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForController(Four, new ControllerNumber(7)), [new AutomationPoint(new Tick(0), ControlValue.Max)]);
        var keys = TrackOf("Keys", Volume(0, 10, channel: 1), Note(0)).WithAutomation([lane]);
        var project = NewProject()
            .With(keys)
            .WithInstrument(out var mu2000)
            .Connect(out var valid, SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id), ChannelMapping.ForceTo(Four))
            .Connect(SignalNode.Track(keys.Id), SignalNode.ExternalPart(ExternalInstrumentId.New()), ChannelMapping.ForceTo(Two));

        var result = project.Evaluate();

        Assert.Contains(result.Diagnostics, d => d.Code == SignalDiagnosticCode.ReplacedByAutomation);
        var volumes = result.PartFor(valid).Events.Select(e => e.Event).OfType<ControllerEvent>().ToList();
        Assert.Equal(ControlValue.Max, Assert.Single(volumes).Value);
    }

    [Fact]
    public void ATapOnAChainWithoutATrack_IsLeftOut_AndEvaluationFinishes()
    {
        var arp = BuiltInDevices.CreateArpeggiator();
        var orphan = DeviceChain.Create(ChainOwner.ForTrack(TrackId.New())) with { Devices = [arp] };
        var listener = TrackOf("Listener", Note(0));
        var project = NewProject()
            .With(listener)
            .WithChain(orphan)
            .WithInstrument(out var mu2000)
            .Connect(out var tap, SignalNode.Device(arp.Id), SignalNode.Track(listener.Id))
            .Connect(out var output, SignalNode.Track(listener.Id), SignalNode.ExternalPart(mu2000.Id));

        var result = project.Evaluate();

        Assert.Contains(result.Diagnostics, d => d.Code == SignalDiagnosticCode.InvalidConnection && d.Connection == tap.Id);
        Assert.Single(result.PartFor(output).Events);
    }

    [Fact]
    public void MutedTracks_SendNothing_AndSoloOverridesMute()
    {
        var muted = Track.FromEvents(TrackId.New(), "Muted", [Note(0, 60)], isMuted: true);
        var plain = TrackOf("Plain", Note(0, 62));
        var soloed = Track.FromEvents(TrackId.New(), "Soloed", [Note(0, 64)], isMuted: true, isSoloed: true);
        var project = NewProject().With(muted).With(plain).WithInstrument(out var mu2000);
        project = project
            .Connect(out var mutedOut, SignalNode.Track(muted.Id), SignalNode.ExternalPart(mu2000.Id))
            .Connect(out var plainOut, SignalNode.Track(plain.Id), SignalNode.ExternalPart(mu2000.Id));

        var withoutSolo = project.Evaluate();
        var withSolo = project.With(soloed).Connect(out var soloOut, SignalNode.Track(soloed.Id), SignalNode.ExternalPart(mu2000.Id)).Evaluate();

        Assert.Empty(withoutSolo.PartFor(mutedOut).Events);
        Assert.Single(withoutSolo.PartFor(plainOut).Events);
        Assert.Empty(withSolo.PartFor(plainOut).Events);
        Assert.Single(withSolo.PartFor(soloOut).Events);
    }

    [Fact]
    public void AGroupsMuteAndSolo_ReachItsMembers_ButNotTracksMerelyRoutedIntoIt()
    {
        var band = Track.Create("Band", TrackRole.Group);
        var bass = TrackOf("Bass", Note(0, 40)).WithGroup(band.Id);
        var keys = TrackOf("Keys", Note(0, 60)).WithGroup(band.Id);
        var guest = TrackOf("Guest", Note(0, 72));
        var project = NewProject().With(band).With(bass).With(keys).With(guest).WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(bass.Id), SignalNode.Track(band.Id))
            .Connect(SignalNode.Track(keys.Id), SignalNode.Track(band.Id))
            .Connect(SignalNode.Track(guest.Id), SignalNode.Track(band.Id))
            .Connect(out var output, SignalNode.Track(band.Id), SignalNode.ExternalPart(mu2000.Id));
        Project Edit(Track track) => project with { Sequence = project.Sequence.WithTrack(track) };

        var open = project.Evaluate().PartFor(output).Events.Notes().Select(n => (int)n.Note.Value).Order();
        var muted = Edit(band.WithMuted(true)).Evaluate().PartFor(output).Events.Notes().Select(n => (int)n.Note.Value);
        var soloed = Edit(band.WithSoloed(true)).Evaluate().PartFor(output).Events.Notes().Select(n => (int)n.Note.Value).Order();
        var memberSoloed = Edit(band.WithMuted(true)) with { Sequence = Edit(band.WithMuted(true)).Sequence.WithTrack(keys.WithSoloed(true)) };

        Assert.Equal([40, 60, 72], open);

        // Decision 11 still holds for anything routed in: only members' own content is muted by their group.
        Assert.Equal([72], muted);
        Assert.Equal([40, 60], soloed);
        Assert.Equal([60], memberSoloed.Evaluate().PartFor(output).Events.Notes().Select(n => (int)n.Note.Value));
    }

    [Fact]
    public void DeviceAutomation_IsAppliedToBuiltIns_AndHandedOutForEveryOtherDevice()
    {
        var transpose = BuiltInDevices.CreateTranspose(0);
        var synth = Instance(TestDevices.Synth);
        var semitones = BuiltInDevices.Transpose.Parameters[0];
        var transposeLane = new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForDevice(transpose.Id, BuiltInDevices.TransposeSemitones),
            [new AutomationPoint(new Tick(0), semitones.ToStored(0), AutomationCurve.Hold), new AutomationPoint(new Tick(240), semitones.ToStored(12), AutomationCurve.Hold)]);
        var releaseLane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(synth.Id, new ParameterId(1)), [new AutomationPoint(new Tick(0), ControlValue.Max, AutomationCurve.Hold)]);
        var keys = TrackOf("Keys", Note(0, 60), Note(480, 60)).WithAutomation([transposeLane, releaseLane]);
        var project = NewProject().With(keys, transpose, synth);

        var result = project.Evaluate();

        Assert.Equal([60, 72], result.InstrumentFor(synth).Events.Notes().Select(n => (int)n.Note.Value));
        var parameters = Assert.Single(result.Parameters);
        Assert.Equal(synth.Id, parameters.Device);
        Assert.All(parameters.Changes, c => Assert.Equal(ControlValue.Max, c.Value));
    }

    [Fact]
    public void DeviceAutomation_StillRunsOnAMutedTrack_BecauseItIsNotSignal()
    {
        var synth = Instance(TestDevices.Synth);
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(synth.Id, new ParameterId(1)), [new AutomationPoint(new Tick(0), ControlValue.Max, AutomationCurve.Hold)]);
        var keys = Track.FromEvents(TrackId.New(), "Keys", [Note(0)], isMuted: true).WithAutomation([lane]);

        var result = NewProject().With(keys, synth).Evaluate();

        Assert.Empty(result.InstrumentFor(synth).Events);
        Assert.Equal(synth.Id, Assert.Single(result.Parameters).Device);
    }

    [Fact]
    public void AutomationOfAMissingDevice_IsReported()
    {
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(DeviceId.New(), new ParameterId(1)), [new AutomationPoint(new Tick(0), ControlValue.Max)]);
        var keys = TrackOf("Keys").WithAutomation([lane]);

        var result = NewProject().With(keys).Evaluate();

        Assert.Contains(result.Diagnostics, d => d.Code == SignalDiagnosticCode.AutomationTargetMissing);
        Assert.Empty(result.Parameters);
    }

    [Fact]
    public void ATrackWithNowhereToGo_IsReportedAsNotRouted()
    {
        var keys = TrackOf("Keys", Note(0));

        var result = NewProject().With(keys).Evaluate();

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(SignalDiagnosticCode.Unrouted, diagnostic.Code);
        Assert.Equal(keys.Id, diagnostic.Track);
    }

    [Fact]
    public void EventsThatPassATracksInstrument_AndGoNowhere_AreReported()
    {
        var synth = Instance(TestDevices.Synth);
        var keys = TrackOf("Keys", XgOn(0), Note(0));

        var result = NewProject().With(keys, synth).Evaluate();

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == SignalDiagnosticCode.Unrouted);
        Assert.Contains("1 events that pass the track's instrument", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrackWhoseInstrumentTakesEverything_IsNotReportedAsUnrouted()
    {
        var synth = Instance(TestDevices.Synth);

        var result = NewProject().With(TrackOf("Keys", Note(0), Volume(0, 90)), synth).Evaluate();

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == SignalDiagnosticCode.Unrouted);
    }

    [Fact]
    public void TransposedAwayNotes_AreReportedAgainstTheirTrack()
    {
        var keys = TrackOf("Keys", Note(0, 120));
        var project = NewProject()
            .With(keys, BuiltInDevices.CreateTranspose(12))
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(keys.Id), SignalNode.ExternalPart(mu2000.Id));

        var report = Assert.Single(project.Evaluate().Diagnostics);

        Assert.Equal(SignalDiagnosticCode.DeviceReport, report.Code);
        Assert.Equal(keys.Id, report.Track);
    }

    [Fact]
    public void Evaluation_IsDeterministic()
    {
        var piano = Chord("Piano");
        var bass = TrackOf("Bass", Note(0, 36), XgOn(0), Volume(120, 80));
        var project = NewProject()
            .With(piano, BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.UpDown, 2))
            .With(bass)
            .WithRack(out var rack, "Shared", BuiltInDevices.CreateTranspose(3))
            .WithInstrument(out var mu2000)
            .Connect(SignalNode.Track(piano.Id), SignalNode.Rack(rack.Id))
            .Connect(SignalNode.Track(bass.Id), SignalNode.Rack(rack.Id))
            .Connect(SignalNode.Rack(rack.Id), SignalNode.ExternalPart(mu2000.Id));

        static ImmutableArray<SignalEvent> Events(SignalGraphResult r) => r.ExternalParts.Single().Events;

        Assert.Equal(Events(project.Evaluate()), Events(project.Evaluate()));
    }
}
