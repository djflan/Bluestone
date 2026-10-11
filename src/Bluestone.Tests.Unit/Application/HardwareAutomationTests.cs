using System.Text;
using Bluestone.Application.Editing;
using Bluestone.Application.Routing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Files;
using Bluestone.Playback;
using Bluestone.Profiles;
using Bluestone.Signal;
using Bluestone.Tests.Unit.Playback;
using static Bluestone.Tests.Unit.Application.RouteResolverTests;

namespace Bluestone.Tests.Unit.Application;

/// <summary>Automation of external instrument parameters becomes CC, RPN, NRPN, or SysEx on the part (ADR 0031).</summary>
public sealed class HardwareAutomationTests
{
    private const string ProfileId = "test.hardware";

    private static readonly EndpointDescriptor SynthOut = Output("loopback", "s", "Synth");
    private static readonly EndpointDescriptor OtherOut = Output("loopback", "o", "Other");
    private static readonly MidiChannel Three = MidiChannel.FromNumber(3);

    private static readonly ProfileCatalog Profiles = ProfileCatalog.Empty.Add("test", ProfileLoader.Load(Encoding.UTF8.GetBytes($$"""
        { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "{{ProfileId}}", "version": "1", "name": "Test Hardware",
          "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
          "parameterMap": [
            { "id": "volume", "name": "Volume", "kind": "cc", "controller": 7 },
            { "id": "cutoff", "name": "Filter Cutoff", "kind": "nrpn", "msb": 1, "lsb": 32 },
            { "id": "bend-range", "name": "Bend Range", "kind": "rpn", "msb": 0, "lsb": 0, "max": 24 },
            { "id": "part-volume", "name": "Part Volume", "kind": "sysex", "bytes": "F0 43 {device} 4C 08 {channel} 0B {value} F7", "device": 16 }
          ] }
        """)));

    private static NoteEvent Note(long at) => new(new Tick(at), new TickSpan(10), Three, new NoteNumber(60), new Velocity(100));

    private static ExternalInstrument Synth(EndpointDescriptor? endpoint = null) =>
        ExternalInstrument.Create("Synth", RouteResolver.ReferenceTo(endpoint ?? SynthOut), new ProfileReference(ProfileId));

    private static AutomationLane Lane(ExternalInstrument instrument, string parameter, params (long Tick, double Value)[] points) =>
        new(AutomationLaneId.New(), AutomationTarget.ForInstrument(instrument.Id, "A", Three, parameter),
            points.Select(p => new AutomationPoint(new Tick(p.Tick), ControlValue.FromFraction(p.Value), AutomationCurve.Hold)));

    private static AutomationLane Ramp(ExternalInstrument instrument, string parameter, long length) =>
        new(AutomationLaneId.New(), AutomationTarget.ForInstrument(instrument.Id, "A", Three, parameter),
            [new AutomationPoint(Tick.Zero, ControlValue.Min, AutomationCurve.Linear), new AutomationPoint(new Tick(length), ControlValue.Max, AutomationCurve.Hold)]);

