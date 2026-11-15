using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Infrastructure.Projects;
using Bluestone.Signal;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>Format 5 mixer topology becomes track-owned Group, Effect, return, and Master strips.</summary>
public sealed class Format5To6MigrationTests
{
    private const string FixtureName = "format5-professional-mixer.bluestone";
    private static readonly ProjectSerializer Serializer = ProjectSerializer.Default;
    private static readonly Guid ProjectGuid = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid SubmixChannelGuid = Guid.Parse("00000000-0000-7000-8000-000000000101");
    private static readonly Guid KeysChannelGuid = Guid.Parse("00000000-0000-7000-8000-000000000103");
    private static readonly Guid MissingOutputChannelGuid = Guid.Parse("00000000-0000-7000-8000-000000000107");
    private static readonly Guid LegacyTapChainGuid = Guid.Parse("00000000-0000-7000-8000-000000000301");
    private static readonly Guid LegacyTapDeviceGuid = Guid.Parse("00000000-0000-7000-8000-000000000302");
    private static readonly Guid LegacyTapConnectionGuid = Guid.Parse("00000000-0000-7000-8000-000000000303");
    private static readonly TrackId BassId = new(Guid.Parse("00000000-0000-7000-8000-000000000002"));
    private static readonly TrackId VocalId = new(Guid.Parse("00000000-0000-7000-8000-000000000003"));
    private static readonly TrackId DirectId = new(Guid.Parse("00000000-0000-7000-8000-000000000004"));
    private static readonly TrackId MidiOnlyId = new(Guid.Parse("00000000-0000-7000-8000-000000000005"));
    private static readonly TrackId DrumsId = new(Guid.Parse("00000000-0000-7000-8000-000000000006"));

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", FixtureName)))!.AsObject();

    private static Project Migrate(JsonObject? json = null) =>
        Serializer.Deserialize(Encoding.UTF8.GetBytes((json ?? Fixture()).ToJsonString())).Project;

    private static Track Named(Project project, string name) =>
        project.Sequence.Tracks.Single(track => track.Name == name);

    private static SignalConnection Route(Project project, TrackId source, AudioRoutePurpose purpose = AudioRoutePurpose.Output) =>
        project.Connections.Single(connection =>
            connection.Kind == SignalKind.Audio
            && connection.Source == SignalNode.Track(source)
            && connection.AudioPurpose == purpose);

