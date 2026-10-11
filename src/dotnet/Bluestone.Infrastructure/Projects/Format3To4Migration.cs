using System.Text.Json;
using System.Text.Json.Nodes;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using static Bluestone.Infrastructure.Projects.NodeReader;

namespace Bluestone.Infrastructure.Projects;

/// <summary>
/// Format 4 replaces the per-track route with the routing model (ADR 0027): external instruments, device
/// chains, connections, and a mixer. Each format 3 route becomes what <see cref="TrackOutputs.Write"/>
/// makes of it, the same conversion the track inspector uses, so the project sends exactly the same MIDI:
/// <list type="bullet">
/// <item>one external instrument per distinct endpoint and profile, named after the endpoint, or
/// "Unassigned" for a route that has settings but no endpoint (such a route never played, and still does not);</item>
/// <item>a connection from the track to that instrument, forcing the route's channel and selecting its voice;</item>
/// <item>a Transpose device in the track's chain for a non-zero transposition.</item>
/// </list>
/// A route for a track that no longer exists is kept as a connection from that track, which routing
/// validation reports. Every track gets the instrument role; the mixer starts empty.
/// </summary>
internal sealed class Format3To4Migration : IProjectMigration
{
    private const string ProjectPath = "$.project";

    public int FromVersion => 3;

    public void Migrate(JsonObject root)
    {
        var project = Object(root, "project", "$");
        var tracks = Array(Object(project, "sequence", ProjectPath), "tracks", $"{ProjectPath}.sequence", int.MaxValue);
        foreach (var track in tracks)
        {
            Object(track, $"{ProjectPath}.sequence.tracks[]")["role"] = "instrument";
        }

        var model = Project.CreateNew();
        if (project["routing"] is not null)
        {
            var routes = Array(project, "routing", ProjectPath, int.MaxValue);
            for (var i = 0; i < routes.Count; i++)
            {
                var (track, output) = ReadRoute(Object(routes[i], $"{ProjectPath}.routing[{i}]"), $"{ProjectPath}.routing[{i}]");
                model = TrackOutputs.Write(model, track, output);
            }

            project.Remove("routing");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            ProjectSerializer.WriteRoutingModel(writer, model);
            writer.WriteEndObject();
        }

        var written = JsonNode.Parse(stream.ToArray())!.AsObject();
        foreach (var name in written.Select(p => p.Key).ToList())
        {
            var node = written[name];
            written.Remove(name);
            project[name] = node;
        }

        project["mixer"] = new JsonObject { ["masterGain"] = 0.0, ["channels"] = new JsonArray() };
    }

    private static (TrackId Track, TrackOutput Output) ReadRoute(JsonObject json, string path)
    {
        var transpose = OptionalInt(json, "transpose", path, -48, 48) ?? 0;
        return (new TrackId(Guid(json, "track", path)), new TrackOutput
        {
            Profile = ProjectSerializer.ReadProfile(json, path),
            Endpoint = ProjectSerializer.ReadEndpoint(json, path),
            Channel = OptionalInt(json, "channel", path, 1, 16) is { } channel ? MidiChannel.FromNumber(channel) : null,
            Transpose = transpose,
            Voice = ProjectSerializer.ReadVoice(json, path),
        });
    }
}
