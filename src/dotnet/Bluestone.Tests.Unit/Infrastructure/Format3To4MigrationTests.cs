using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Application.Routing;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Infrastructure.Projects;
using Bluestone.Signal;
using static Bluestone.Tests.Unit.Playback.PlanDump;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>Format 3 routes become instruments, connections, and Transpose devices (ADR 0027).</summary>
public sealed class Format3To4MigrationTests
{
    private static Project RoutingFull() =>
        ProjectSerializer.Default.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", "format3-routing-full.bluestone"))).Project;

    private static Track TrackNamed(Project project, string name) => project.Sequence.Tracks.Single(t => t.Name == name);

    [Fact]
    public void Routes_BecomeOneInstrumentPerEndpointAndProfile()
    {
        var project = RoutingFull();

        Assert.Equal(
            ["QY Out", "QY Out (General MIDI)", "Module B", "Module C", "Gone", TrackOutputs.UnassignedName, "Module B (General MIDI)", "orphan-route"],
            project.Instruments.Select(i => i.Name));
        Assert.Equal(10, project.Connections.Length);
        Assert.All(project.Connections, c => Assert.Equal(SignalNodeKind.ExternalInstrument, c.Destination.Kind));
        Assert.All(project.Sequence.Tracks, t => Assert.Equal(TrackRole.Instrument, t.Role));
        Assert.Empty(project.Mixer.Channels);
    }

    [Fact]
    public void ARoute_ReadsBackAsTheSameOutput()
    {
        var project = RoutingFull();
        var bass = TrackNamed(project, "Bass");

        var output = TrackOutputs.Read(project, bass.Id);

        Assert.Equal("bluestone.generic.xg", output.Profile!.ProfileId);
        Assert.Equal(("coremidi", "-12345", "QY Out", "Maker", "Model"), (output.Endpoint!.ProviderId, output.Endpoint.EndpointKey, output.Endpoint.DisplayName, output.Endpoint.Manufacturer, output.Endpoint.Model));
        Assert.Equal(3, output.Channel!.Value.Number);
        Assert.Equal(-12, output.Transpose);
        Assert.Equal(new VoiceAssignment("normal", ProgramNumber.FromNumber(34)), output.Voice);
    }

    [Fact]
    public void Transposition_BecomesATransposeDeviceInTheTracksChain()
    {
        var project = RoutingFull();

        Assert.Equal(3, project.Chains.Length);
        Assert.Equal(
            [("Bass", -12), ("Lead", 7), ("Settings only", 5)],
            project.Chains.Select(c => (project.Sequence.FindTrack(c.Owner.Track)!.Name, BuiltInDevices.TransposeOf(Assert.Single(c.Devices)))));
    }

    [Fact]
    public void ARouteWithSettingsButNoEndpoint_KeepsItsSettings_AndStillDoesNotPlay()
    {
        var project = RoutingFull();
        var track = TrackNamed(project, "Settings only");

        var output = TrackOutputs.Read(project, track.Id);

        Assert.Null(output.Endpoint);
        Assert.Equal(("bluestone.generic.xg", 9, 5), (output.Profile!.ProfileId, output.Channel!.Value.Number, output.Transpose));
        var resolved = RouteResolver.ResolveTrack(project, track.Id, RouteResolver.ResolveInstruments(project, Format3EquivalenceTests.Profiles, Format3EquivalenceTests.Endpoints));
        Assert.False(resolved.CanPlay);
    }

    [Fact]
    public void ARouteForATrackThatIsGone_IsKept_AndReportedByValidation()
    {
        var project = RoutingFull();
        var orphan = project.Connections.Single(c => project.Sequence.FindTrack(c.Source.AsTrack()) is null);

        var issue = Assert.Single(SignalRoutingValidator.Validate(project, DeviceCatalog.BuiltIn.Lookup), i => i.Connections.Contains(orphan.Id));

        Assert.Equal(RoutingIssueCode.UnknownSource, issue.Code);
    }

    [Fact]
    public void AMigratedProject_SavedAndReopened_StillSendsTheSameBytes()
    {
        var original = RoutingFull();
        var saved = ProjectSerializer.Default.Serialize(new ProjectDocument(original));
        Assert.Equal(4, (int)JsonNode.Parse(saved)!["formatVersion"]!);
        Assert.Null(JsonNode.Parse(saved)!["project"]!["routing"]);

        var reopened = ProjectSerializer.Default.Deserialize(saved).Project;
        var plan = PlaybackRouting.Prepare(reopened, DeviceCatalog.BuiltIn, Format3EquivalenceTests.Profiles, Format3EquivalenceTests.Endpoints).Compile(reopened.Sequence);

        Assert.Equal("2F73C9A155441A5E", Hash(PlaybackStream(plan)));
        Assert.Equal(ProjectSamples.Describe(original), ProjectSamples.Describe(reopened));
    }

    [Fact]
    public void AFormat3ProjectWithoutRoutes_GetsEmptyRoutingAndAnEmptyMixer()
    {
        var json = $$"""
            { "format": "bluestone-project", "formatVersion": 3,
              "project": { "id": "{{Guid.NewGuid()}}", "name": "p",
                "sequence": { "ppqn": 480, "tempo": [], "meter": [], "markers": [],
                  "tracks": [ { "id": "{{Guid.NewGuid()}}", "name": "t", "muted": false, "soloed": false, "clips": [] } ] },
                "routing": [] } }
            """;

        var document = ProjectSerializer.Default.Deserialize(Encoding.UTF8.GetBytes(json));

        Assert.Empty(document.Project.Instruments);
        Assert.Empty(document.Project.Chains);
        Assert.Empty(document.Project.Connections);
        Assert.Equal(TrackRole.Instrument, Assert.Single(document.Project.Sequence.Tracks).Role);
        Assert.Empty(document.Notes);
    }
}
