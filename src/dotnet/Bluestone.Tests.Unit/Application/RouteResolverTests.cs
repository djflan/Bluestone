using System.Text;
using Bluestone.Application.Routing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.Endpoints;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Application;

public sealed class RouteResolverTests
{
    private static readonly EndpointDescriptor SynthOut = Output("coremidi", "1001", "Synth");
    private static readonly EndpointDescriptor ModuleOut = Output("coremidi", "1002", "Module");

    internal static EndpointDescriptor Output(string provider, string key, string name) =>
        new(new EndpointId(provider, key), name, EndpointDirection.Output, EndpointTransport.Physical, EndpointCapabilities.SystemExclusive);

    internal static ProfileCatalog Catalog(params string[] ids) =>
        ids.Aggregate(ProfileCatalog.Empty, (catalog, id) => catalog.Add(id, ProfileLoader.Load(Encoding.UTF8.GetBytes($$"""
            { "format": "bluestone-device-profile", "schemaVersion": 1, "id": "{{id}}", "version": "1", "name": "Profile {{id}}",
              "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
              "banks": [ { "id": "main", "name": "Main", "msb": 0, "lsb": 3 } ] }
            """))));

    // A one-track project whose track is sent to the endpoint with the profile, as the inspector does it.
    private static ResolvedTrackOutput Resolve(EndpointReference? endpoint, ProfileReference? profile, ProfileCatalog catalog, IReadOnlyList<EndpointDescriptor> endpoints, VoiceAssignment? voice = null)
    {
        var track = Track.Create("t");
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) };
        project = TrackOutputs.Write(project, track.Id, new TrackOutput { Endpoint = endpoint, Profile = profile, Voice = voice });
        return RouteResolver.ResolveTrack(project, track.Id, RouteResolver.ResolveInstruments(project, catalog, endpoints));
    }

    [Fact]
    public void ExactId_Binds()
    {
        var resolved = Resolve(RouteResolver.ReferenceTo(SynthOut), null, ProfileCatalog.Empty, [SynthOut, ModuleOut]);

        Assert.Equal(EndpointBindingKind.Bound, resolved.Port!.Endpoint.Kind);
        Assert.Same(SynthOut, resolved.Port.Endpoint.Endpoint);
        Assert.True(resolved.CanPlay);
        Assert.Same(SynthOut, resolved.Live!.Endpoint);
        Assert.Empty(resolved.Problems);
    }

    [Fact]
    public void ChangedId_WithUniqueName_BindsByNameAndAsksForConfirmation()
    {
        var resolved = Resolve(new EndpointReference("coremidi", "old-id", "synth"), null, ProfileCatalog.Empty, [SynthOut, ModuleOut]);

        Assert.Equal(EndpointBindingKind.BoundByName, resolved.Port!.Endpoint.Kind);
        Assert.Same(SynthOut, resolved.Port.Endpoint.Endpoint);
        Assert.True(resolved.CanPlay);
        Assert.Contains("matched by name", Assert.Single(resolved.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedName_IsAmbiguous()
    {
        var twin = Output("coremidi", "1003", "Synth");

        var resolved = Resolve(new EndpointReference("coremidi", "gone", "Synth"), null, ProfileCatalog.Empty, [SynthOut, twin]);

        Assert.Equal(EndpointBindingKind.Ambiguous, resolved.Port!.Endpoint.Kind);
        Assert.Equal(2, resolved.Port.Endpoint.Candidates.Length);
        Assert.False(resolved.CanPlay);
        Assert.Null(resolved.Live);
    }

    [Fact]
    public void NameMatching_StaysWithinTheSameProvider()
    {
        var resolved = Resolve(new EndpointReference("winmm", "3", "Synth"), null, ProfileCatalog.Empty, [SynthOut]);

        Assert.Equal(EndpointBindingKind.Missing, resolved.Port!.Endpoint.Kind);
        Assert.Contains("\"Synth\" is not connected", Assert.Single(resolved.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void InputEndpoints_AreNeverBoundAsOutputs()
    {
        var input = SynthOut with { Direction = EndpointDirection.Input };

        Assert.Equal(EndpointBindingKind.Missing, Resolve(RouteResolver.ReferenceTo(SynthOut), null, ProfileCatalog.Empty, [input]).Port!.Endpoint.Kind);
    }

    [Fact]
    public void MissingProfile_IsExplainedButDoesNotPreventPlayback()
    {
        var resolved = Resolve(RouteResolver.ReferenceTo(SynthOut), new ProfileReference("absent.profile", "Old Synth"), ProfileCatalog.Empty, [SynthOut]);

        Assert.Equal(ProfileBindingKind.Missing, resolved.Instrument!.Profile.Kind);
        Assert.True(resolved.CanPlay);
        Assert.Contains("\"Old Synth\" is not installed", Assert.Single(resolved.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesAndEndpointsCombineFreely()
    {
        // The same profile can drive any endpoint, and an endpoint's name never selects a profile.
        var catalog = Catalog("vendor.module", "generic.gm");
        var profile = new ProfileReference("vendor.module");
        foreach (var endpoint in new[] { SynthOut, ModuleOut, Output("loopback", "bus/out", "Profile vendor.module") })
        {
            var resolved = Resolve(RouteResolver.ReferenceTo(endpoint), profile, catalog, [endpoint]);

            Assert.Equal("vendor.module", resolved.Instrument!.Profile.Profile!.Id);
            Assert.Same(endpoint, resolved.Port!.Endpoint.Endpoint);
        }

        var unprofiled = Resolve(RouteResolver.ReferenceTo(ModuleOut), null, catalog, [ModuleOut]);
        Assert.Equal(ProfileBindingKind.Unassigned, unprofiled.Instrument!.Profile.Kind);
    }

    [Fact]
    public void UnknownVoiceBank_IsReported()
    {
        var resolved = Resolve(RouteResolver.ReferenceTo(SynthOut), new ProfileReference("p.one"), Catalog("p.one"), [SynthOut], new VoiceAssignment("nope", new ProgramNumber(0)));

        Assert.Contains(resolved.Problems, p => p.Contains("Bank \"nope\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveTracks_CoversEveryTrackIncludingUnrouted()
    {
        var routed = Track.Create("routed");
        var unrouted = Track.Create("unrouted");
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(routed).WithTrack(unrouted) };
        project = TrackOutputs.Write(project, routed.Id, new TrackOutput { Endpoint = RouteResolver.ReferenceTo(SynthOut) });

        var resolved = RouteResolver.ResolveTracks(project, RouteResolver.ResolveInstruments(project, ProfileCatalog.Empty, [SynthOut]));

        Assert.Equal([routed.Id, unrouted.Id], resolved.Select(r => r.Track));
        Assert.True(resolved[0].CanPlay);
        Assert.Null(resolved[1].Port);
        Assert.Equal(RouteResolver.NoOutputProblem, Assert.Single(resolved[1].Problems));
    }

    [Fact]
    public void ASettingsOnlyOutput_HasAnUnassignedPort_AndCannotPlay()
    {
        var resolved = Resolve(null, new ProfileReference("p.one"), Catalog("p.one"), [SynthOut]);

        Assert.Equal(TrackOutputs.UnassignedName, resolved.Instrument!.Instrument.Name);
        Assert.Equal(EndpointBindingKind.Unassigned, resolved.Port!.Endpoint.Kind);
        Assert.False(resolved.CanPlay);
        Assert.Contains(RouteResolver.NoOutputProblem, resolved.Problems);
    }

    [Fact]
    public void TheLiveRoute_FollowsARackAndAddsUpTransposition()
    {
        var track = Track.Create("keys");
        var rack = DeviceChain.Create(ChainOwner.Rack, "FX") with { Devices = [BuiltInDevices.CreateTranspose(5)] };
        var instrument = ExternalInstrument.Create("Synth", RouteResolver.ReferenceTo(SynthOut));
        var project = Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track) };
        project = TrackOutputs.Write(project, track.Id, new TrackOutput { Transpose = 2 }).WithChain(rack) with
        {
            Instruments = [instrument],
            Connections =
            [
                SignalConnection.Create(SignalKind.Events, SignalNode.Track(track.Id), SignalNode.Rack(rack.Id)),
                SignalConnection.Create(SignalKind.Events, SignalNode.Rack(rack.Id), SignalNode.ExternalPart(instrument.Id)) with { Mapping = ChannelMapping.ForceTo(MidiChannel.FromNumber(3)) },
            ],
        };

        var resolved = RouteResolver.ResolveTrack(project, track.Id, RouteResolver.ResolveInstruments(project, ProfileCatalog.Empty, [SynthOut]));

        Assert.Null(resolved.Port);
        Assert.Empty(resolved.Problems);
        Assert.Same(SynthOut, resolved.Live!.Endpoint);
        Assert.Equal(7, resolved.Live.Transpose);
        Assert.Equal(3, resolved.Live.Channel!.Value.Number);
    }
}
