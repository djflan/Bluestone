using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Infrastructure.Projects;
using CsCheck;

namespace Bluestone.Tests.Unit.Infrastructure;

public sealed class ProjectSerializerTests
{
    private static readonly ProjectSerializer Serializer = ProjectSerializer.Default;

    private static JsonObject ToJson(Project project) => JsonNode.Parse(Serializer.Serialize(new ProjectDocument(project)))!.AsObject();

    private static ProjectDocument FromJson(JsonObject json) => Serializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var project = ProjectSamples.Full();

        var restored = Serializer.Deserialize(Serializer.Serialize(new ProjectDocument(project))).Project;

        Assert.Equal(ProjectSamples.Describe(project), ProjectSamples.Describe(restored));
    }

    [Fact]
    public void Serialize_IsDeterministicAndHumanReadable()
    {
        var document = new ProjectDocument(ProjectSamples.Full());

        var first = Serializer.Serialize(document);
        var text = Encoding.UTF8.GetString(first);

        Assert.Equal(first, Serializer.Serialize(document));
        Assert.StartsWith("{\n  \"format\": \"bluestone-project\",\n  \"formatVersion\": 5,", text, StringComparison.Ordinal);
        Assert.Contains("\"bytes\": \"F0 43 10 4C 00 00 7E 00 F7\"", text, StringComparison.Ordinal);
        Assert.Contains("\"channel\": 2", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTopLevelDataAndExtensions_AreKept()
    {
        var json = ToJson(Project.CreateNew());
        json["extensions"] = JsonNode.Parse("""{ "org.example.ui": { "zoom": 2 } }""");
        json["futureThing"] = 42;

        var document = FromJson(json);
        var again = JsonNode.Parse(Serializer.Serialize(document))!.AsObject();

        Assert.Equal(2, (int)again["extensions"]!["org.example.ui"]!["zoom"]!);
        Assert.Equal(42, (int)again["futureThing"]!);
    }

    [Fact]
    public void NewerFormat_IsRefusedWithAClearMessage()
    {
        var json = ToJson(Project.CreateNew());
        json["formatVersion"] = 9;

        var error = Assert.Throws<ProjectVersionException>(() => FromJson(json));

        Assert.Equal(9, error.FileVersion);
        Assert.Contains("newer version of Bluestone", error.Message, StringComparison.Ordinal);
    }

    private sealed class RenameTitleMigration : IProjectMigration
    {
        public int FromVersion => ProjectSerializer.CurrentFormatVersion;

        public void Migrate(JsonObject root)
        {
            var project = root["project"]!.AsObject();
            project["name"] = (string)project["title"]!;
            project.Remove("title");
        }
    }

    [Fact]
    public void OlderFormats_AreMigratedBeforeReading()
    {
        var json = ToJson(Project.CreateNew("ignored"));
        var project = json["project"]!.AsObject();
        project.Remove("name");
        project["title"] = "From the current format";
        var next = ProjectSerializer.CurrentFormatVersion + 1;
        var serializerWithNext = new ProjectSerializer(next, [new RenameTitleMigration()]);

        var document = serializerWithNext.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Equal("From the current format", document.Project.Name);
        Assert.Contains($"\"formatVersion\": {next}", Encoding.UTF8.GetString(serializerWithNext.Serialize(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMigration_IsReported()
    {
        var json = ToJson(Project.CreateNew());
        json["formatVersion"] = 0;

        Assert.Contains("format 0 is not supported", Assert.Throws<ProjectFormatException>(() => FromJson(json)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$.project.sequence.ppqn", "0")]
    [InlineData("$.project.sequence.tracks[0].clips[0].events[2].velocity", "0")]
    [InlineData("$.project.sequence.tracks[0].clips[0].events[2].channel", "17")]
    [InlineData("$.project.sequence.tracks[0].clips[0].events[0].type", "\"lyric\"")]
    [InlineData("$.project.sequence.tracks[0].clips[0].events[1].program", "0")]
    [InlineData("$.project.sequence.tracks[0].clips[0].events[3].value", "4294967296")]
    [InlineData("$.project.sequence.tracks[0].clips[0].events[0].bytes", "\"F0 43 99 F7\"")]
    [InlineData("$.project.sequence.tracks[0].id", "\"not-a-guid\"")]
    [InlineData("$.project.sequence.tracks[0].clips[0].length", "0")]
    [InlineData("$.project.sequence.tracks[0].clips[0].start", "-1")]
    [InlineData("$.project.sequence.tracks[0].clips[0].offset", "-1")]
    [InlineData("$.project.sequence.tracks[0].clips[0].type", "\"audio\"")]
    [InlineData("$.project.sequence.tracks[0].clips[0].id", "\"not-a-guid\"")]
    [InlineData("$.project.sequence.tracks[2].clips[0].name", "7")]
    [InlineData("$.project.sequence.tracks[2].automation[0].target.controller", "0")]
    [InlineData("$.project.sequence.tracks[2].automation[0].target.type", "\"aftertouch\"")]
    [InlineData("$.project.sequence.tracks[2].automation[0].target.channel", "0")]
    [InlineData("$.project.sequence.tracks[2].automation[0].points[0].curve", "\"smooth\"")]
    [InlineData("$.project.sequence.tracks[2].automation[0].points[0].value", "-1")]
    [InlineData("$.project.sequence.tracks[2].automation[0].id", "1")]
    [InlineData("$.project.sequence.meter[0].denominator", "3")]
    [InlineData("$.project.sequence.tracks[0].role", "\"lead\"")]
    [InlineData("$.project.sequence.tracks[2].automation[3].target.parameter", "-1")]
    [InlineData("$.project.instruments[0].ports[0].id", "\"\"")]
    [InlineData("$.project.chains[0].owner.type", "\"bus\"")]
    [InlineData("$.project.chains[0].devices[0].parameters[0].id", "-1")]
    [InlineData("$.project.chains[2].devices[0].state.data", "\"not base64!\"")]
    [InlineData("$.project.connections[0].mapping.force", "17")]
    [InlineData("$.project.connections[1].mapping.remap[0].to", "0")]
    [InlineData("$.project.connections[0].source.type", "\"bus\"")]
    [InlineData("$.project.connections[0].kind", "\"light\"")]
    [InlineData("$.project.mixer.channels[0].gain", "99")]
    [InlineData("$.project.mixer.masterGain", "\"loud\"")]
    [InlineData("$.project.loop.end", "0")]
    public void InvalidValues_AreReportedWithTheirPath(string path, string replacement)
    {
        var json = ToJson(ProjectSamples.Full());
        Set(json, path, JsonNode.Parse(replacement));

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.StartsWith(path[..path.LastIndexOf('.')], error.JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePropertyNames_AreFormatErrors() =>
        Assert.Throws<ProjectFormatException>(() => Serializer.Deserialize("{ \"format\": \"bluestone-project\", \"format\": \"x\" }"u8));

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        var json = ToJson(ProjectSamples.Full());
        var events = json["project"]!["sequence"]!["tracks"]![0]!["clips"]![0]!["events"]!.AsArray();
        events[1]!["id"] = (string)events[0]!["id"]!;

        Assert.Contains("more than once", Assert.Throws<ProjectFormatException>(() => FromJson(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationLanes_AreCheckedForDuplicatesWithTheirPath()
    {
        var json = ToJson(ProjectSamples.Full());
        var lanes = json["project"]!["sequence"]!["tracks"]![2]!["automation"]!.AsArray();
        lanes[0]!["points"]![1]!["tick"] = 0L;

        Assert.Equal("$.project.sequence.tracks[2].automation[0].points", Assert.Throws<ProjectFormatException>(() => FromJson(json)).JsonPath);

        json = ToJson(ProjectSamples.Full());
        json["project"]!["sequence"]!["tracks"]![2]!["automation"]![1]!["target"] = JsonNode.Parse("""{ "type": "controller", "channel": 2, "controller": 7 }""");
        Assert.Equal("$.project.sequence.tracks[2].automation", Assert.Throws<ProjectFormatException>(() => FromJson(json)).JsonPath);
    }

    [Fact]
    public void DuplicateConnectionIds_AreFormatErrors()
    {
        var json = ToJson(ProjectSamples.Full());
        var connections = json["project"]!["connections"]!.AsArray();
        connections[1]!["id"] = (string)connections[0]!["id"]!;

        Assert.Contains("more than once", Assert.Throws<ProjectFormatException>(() => FromJson(json)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldingAndParkedConnections_AreWrittenOnlyWhenPresent_SoOtherFilesAreUnchanged()
    {
        var full = ToJson(ProjectSamples.Full());
        var plain = ProjectSamples.Full();
        plain = plain with
        {
            Sequence = plain.Sequence.WithTrack(plain.Sequence.Tracks[3].WithFolded(false)),
            ParkedConnections = [],
        };
        var json = ToJson(plain);

        Assert.True((bool)full["project"]!["sequence"]!["tracks"]![3]!["folded"]!);
        Assert.Single(full["project"]!["parkedConnections"]!.AsArray());
        Assert.Null(json["project"]!["sequence"]!["tracks"]![3]!["folded"]);
        Assert.Null(json["project"]!["parkedConnections"]);
    }

    [Fact]
    public void AParkedConnection_AlsoInConnections_IsAFormatError()
    {
        var json = ToJson(ProjectSamples.Full());
        json["project"]!["parkedConnections"]![0]!["id"] = (string)json["project"]!["connections"]![0]!["id"]!;

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.Equal("$.project.parkedConnections", error.JsonPath);
    }

    [Fact]
    public void Tracks_WithoutAutomation_DoNotWriteIt()
    {
        var json = ToJson(ProjectSamples.Full());

        Assert.Null(json["project"]!["sequence"]!["tracks"]![0]!["automation"]);
        Assert.Equal(4, json["project"]!["sequence"]!["tracks"]![2]!["automation"]!.AsArray().Count);
    }

    [Fact]
    public void OverlappingClips_AreRejectedAtTheTrack()
    {
        var json = ToJson(ProjectSamples.Full());
        json["project"]!["sequence"]!["tracks"]![2]!["clips"]![1]!["start"] = 2000L;

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.Equal("$.project.sequence.tracks[2].clips", error.JsonPath);
        Assert.Contains("overlap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format2_TrackEventsMoveIntoOneClipPerTrack()
    {
        const string track = "0199b0f2-0000-7000-8000-000000000001";
        const string empty = "0199b0f2-0000-7000-8000-000000000002";
        var json = $$"""
            { "format": "bluestone-project", "formatVersion": 2,
              "project": { "id": "0199b0f2-0000-7000-8000-0000000000ff", "name": "Old",
                "sequence": { "ppqn": 480, "tempo": [], "markers": [],
                  "meter": [ { "tick": 0, "numerator": 4, "denominator": 4 } ],
                  "tracks": [
                    { "id": "{{track}}", "name": "Pad", "muted": true, "soloed": false, "events": [
                      { "id": "0199b0f2-0000-7000-8000-000000000010", "tick": 2000, "type": "note", "length": 4000, "channel": 1, "note": 60, "velocity": 100, "release": 64 },
                      { "id": "0199b0f2-0000-7000-8000-000000000011", "tick": 2500, "type": "controller", "channel": 1, "controller": 1, "value": 0 } ] },
                    { "id": "{{empty}}", "name": "Empty", "events": [] } ] },
                "routing": [] } }
            """;

        var project = Serializer.Deserialize(Encoding.UTF8.GetBytes(json)).Project;

        var pad = project.Sequence.Tracks[0];
        var clip = Assert.IsType<NoteClip>(Assert.Single(pad.Clips));
        Assert.Equal((Guid.Parse(track), 1920L, 7680L, 0L), (clip.Id.Value, clip.Start.Value, clip.End.Value, clip.ContentOffset.Value));
        Assert.Equal([80L, 580L], clip.Content.Items.Select(e => e.Position.Value));
        Assert.Equal([2000L, 2500L], pad.ArrangedEvents.Select(e => e.Position.Value));
        Assert.Equal(4000, ((NoteEvent)pad.ArrangedEvents[0]).Duration.Value);
        Assert.True(pad.IsMuted);
        Assert.Empty(project.Sequence.Tracks[1].Clips);
    }

    [Fact]
    public void Format2_BadEventTicks_AreReportedWithTheirPath()
    {
        var json = """
            { "format": "bluestone-project", "formatVersion": 2,
              "project": { "id": "0199b0f2-0000-7000-8000-0000000000ff", "name": "Old",
                "sequence": { "ppqn": 480, "tempo": [], "markers": [], "meter": [],
                  "tracks": [ { "id": "0199b0f2-0000-7000-8000-000000000001", "name": "t", "events": [
                    { "id": "0199b0f2-0000-7000-8000-000000000010", "tick": "soon", "type": "meta", "metaType": 1, "bytes": "" } ] } ] },
                "routing": [] } }
            """;

        var error = Assert.Throws<ProjectFormatException>(() => Serializer.Deserialize(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("$.project.sequence.tracks[0].events[0].tick", error.JsonPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"format\": \"bluestone-device-profile\", \"formatVersion\": 1 }")]
    [InlineData("{ \"format\": \"bluestone-project\" }")]
    public void NonProjects_AreRejected(string text) =>
        Assert.Throws<ProjectFormatException>(() => Serializer.Deserialize(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void MutatedFiles_FailOnlyWithFormatErrors()
    {
        var bytes = Serializer.Serialize(new ProjectDocument(ProjectSamples.Full()));
        Gen.Select(Gen.Int[0, bytes.Length - 1], Gen.Byte).Array[1, 4].Sample(edits =>
        {
            var copy = (byte[])bytes.Clone();
            foreach (var (index, value) in edits)
            {
                copy[index] = value;
            }

            try
            {
                Serializer.Deserialize(copy);
            }
            catch (ProjectFormatException)
            {
            }

            return true;
        }, iter: 2000);
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        JsonNode node = root;
        var parts = path[2..].Replace("[", ".[", StringComparison.Ordinal).Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            node = parts[i].StartsWith('[') ? node[int.Parse(parts[i][1..^1], System.Globalization.CultureInfo.InvariantCulture)]! : node[parts[i]]!;
        }

        node.AsObject()[parts[^1]] = value;
    }
}