    [Fact]
    public void FrozenFormat5Fixture_MigratesBusesReturnsSendsAndDirectOutputs()
    {
        var project = Migrate();

        var submix = Named(project, "Submix");
        var room = Named(project, "Room");
        var keys = Named(project, "Keys");
        var keysReturn = Named(project, "Keys Return");
        var vox = Named(project, "Vox Bus");
        var orderedNames = project.Sequence.Tracks.Select(track => track.Name).ToArray();
        Assert.True(Array.IndexOf(orderedNames, "Submix") < Array.IndexOf(orderedNames, "Bass"));
        Assert.True(Array.IndexOf(orderedNames, "Keys") < Array.IndexOf(orderedNames, "Vocal"));
        Assert.Equal(Array.IndexOf(orderedNames, "Keys") + 1, Array.IndexOf(orderedNames, "Keys Return"));
        Assert.Equal(TrackRole.Group, submix.Role);
        Assert.Equal(TrackRole.Effect, room.Role);
        Assert.Equal(TrackRole.Group, keys.Role);
        Assert.Equal(TrackRole.Effect, keysReturn.Role);
        Assert.Equal(TrackRole.Effect, vox.Role);

        Assert.Equal(new AudioStrip { GainDecibels = -3, Pan = 0.2, IsSoloed = true }, submix.AudioStrip);
        Assert.Equal(new AudioStrip { GainDecibels = -6 }, room.AudioStrip);
        Assert.Equal(new AudioStrip { GainDecibels = -10, Pan = -0.3, IsMuted = true }, keys.AudioStrip);
        Assert.Equal(AudioStrip.Default, keysReturn.AudioStrip);
        Assert.Equal(AudioStrip.Default, project.Sequence.FindTrack(BassId)!.AudioStrip);
        Assert.Equal(AudioStrip.Default, project.Sequence.FindTrack(VocalId)!.AudioStrip);
        Assert.Equal(AudioStrip.Default, project.Sequence.FindTrack(DirectId)!.AudioStrip);
        Assert.Null(project.Sequence.FindTrack(MidiOnlyId)!.AudioStrip);
        Assert.Equal(-1.25, project.MasterStrip.GainDecibels);
        Assert.Equal(0, project.MasterStrip.Pan);
        Assert.Empty(project.Mixer.Channels);

        Assert.Equal(submix.Id, project.Sequence.FindTrack(BassId)!.Group);
        Assert.Equal(keys.Id, project.Sequence.FindTrack(VocalId)!.Group);
        Assert.Equal(submix.Id, vox.Group);
        Assert.Equal(keys.Id, keysReturn.Group);
        Assert.Equal(SignalNode.Track(submix.Id), Route(project, BassId).Destination);
        Assert.Equal(SignalNode.Track(keys.Id), Route(project, VocalId).Destination);
        Assert.Equal(SignalNode.Master, Route(project, DirectId).Destination);
        Assert.Equal(SignalNode.Master, Route(project, submix.Id).Destination);
        Assert.Equal(SignalNode.Master, Route(project, room.Id).Destination);
        Assert.Equal(SignalNode.Track(submix.Id), Route(project, vox.Id).Destination);
        Assert.Equal(SignalNode.Track(keys.Id), Route(project, keysReturn.Id).Destination);

        var sends = project.Connections.Where(connection => connection.AudioPurpose == AudioRoutePurpose.Send).ToArray();
        Assert.Collection(
            sends.OrderBy(connection => connection.SendLevelDecibels),
            connection =>
            {
                Assert.Equal(SignalNode.Track(submix.Id), connection.Source);
                Assert.Equal(SignalNode.Track(room.Id), connection.Destination);
                Assert.Equal(-8.5, connection.SendLevelDecibels);
                Assert.True(connection.SendPreFader);
            },
            connection =>
            {
                Assert.Equal(SignalNode.Track(submix.Id), connection.Source);
                Assert.Equal(SignalNode.Track(keysReturn.Id), connection.Destination);
                Assert.Equal(-5, connection.SendLevelDecibels);
                Assert.False(connection.SendPreFader);
            });

        Assert.Contains(project.Connections, connection =>
            connection.Id.Value == Guid.Parse("00000000-0000-7000-8000-000000000201")
            && connection.Source == SignalNode.Track(BassId)
            && connection.Destination == SignalNode.Track(submix.Id));
        Assert.Contains(project.Connections, connection =>
            connection.Id.Value == Guid.Parse("00000000-0000-7000-8000-000000000202")
            && connection.Source == SignalNode.Track(VocalId)
            && connection.Destination == SignalNode.Track(keys.Id));
        Assert.Contains(project.Connections, connection =>
            connection.Id.Value == Guid.Parse("00000000-0000-7000-8000-000000000203")
            && connection.Source == SignalNode.Track(DirectId)
            && connection.Destination == SignalNode.Master);
        Assert.Contains(project.Connections, connection =>
            connection.Id.Value == Guid.Parse("00000000-0000-7000-8000-000000000204")
            && connection.Source == SignalNode.Track(vox.Id)
            && connection.Destination == SignalNode.Track(submix.Id));
    }

