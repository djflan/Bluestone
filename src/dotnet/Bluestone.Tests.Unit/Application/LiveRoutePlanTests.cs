using Bluestone.Application.Live;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Application;

/// <summary>Live input fans out through the whole chain and along every connection (ADR 0037), as playback does.</summary>
public sealed class LiveRoutePlanTests
{
    private static readonly DeviceDefinition SynthA = Instrument("plugin:test.synth-a", "Synth A");
    private static readonly DeviceDefinition SynthB = Instrument("plugin:test.synth-b", "Synth B");
    private static readonly DeviceDefinition Sink = Instrument("plugin:test.sink", "Sink") with { EventThru = EventClass.None };
    private static readonly DeviceCatalog Catalog = DeviceCatalog.BuiltIn.With(SynthA).With(SynthB).With(Sink);
    private static readonly EndpointDescriptor Hardware = new(new EndpointId("loopback", "hw"), "Hardware", EndpointDirection.Output, EndpointTransport.Virtual, default);

    private static DeviceDefinition Instrument(string id, string name) => new()
    {
        Id = new DeviceDefinitionId(id),
        Name = name,
        Origin = DeviceOrigin.Plugin,
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Audio,
        Handles = EventClass.Notes,
    };

    private static DeviceInstance Of(DeviceDefinition definition) => DeviceInstance.Create(definition.ToReference());

    private static (Project Project, Track Track) TrackWith(params DeviceInstance[] devices)
    {
        var track = Track.Create("Keys");
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Bluestone.Domain.Time.Ppqn.Default).WithTrack(track) };
        return (project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [.. devices] }), track);
    }

    [Fact]
    public void ALayeredChain_PlaysEveryInstrument_WithTheNoteEffectsBeforeEach()
    {
        var a = Of(SynthA);
        var arp = BuiltInDevices.CreateArpeggiator();
        var b = Of(SynthB);
        var (project, track) = TrackWith(a, arp, b);

        var plans = LiveRoutePlan.FindAll(project, track.Id, Catalog, (_, _) => null);

        Assert.Equal([(a.Id, 0), (b.Id, 1)], plans.Select(p => (p.Instrument!.Value, p.Stages.Length)));
        Assert.All(plans, p => Assert.Equal(LiveTargetKind.Software, p.Target));
        Assert.Equal(a.Id, LiveRoutePlan.Find(project, track.Id, Catalog, (_, _) => null).Instrument);
    }

    [Fact]
    public void AnInstrumentThatEndsTheNotePath_StopsTheRouteThere()
    {
        var (project, track) = TrackWith(Of(Sink), Of(SynthB));

        Assert.Single(LiveRoutePlan.FindAll(project, track.Id, Catalog, (_, _) => null));
    }

    [Fact]
    public void ATrackWithAnInstrumentAndAConnectionToHardware_PlaysBoth_AndAForcedChannelIsAStage()
    {
        var a = Of(SynthA);
        var (project, track) = TrackWith(a);
        var instrument = ExternalInstrument.Create("Hardware", new EndpointReference("loopback", "hw", "Hardware"));
        project = project with
        {
            Instruments = [instrument],
            Connections = [SignalConnection.Create(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.ExternalPart(instrument.Id)) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(3)) }],
        };

        var plans = LiveRoutePlan.FindAll(project, track.Id, Catalog, (_, _) => Hardware);

        Assert.Equal([LiveTargetKind.Software, LiveTargetKind.External], plans.Select(p => p.Target));
        Assert.Equal(MidiChannel.FromNumber(3), Assert.Single(plans[1].Stages).Channel);
    }

    [Fact]
    public void NothingPlayable_IsOneSilentPlan()
    {
        var (project, track) = TrackWith(BuiltInDevices.CreateTranspose(3));

        var plan = Assert.Single(LiveRoutePlan.FindAll(project, track.Id, Catalog, (_, _) => null));

        Assert.Equal(LiveTargetKind.None, plan.Target);
    }

    [Fact]
    public void TheSameInstrumentReachedTwice_IsPlayedOnce()
    {
        var a = Of(SynthA);
        var keys = Track.Create("Keys");
        var rack = DeviceChain.Create(ChainOwner.Rack, "Shared") with { Devices = [a] };
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Bluestone.Domain.Time.Ppqn.Default).WithTrack(keys) };
        var transpose = BuiltInDevices.CreateTranspose(12);
        project = project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(keys.Id)) with { Devices = [transpose] }).WithChain(rack) with
        {
            Connections =
            [
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(keys.Id), SignalNode.Rack(rack.Id)),
                SignalConnection.Create(SignalKind.Events, SignalNode.Device(transpose.Id), SignalNode.Rack(rack.Id)),
            ],
        };

        Assert.Single(LiveRoutePlan.FindAll(project, keys.Id, Catalog, (_, _) => null));
    }
}
