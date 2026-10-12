using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>Instrument parameter automation targets in format 4 (ADR 0031): an additive target type, no new format version.</summary>
public sealed class InstrumentAutomationFormatTests
{
    private static readonly ProjectSerializer Serializer = ProjectSerializer.Default;

    private static Project Sample(out ExternalInstrument instrument, out AutomationLane lane)
    {
        instrument = ExternalInstrument.Create("MU2000", new EndpointReference("coremidi", "1", "MU2000"), new ProfileReference("bluestone.generic.xg", "Generic XG"));
        lane = new AutomationLane(
            AutomationLaneId.New(),
            AutomationTarget.ForInstrument(instrument.Id, "A", MidiChannel.FromNumber(3), "filter-cutoff"),
            [new AutomationPoint(Tick.Zero, ControlValue.Center, AutomationCurve.Linear), new AutomationPoint(new Tick(960), ControlValue.Max, AutomationCurve.Hold)]);
        var track = Track.Create("Lead").WithAutomation([lane]);
        var project = Project.CreateNew("Hardware");
        return project with { Instruments = [instrument], Sequence = project.Sequence.WithTrack(track) };
    }

    private static JsonObject ToJson(Project project) => JsonNode.Parse(Serializer.Serialize(new ProjectDocument(project)))!.AsObject();

    private static ProjectDocument FromJson(JsonObject json) => Serializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

    private static JsonObject TargetOf(JsonObject json) => json["project"]!["sequence"]!["tracks"]![0]!["automation"]![0]!["target"]!.AsObject();

    [Fact]
    public void InstrumentTarget_IsWrittenInTheCurrentFormat_AndReadBack()
    {
        var project = Sample(out var instrument, out var lane);

        var json = ToJson(project);
        var restored = FromJson(json).Project.Sequence.Tracks.Single().Automation.Single();

        Assert.Equal(ProjectSerializer.CurrentFormatVersion, (int)json["formatVersion"]!);
        Assert.Equal(
            $$"""{"type":"instrument","instrument":"{{instrument.Id.Value:D}}","port":"A","channel":3,"parameter":"filter-cutoff"}""",
            TargetOf(json).ToJsonString());
        Assert.Equal(lane.Id, restored.Id);
        Assert.Equal(lane.Target, restored.Target);
        Assert.Equal(lane.Points, restored.Points);
    }

    [Fact]
    public void InstrumentTarget_SavesIdenticallyAfterARoundTrip()
    {
        var bytes = Serializer.Serialize(new ProjectDocument(Sample(out _, out _)));

        Assert.Equal(bytes, Serializer.Serialize(Serializer.Deserialize(bytes)));
    }

    [Theory]
    [InlineData("port", "\"\"")]
    [InlineData("parameter", "\"\"")]
    [InlineData("channel", "0")]
    [InlineData("instrument", "\"not-a-guid\"")]
    [InlineData("instrument", "\"00000000-0000-0000-0000-000000000000\"")]
    public void InvalidInstrumentTargets_AreReportedWithTheirPath(string property, string replacement)
    {
        var json = ToJson(Sample(out _, out _));
        TargetOf(json)[property] = JsonNode.Parse(replacement);

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.StartsWith("$.project.sequence.tracks[0].automation[0].target", error.JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTargetProperties_AreReported()
    {
        var json = ToJson(Sample(out _, out _));
        TargetOf(json).Remove("parameter");

        Assert.StartsWith("$.project.sequence.tracks[0].automation[0].target", Assert.Throws<ProjectFormatException>(() => FromJson(json)).JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneForAMissingInstrument_IsKeptOnLoad()
    {
        var json = ToJson(Sample(out _, out var lane));
        json["project"]!["instruments"] = new JsonArray();

        var restored = FromJson(json).Project;

        Assert.Empty(restored.Instruments);
        Assert.Equal(lane.Target, restored.Sequence.Tracks.Single().Automation.Single().Target);
    }
}