    [Fact]
    public void ChannelThatIsBothBusAndSendTarget_SplitsIntoGroupAndEffectReturn()
    {
        var project = Migrate();
        var submix = Named(project, "Submix");
        var group = Named(project, "Keys");
        var effect = Named(project, "Keys Return");

        Assert.Equal(TrackRole.Group, group.Role);
        Assert.Equal(TrackRole.Effect, effect.Role);
        Assert.Equal(group.Id, effect.Group);
        Assert.Equal(new AudioStrip { GainDecibels = -10, Pan = -0.3, IsMuted = true }, group.AudioStrip);
        Assert.Equal(AudioStrip.Default, effect.AudioStrip);

        var send = Assert.Single(project.Connections, connection =>
            connection.AudioPurpose == AudioRoutePurpose.Send
            && connection.Destination == SignalNode.Track(effect.Id));
        Assert.Equal(SignalNode.Track(submix.Id), send.Source);
        Assert.Equal(SignalNode.Track(group.Id), Route(project, effect.Id).Destination);
        Assert.Equal(SignalNode.Master, Route(project, group.Id).Destination);
        Assert.DoesNotContain(project.Connections, connection =>
            connection.AudioPurpose == AudioRoutePurpose.Send
            && connection.Destination == SignalNode.Track(group.Id));
    }

    [Fact]
    public void NestedLegacyBuses_RemainNestedGroups()
    {
        var project = Migrate();
        var parent = Named(project, "Submix");
        var child = Named(project, "Drum Bus");
        var drums = project.Sequence.FindTrack(DrumsId)!;

        Assert.Equal(TrackRole.Group, parent.Role);
        Assert.Equal(TrackRole.Group, child.Role);
        Assert.Equal(parent.Id, child.Group);
        Assert.Equal(child.Id, drums.Group);
        Assert.Equal(SignalNode.Track(child.Id), Route(project, DrumsId).Destination);
        Assert.Equal(SignalNode.Track(parent.Id), Route(project, child.Id).Destination);
        Assert.True(project.Sequence.Tracks.IndexOf(child) < project.Sequence.Tracks.IndexOf(drums));
    }