    /// <summary>One track playing notes on channel 3 through the synth's port A, with <paramref name="lanes"/>.</summary>
    private static Project Build(ExternalInstrument instrument, IEnumerable<TrackEvent> events, params AutomationLane[] lanes)
    {
        var track = Track.FromEvents(TrackId.New(), "Lead", events).WithAutomation(lanes);
        var project = Project.CreateNew("hw");
        return project with
        {
            Sequence = project.Sequence.WithTrack(track),
            Instruments = [instrument],
            Connections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.ExternalPart(instrument.Id, "A"))],
        };
    }

    private static PreparedRouting Prepare(Project project, params EndpointDescriptor[] endpoints) =>
        PlaybackRouting.Prepare(project, DeviceCatalog.BuiltIn, Profiles, endpoints.Length == 0 ? [SynthOut] : endpoints);

    private static PlaybackPlan Compile(Project project, params EndpointDescriptor[] endpoints) => Prepare(project, endpoints).Compile(project.Sequence);

    // Every message in plan order as "slot tick hex".
    private static List<string> Sent(PlaybackPlan plan) =>
        [.. plan.Events.Select(e => e.PayloadIndex >= 0
            ? $"{e.Slot} {e.Tick} {Convert.ToHexString(plan.Payloads[e.PayloadIndex].Span)}"
            : $"{e.Slot} {e.Tick} {Hex(e.Message.Status, e.Message.Data1, e.Message.Data2, e.Message.Length)}")];

    private static string Hex(byte status, byte data1, byte data2, int length) =>
        length == 2 ? $"{status:X2}{data1:X2}" : $"{status:X2}{data1:X2}{data2:X2}";

    private static string Chased(ChaseMessage m) =>
        m.Payload is { } payload ? Convert.ToHexString(payload.Span) : Hex(m.Message.Status, m.Message.Data1, m.Message.Data2, m.Message.Length);

    [Fact]
    public void Values_SendOnlyChanges()
    {
        var synth = Synth();
        var mapping = Profiles.Find(ProfileId)!.FindMapping("volume")!;
        var lane = Lane(synth, "volume", (0, 0.5), (960, 0.5), (1920, 1.0));

        var values = HardwareAutomation.Values(lane, mapping, TempoMap.Constant(Ppqn.Default, Tempo.Default)).ToList();

        Assert.Equal([(Tick.Zero, 64), (new Tick(1920), 127)], values);
    }

    [Fact]
    public void Values_ARamp_IsThinnedToTheMinimumInterval_AndEndsOnItsLastValue()
    {
        var tempo = TempoMap.Constant(Ppqn.Default, Tempo.FromBeatsPerMinute(240));
        var mapping = Profiles.Find(ProfileId)!.FindMapping("volume")!;
        var lane = Ramp(Synth(), "volume", 4 * Ppqn.Default.TicksPerQuarterNote);
        var unthinned = HardwareAutomation.Values(lane, mapping, tempo, TimeSpan.Zero).ToList();

        var values = HardwareAutomation.Values(lane, mapping, tempo).ToList();

        Assert.True(values.Count < unthinned.Count);
        Assert.Equal(127, unthinned[^1].Value);
        Assert.Equal(127, values[^1].Value);
        Assert.InRange(values[^1].Position.Value, 3840, 3840 + 80);
        for (var i = 1; i < values.Count; i++)
        {
            Assert.True(values[i].Value > values[i - 1].Value);
            Assert.True(tempo.TimeAt(values[i].Position) - tempo.TimeAt(values[i - 1].Position) >= HardwareAutomation.MinimumInterval, $"{values[i - 1]} → {values[i]}");
        }
    }

    [Fact]
    public void Values_WithoutAMinimumInterval_SendEveryChange()
    {
        var mapping = Profiles.Find(ProfileId)!.FindMapping("bend-range")!;
        var lane = Ramp(Synth(), "bend-range", 960);

        var values = HardwareAutomation.Values(lane, mapping, TempoMap.Constant(Ppqn.Default, Tempo.Default), TimeSpan.Zero).Select(v => v.Value).ToList();

        Assert.Equal(Enumerable.Range(0, 25), values);
    }

    [Fact]
    public void Plan_TranslatesEachKindOntoThePartsEndpointAndChannel()
    {
        var synth = Synth();
        var project = Build(synth, [], Lane(synth, "volume", (0, 1.0)), Lane(synth, "cutoff", (0, 0.5)), Lane(synth, "bend-range", (0, 0.5)), Lane(synth, "part-volume", (0, 0.5)));

        var prepared = Prepare(project);
        var sent = Sent(prepared.Compile(project.Sequence));

        Assert.Empty(prepared.Diagnostics);
        Assert.Equal([SynthOut.Id], prepared.Slots);
        Assert.Equal(
            [
                // SysEx sends in its own phase, before controllers at the same tick.
                "0 0 F043104C08020B40F7",
                "0 0 B2077F",
                "0 0 B26301", "0 0 B26220", "0 0 B20640", "0 0 B2657F", "0 0 B2647F",
                "0 0 B26500", "0 0 B26400", "0 0 B2060C", "0 0 B2657F", "0 0 B2647F",
            ],
            sent);
    }

    [Fact]
    public void Plan_PutsTranslatedAutomationAfterTheTracksOwnControllers_AndBeforeItsNotes()
    {
        var synth = Synth();
        var clipVolume = new ControllerEvent(Tick.Zero, Three, new ControllerNumber(7), ControlValue.FromSevenBit(10));
        var project = Build(synth, [Note(0), clipVolume], Lane(synth, "cutoff", (0, 1.0)));

        var sent = Sent(Compile(project));

        Assert.Equal(["0 0 B2070A", "0 0 B26301", "0 0 B26220", "0 0 B2067F", "0 0 B2657F", "0 0 B2647F", "0 0 923C64"], sent);
    }

    [Fact]
    public void Plan_SendsAMutedTracksHardwareAutomation()
    {
        var synth = Synth();
        var project = Build(synth, [Note(0)], Lane(synth, "volume", (0, 1.0)));
        var muted = project with { Sequence = project.Sequence.WithTrack(project.Sequence.Tracks[0].WithMuted(true)) };

        Assert.Equal(["0 0 B2077F"], Sent(Compile(muted)));
    }

    [Fact]
    public void Plan_GivesAnUnconnectedPartsEndpointASlotAfterTheOthers()
    {
        var other = ExternalInstrument.Create("Other", RouteResolver.ReferenceTo(OtherOut));
        var synth = Synth();
        var project = Build(other, [Note(0)]);
        project = project with
        {
            Instruments = [other, synth],
            Sequence = project.Sequence.WithTrack(project.Sequence.Tracks[0].WithAutomation([Lane(synth, "volume", (0, 1.0))])),
        };

        var prepared = Prepare(project, SynthOut, OtherOut);

        Assert.Equal([OtherOut.Id, SynthOut.Id], prepared.Slots);
        Assert.Equal(1, Assert.Single(prepared.ParameterParts).OutputSlot);
        Assert.Equal(["1 0 B2077F", "0 0 923C64"], Sent(prepared.Compile(project.Sequence)));
    }

    [Fact]
    public void Plan_ReportsLanesItCannotSend_AndSendsTheRest()
    {
        var synth = Synth();
        var unprofiled = ExternalInstrument.Create("Bare", RouteResolver.ReferenceTo(SynthOut));
        var gone = ExternalInstrument.Create("Gone", new EndpointReference("loopback", "gone", "Gone"), new ProfileReference(ProfileId));
        var project = Build(synth, [], Lane(synth, "volume", (0, 1.0)), Lane(synth, "resonance", (0, 1.0)), Lane(unprofiled, "volume", (0, 1.0)), Lane(gone, "volume", (0, 1.0)));
        var second = Track.Create("Second").WithAutomation([Lane(synth, "volume", (0, 0.0))]);
        project = project with { Instruments = [synth, unprofiled, gone], Sequence = project.Sequence.WithTrack(second) };

        var prepared = Prepare(project);

        Assert.Equal(["0 0 B2077F"], Sent(prepared.Compile(project.Sequence)));
        // The second track has no connection of its own, so it is also "not routed" (handoff decision 15).
        Assert.Collection(
            prepared.Diagnostics.Where(d => d.Message != PlaybackRouting.NotRoutedMessage),
            d => Assert.Contains("does not define a parameter \"resonance\"", d.Message, StringComparison.Ordinal),
            d => Assert.Contains("Bare has no device profile", d.Message, StringComparison.Ordinal),
            d => Assert.Contains("port A of Gone has no available endpoint", d.Message, StringComparison.Ordinal),
            d => Assert.Equal((second.Id, "Automation of Volume on channel 3 is not sent: another lane already controls it on the same part."), (d.Track, d.Message)));
    }

    [Fact]
    public void Chase_SendsTheLatestWholeSequence_OfEachParameter()
    {
        var synth = Synth();
        var project = Build(synth, [Note(2000)], Lane(synth, "cutoff", (0, 0.25), (960, 0.75)), Lane(synth, "part-volume", (0, 1.0)), Lane(synth, "volume", (0, 1.0)));
        var plan = Compile(project);

        var early = ChaseState.Compute(plan, 500).Select(Chased).ToList();
        var late = ChaseState.Compute(plan, 1500).Select(Chased).ToList();

        // The controller mapping is chased as a controller, once; the NRPN and SysEx parameters whole, after it.
        Assert.Equal(["B2077F", "B26301", "B26220", "B20620", "B2657F", "B2647F", "F043104C08020B7FF7"], early);
        Assert.Equal(["B2077F", "B26301", "B26220", "B2065F", "B2657F", "B2647F", "F043104C08020B7FF7"], late);
        Assert.Empty(ChaseState.Compute(plan, 0));
    }

    [Fact]
    public void LoopWrap_RestoresAParameterSetBeforeTheLoop_AndLeavesOneSetAtItsStart()
    {
        var synth = Synth();
        var project = Build(synth, [], Lane(synth, "cutoff", (0, 0.25), (1000, 0.75)));
        var plan = Compile(project);

        var wrap = ChaseState.AtWrap(plan, new LoopRegion(new Tick(500), new Tick(1500))).Select(Chased).ToList();

        Assert.Equal(["B26301", "B26220", "B20620", "B2657F", "B2647F"], wrap);
        Assert.Empty(ChaseState.AtWrap(plan, new LoopRegion(new Tick(1000), new Tick(1500))));
        Assert.Empty(ChaseState.AtWrap(plan, new LoopRegion(new Tick(100), new Tick(900))));
    }

    [Fact]
    public void Play_FromMidSong_SendsChasedSysExBeforeTheMusic()
    {
        var synth = Synth();
        var project = Build(synth, [Note(1000)], Lane(synth, "part-volume", (0, 1.0)));
        using var f = new PlaybackFixture();
        f.Engine.Load(Compile(project));

        f.Engine.Play(new Tick(960));
        f.PumpAt(0);

        Assert.Equal("F043104C08020B7FF7@0", f.Sent()[0]);
    }

    [Fact]
    public async Task Locate_WhileAnotherPlanIsLoaded_SendsOnlyTheChasedPlansMessages()
    {
        // A chases its part-volume SysEx; B's only payload is a SysEx far beyond the playhead, so it must never be sent.
        var synth = Synth();
        var a = Compile(Build(synth, [Note(0)], Lane(synth, "part-volume", (0, 1.0))));
        var b = Compile(Build(synth, [Note(0), new SysExEvent(new Tick(100_000), SysExMessage.Create([0xF0, 0x7D, 0x01, 0xF7]))]));
        using var f = new PlaybackFixture();
        f.Engine.Load(a);
        f.Engine.Play(new Tick(960));
        f.PumpAt(0);

        // A Seek computes its chase from the plan loaded when it is called; a Load can reach the playback thread first.
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        var loader = Task.Run(() =>
        {
            while (DateTime.UtcNow < deadline)
            {
                f.Engine.Load(a);
                f.Engine.Load(b);
            }
        }, TestContext.Current.CancellationToken);
        var seeker = Task.Run(() =>
        {
            while (DateTime.UtcNow < deadline)
            {
                f.Engine.Seek(new Tick(960));
            }
        }, TestContext.Current.CancellationToken);
        while (!loader.IsCompleted || !seeker.IsCompleted)
        {
            f.PumpAt(0);
        }

        await Task.WhenAll(loader, seeker);
        f.PumpAt(0);

        var sent = f.Sent();
        Assert.Contains(sent, m => m.StartsWith("F043104C08020B7FF7", StringComparison.Ordinal));
        Assert.DoesNotContain(sent, m => m.StartsWith("F07D01F7", StringComparison.Ordinal));
    }

    [Fact]
    public void Export_WritesTranslatedAutomationAsMidi()
    {
        var synth = Synth();
        var project = Build(synth, [Note(0)], Lane(synth, "cutoff", (0, 1.0)), Lane(synth, "part-volume", (480, 0.5)));

        var export = SmfExporter.Export(project.Sequence, null, HardwareAutomation.ForExport(project, Profiles));
        var track = SmfReader.Read(SmfWriter.Write(export.File)).File.Tracks.ElementAt(1);

        Assert.DoesNotContain(export.Diagnostics, d => d.Code == SmfDiagnosticCodes.DeviceAutomationNotExported);
        var written = track.Events.Select(e => e switch
        {
            SmfChannelEvent { Message.Kind: Bluestone.Midi.Wire.ChannelMessageKind.ControlChange } c => $"{e.Tick} {Hex(c.Message.Status, c.Message.Data1, c.Message.Data2, c.Message.Length)}",
            SmfSysExEvent s => $"{e.Tick} F0{Convert.ToHexString(s.Data.Span)}",
            _ => null,
        }).OfType<string>().ToList();
        // A lane holds its first value from tick 0, and SysEx goes before controllers at the same tick.
        Assert.Equal(["0 F043104C08020B40F7", "0 B26301", "0 B26220", "0 B2067F", "0 B2657F", "0 B2647F"], written);
    }

    [Fact]
    public void Export_ReportsLanesItCannotTranslate()
    {
        var synth = Synth();
        var project = Build(synth, [Note(0)], Lane(synth, "volume", (0, 1.0)), Lane(synth, "resonance", (0, 1.0)));

        var withProfiles = SmfExporter.Export(project.Sequence, null, HardwareAutomation.ForExport(project, Profiles));
        var without = SmfExporter.Export(project.Sequence);

        Assert.Contains(withProfiles.Diagnostics, d => d.Code == SmfDiagnosticCodes.DeviceAutomationNotExported && d.Message.StartsWith("1 automation lanes control external instrument parameters", StringComparison.Ordinal));
        Assert.Contains(without.Diagnostics, d => d.Code == SmfDiagnosticCodes.DeviceAutomationNotExported && d.Message.StartsWith("2 automation lanes control external instrument parameters", StringComparison.Ordinal));
    }

    [Fact]
    public void AddingAndEditingAHardwareLane_IsUndoable()
    {
        var synth = Synth();
        var project = Build(synth, [Note(0)]);
        var track = project.Sequence.Tracks[0].Id;
        var history = new EditHistory(project);
        var lane = AutomationLane.Create(AutomationTarget.ForInstrument(synth.Id, "A", Three, "cutoff"));

        history.Execute(AutomationCommands.AddLane(track, lane));
        history.Execute(AutomationCommands.SetPoints(track, lane.Id, "Draw", [new AutomationPoint(Tick.Zero, ControlValue.Max)]));

        Assert.Equal(["0 0 B26301", "0 0 B26220", "0 0 B2067F", "0 0 B2657F", "0 0 B2647F", "0 0 923C64"], Sent(Compile(history.Current)));
        history.Undo();
        Assert.Empty(history.Current.Sequence.FindTrack(track)!.Automation.Single().Points);
        history.Undo();
        Assert.Empty(history.Current.Sequence.FindTrack(track)!.Automation);
        history.Redo();
        Assert.Equal(lane.Target, history.Current.Sequence.FindTrack(track)!.Automation.Single().Target);
    }

    [Fact]
    public void ProjectsWithoutInstrumentLanes_HaveNoParameterParts()
    {
        var project = Build(Synth(), [Note(0)]);

        var prepared = Prepare(project);

        Assert.Empty(prepared.ParameterParts);
        Assert.Empty(Compile(project).ParameterChases);
    }
}