    [Fact]
    public void MissingLegacyOutputDestination_RemainsAnInvalidDiagnosableRoute()
    {
        var project = Migrate();
        var brokenOut = Named(project, "Broken Out");
        var route = Route(project, brokenOut.Id);

        Assert.Equal(SignalNode.Mixer(new MixerChannelId(MissingOutputChannelGuid)), route.Destination);
        Assert.NotEqual(SignalNode.Master, route.Destination);
        Assert.Contains(
            SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(project, DeviceCatalog.BuiltIn.Lookup)),
            candidate => candidate.Code == RoutingIssueCode.UnknownDestination
                && candidate.Connections.Contains(route.Id));
    }

    [Theory]
    [InlineData("device")]
    [InlineData("rack")]
    [InlineData("chainInput")]
    public void LegacyJunctionToMaster_MigratesThroughAnEffectStrip(string sourceType)
    {
        var json = Fixture();
        var projectJson = json["project"]!.AsObject();
        if (sourceType == "chainInput")
        {
            var direct = projectJson["sequence"]!["tracks"]!.AsArray()
                .OfType<JsonObject>()
                .Single(track => (string?)track["id"] == DirectId.Value.ToString("D"));
            direct["role"] = "audio";
        }

        var chain = new JsonObject
        {
            ["id"] = LegacyTapChainGuid.ToString("D"),
            ["owner"] = sourceType == "rack"
                ? new JsonObject { ["type"] = "rack" }
                : new JsonObject { ["type"] = "track", ["track"] = DirectId.Value.ToString("D") },
            ["devices"] = sourceType == "device"
                ? new JsonArray(new JsonObject
                {
                    ["id"] = LegacyTapDeviceGuid.ToString("D"),
                    ["definition"] = new JsonObject { ["id"] = "missing.legacy-audio", ["name"] = "Legacy Audio Device" },
                    ["parameters"] = new JsonArray(),
                })
                : new JsonArray(),
        };
        projectJson["chains"]!.AsArray().Add(chain);
        var source = sourceType switch
        {
            "rack" => new JsonObject { ["type"] = "rack", ["id"] = LegacyTapChainGuid.ToString("D") },
            "device" => new JsonObject { ["type"] = "device", ["id"] = LegacyTapDeviceGuid.ToString("D") },
            "chainInput" => new JsonObject { ["type"] = "chainInput", ["id"] = LegacyTapChainGuid.ToString("D") },
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown legacy source type."),
        };
        projectJson["connections"]!.AsArray().Add(new JsonObject
        {
            ["id"] = LegacyTapConnectionGuid.ToString("D"),
            ["kind"] = "audio",
            ["source"] = source,
            ["destination"] = new JsonObject { ["type"] = "master" },
        });

        var project = Migrate(json);
        var route = project.Connections.Single(connection => connection.Id.Value == LegacyTapConnectionGuid);
        var effect = Assert.Single(project.Sequence.Tracks, track => track.Name == "Legacy Master Return 1");

        Assert.Equal(TrackRole.Effect, effect.Role);
        Assert.Equal(AudioStrip.Default, effect.AudioStrip);
        Assert.Equal(sourceType switch
        {
            "rack" => SignalNode.Rack(new DeviceChainId(LegacyTapChainGuid)),
            "device" => SignalNode.Device(new DeviceId(LegacyTapDeviceGuid)),
            "chainInput" => SignalNode.ChainInput(new DeviceChainId(LegacyTapChainGuid)),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown legacy source type."),
        }, route.Source);
        Assert.Equal(SignalNode.Track(effect.Id), route.Destination);
        Assert.Equal(AudioRoutePurpose.Internal, route.AudioPurpose);
        Assert.Equal(SignalNode.Master, Route(project, effect.Id).Destination);
        Assert.DoesNotContain(SignalRoutingValidator.Errors(SignalRoutingValidator.Validate(project, DeviceCatalog.BuiltIn.Lookup)),
            issue => issue.Connections.Contains(route.Id));
    }

    [Fact]
    public void UnroutedMidiOnlyInstrument_DoesNotGainAnAudioStrip()
    {
        var project = Migrate();
        var midiOnly = project.Sequence.FindTrack(MidiOnlyId)!;

        Assert.Equal(TrackRole.Instrument, midiOnly.Role);
        Assert.Null(midiOnly.AudioStrip);
        Assert.DoesNotContain(project.Connections, connection =>
            connection.Kind == SignalKind.Audio
            && (connection.Source == SignalNode.Track(MidiOnlyId) || connection.Destination == SignalNode.Track(MidiOnlyId)));
    }

    [Fact]
    public void GeneratedTrackAndConnectionIds_AreDeterministicForTheFrozenFixture()
    {
        var first = Migrate();
        var second = Migrate();

        Assert.Equal(
            first.Sequence.Tracks.Select(track => (track.Name, track.Id, track.Role, track.Group)),
            second.Sequence.Tracks.Select(track => (track.Name, track.Id, track.Role, track.Group)));
        Assert.Equal(first.Connections, second.Connections);
    }

    [Fact]
    public void MigratedFormat6Project_RoundTripsAndWritesOnlyTheCurrentShape()
    {
        var project = Migrate();

        var saved = Serializer.Serialize(new ProjectDocument(project));
        var text = Encoding.UTF8.GetString(saved);
        var json = JsonNode.Parse(saved)!.AsObject();
        var reopened = Serializer.Deserialize(saved).Project;

        Assert.StartsWith("{\n  \"format\": \"bluestone-project\",\n  \"formatVersion\": 6,", text, StringComparison.Ordinal);
        Assert.Null(json["project"]!["mixer"]);
        Assert.NotNull(json["project"]!["masterStrip"]);
        Assert.Equal(ProjectSamples.Describe(project), ProjectSamples.Describe(reopened));
    }

    [Fact]
    public void Format6_UnknownAudioPurpose_IsRejectedAtItsPropertyPath()
    {
        var json = JsonNode.Parse(Serializer.Serialize(new ProjectDocument(Migrate())))!.AsObject();
        var connections = json["project"]!["connections"]!.AsArray();
        var targetId = Route(Migrate(), DirectId).Id.Value.ToString();
        var index = connections.Select((node, i) => (node, i))
            .Single(pair => (string?)pair.node!["id"] == targetId).i;
        connections[index]!["purpose"] = "auxiliary";

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal($"$.project.connections[{index}].purpose", error.JsonPath);
    }

    [Fact]
    public void Format6_SendWithoutLevel_IsRejectedAtItsPropertyPath()
    {
        var json = JsonNode.Parse(Serializer.Serialize(new ProjectDocument(Migrate())))!.AsObject();
        var connections = json["project"]!["connections"]!.AsArray();
        var project = Migrate();
        var submix = Named(project, "Submix");
        var room = Named(project, "Room");
        var target = Assert.Single(project.Connections, connection =>
            connection.AudioPurpose == AudioRoutePurpose.Send
            && connection.Source == SignalNode.Track(submix.Id)
            && connection.Destination == SignalNode.Track(room.Id));
        var index = connections.Select((node, i) => (node, i))
            .Single(pair => (string?)pair.node!["id"] == target.Id.Value.ToString()).i;
        connections[index]!.AsObject().Remove("sendLevel");

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal($"$.project.connections[{index}].sendLevel", error.JsonPath);
    }

    [Fact]
    public void MultipleOrdinaryOutputsForOneTrack_FailAtTheSecondDestination()
    {
        var json = Fixture();
        json["project"]!["connections"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "00000000-0000-7000-8000-000000000206",
            ["kind"] = "audio",
            ["source"] = new JsonObject { ["type"] = "track", ["id"] = BassId.Value.ToString("D") },
            ["destination"] = new JsonObject { ["type"] = "master" },
        });

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal("$.project.connections[5].destination", error.JsonPath);
    }

    [Fact]
    public void ExistingTrackGroupThatConflictsWithLegacyBusOutput_FailsAtThatDestination()
    {
        var json = Fixture();
        json["project"]!["sequence"]!["tracks"]![0]!["group"] = "00000000-0000-7000-8000-0000000000ff";

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal("$.project.connections[0].destination", error.JsonPath);
    }

    [Fact]
    public void LegacyBusCycle_FailsAtTheChannelOutputThatClosesTheCycle()
    {
        var json = Fixture();
        var channels = json["project"]!["mixer"]!["channels"]!.AsArray();
        channels[0]!["output"] = KeysChannelGuid.ToString("D");
        channels[2]!["output"] = SubmixChannelGuid.ToString("D");

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal("$.project.mixer.channels[0].output", error.JsonPath);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("duplicate")]
    public void UnrepresentableSends_FailAtTheOffendingTarget(string shape)
    {
        var json = Fixture();
        var sends = json["project"]!["mixer"]!["channels"]![0]!["sends"]!.AsArray();
        if (shape == "self")
        {
            sends.Add(new JsonObject
            {
                ["target"] = SubmixChannelGuid.ToString("D"),
                ["level"] = 0,
            });
        }
        else
        {
            sends.Add(new JsonObject
            {
                ["target"] = "00000000-0000-7000-8000-000000000102",
                ["level"] = 0,
            });
        }

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal("$.project.mixer.channels[0].sends[2].target", error.JsonPath);
    }

    [Fact]
    public void GeneratedTrackIdCollidingWithAnExistingTrack_FailsAtTheLegacyChannel()
    {
        var json = Fixture();
        var tracks = json["project"]!["sequence"]!["tracks"]!.AsArray();
        var collision = (JsonObject)tracks[0]!.DeepClone();
        collision["id"] = GeneratedId(ProjectGuid, SubmixChannelGuid, "group").ToString("D");
        tracks.Add(collision);

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal("$.project.mixer.channels[0].id", error.JsonPath);
    }

    [Fact]
    public void GeneratedConnectionIdCollidingWithAnExistingConnection_FailsAtTheLegacyOutput()
    {
        var json = Fixture();
        json["project"]!["connections"]![0]!["id"] = GeneratedId(ProjectGuid, SubmixChannelGuid, "output").ToString("D");

        var error = Assert.Throws<ProjectFormatException>(() => Migrate(json));

        Assert.Equal("$.project.mixer.channels[0].output", error.JsonPath);
    }

    private static Guid GeneratedId(Guid project, Guid owner, string role)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{project:D}/{owner:D}/{role}"));
        Span<byte> id = stackalloc byte[16];
        bytes.AsSpan(0, id.Length).CopyTo(id);
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id);
    }
}
