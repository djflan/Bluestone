using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Mixing;
using static Bluestone.Infrastructure.Projects.NodeReader;

namespace Bluestone.Infrastructure.Projects;

/// <summary>Moves legacy mixer-channel topology into track-owned Group, Effect, and Master strips.</summary>
internal sealed class Format5To6Migration : IProjectMigration
{
    private const string ProjectPath = "$.project";
    private const int MaxTracks = 4096;

    public int FromVersion => 5;

    public void Migrate(JsonObject root)
    {
        var project = Object(root, "project", "$");
        var projectId = Guid(project, "id", ProjectPath);
        var sequence = Object(project, "sequence", ProjectPath);
        var tracks = Array(sequence, "tracks", $"{ProjectPath}.sequence", MaxTracks);
        var connections = Array(project, "connections", ProjectPath, 65_536);
        var parkedConnections = project["parkedConnections"] is null
            ? []
            : Array(project, "parkedConnections", ProjectPath, 65_536);
        if (!project.ContainsKey("mixer") && project["masterStrip"] is JsonObject)
        {
            return;
        }

        var mixer = Object(project, "mixer", ProjectPath);
        var channels = Array(mixer, "channels", $"{ProjectPath}.mixer", 1024);
        var infos = ReadChannels(channels, connections, projectId);

        CheckLegacyCycles(infos);
        ValidateOrdinaryOutputs(connections, infos);

        var trackById = new Dictionary<Guid, JsonObject>();
        var trackIndexById = new Dictionary<Guid, int>();
        for (var i = 0; i < tracks.Count; i++)
        {
            var path = $"{ProjectPath}.sequence.tracks[{i}]";
            var track = Object(tracks[i], path);
            var id = Guid(track, "id", path);
            if (!trackById.TryAdd(id, track))
            {
                throw new ProjectFormatException($"{path}.id", $"track ID {id:D} appears more than once.");
            }

            trackIndexById.Add(id, i);
        }

        var masterReturns = FindLegacyMasterReturns(connections, parkedConnections, projectId);
        var referencedTrackIds = TrackReferences(connections, $"{ProjectPath}.connections")
            .Concat(TrackReferences(parkedConnections, $"{ProjectPath}.parkedConnections"))
            .ToHashSet();
        var generatedTrackIds = new HashSet<Guid>();
        foreach (var info in infos)
        {
            AddTrackId(info.PrimaryId, info.Path, trackById, generatedTrackIds, referencedTrackIds);
            if (info.ReturnId is { } returnId)
            {
                AddTrackId(returnId, info.Path, trackById, generatedTrackIds, referencedTrackIds);
            }
        }

        foreach (var masterReturn in masterReturns)
        {
            AddTrackId(masterReturn.TrackId, masterReturn.Path, trackById, generatedTrackIds, referencedTrackIds);
        }

        foreach (var info in infos)
        {
            info.PrimaryTrack = CreateTrack(info, info.PrimaryId, info.IsBus ? "group" : "effect", info.Strip);
            if (info.ReturnId is { } returnId)
            {
                info.ReturnTrack = CreateTrack(info, returnId, "effect", AudioStrip.Default);
            }
        }

        foreach (var masterReturn in masterReturns)
        {
            masterReturn.Track = CreateLegacyMasterReturn(masterReturn.TrackId, masterReturn.Name);
        }

        var masterReturnsByConnectionIndex = masterReturns
            .Where(masterReturn => !masterReturn.Parked)
            .ToDictionary(masterReturn => masterReturn.Index);

        var occupiedConnectionIds = new HashSet<Guid>();
        foreach (var connection in connections)
        {
            var item = Object(connection, $"{ProjectPath}.connections[]");
            occupiedConnectionIds.Add(Guid(item, "id", $"{ProjectPath}.connections[]"));
        }

        foreach (var connection in parkedConnections)
        {
            var item = Object(connection, $"{ProjectPath}.parkedConnections[]");
            var id = Guid(item, "id", $"{ProjectPath}.parkedConnections[]");
            if (!occupiedConnectionIds.Add(id))
            {
                throw new ProjectFormatException($"{ProjectPath}.parkedConnections", $"connection ID {id:D} is also used by an active or parked connection.");
            }
        }

        var existingChannelOutputs = FindExistingChannelOutputs(connections, infos);
        var migratedConnections = new JsonArray();
        foreach (var info in infos)
        {
            foreach (var output in AddOutputConnections(info, infos, occupiedConnectionIds, !existingChannelOutputs.Contains(info.Id)))
            {
                migratedConnections.Add(output);
            }
        }

        foreach (var masterReturn in masterReturns)
        {
            migratedConnections.Add(CreateConnection(
                GeneratedId(projectId, masterReturn.ConnectionId, "legacy-master-return-output"),
                Node("track", masterReturn.TrackId),
                Node("master"),
                "output",
                0,
                false,
                occupiedConnectionIds,
                masterReturn.Path));
        }

        foreach (var info in infos)
        {
            foreach (var send in info.Sends)
            {
                var target = infos.FirstOrDefault(candidate => candidate.Id == send.Target);
                if (target is null)
                {
                    // Keep the unresolved target as an invalid route for the normal routing validator.
                    migratedConnections.Add(CreateConnection(
                        GeneratedId(projectId, info.Id, $"send-{send.Index}"),
                        Node("track", info.PrimaryId),
                        Node("mixer", send.Target),
                        "send",
                        send.Level,
                        send.PreFader,
                        occupiedConnectionIds,
                        send.Path));
                    continue;
                }

                var returnTarget = target.ReturnId ?? (target.IsBus ? null : target.PrimaryId);
                if (returnTarget is null)
                {
                    throw new ProjectFormatException(send.Path, "the send target has no representable Effect return.");
                }

                if (returnTarget.Value == info.PrimaryId)
                {
                    throw new ProjectFormatException(send.Path, "a channel cannot send to itself.");
                }

                migratedConnections.Add(CreateConnection(
                    GeneratedId(projectId, info.Id, $"send-{send.Index}"),
                    Node("track", info.PrimaryId),
                    Node("track", returnTarget.Value),
                    "send",
                    send.Level,
                    send.PreFader,
                    occupiedConnectionIds,
                    send.Path));
            }
        }

        for (var i = 0; i < connections.Count; i++)
        {
            var path = $"{ProjectPath}.connections[{i}]";
            var item = Object(connections[i], path);
            var kind = String(item, "kind", path, 16);
            if (kind != "audio")
            {
                migratedConnections.Add(item.DeepClone());
                continue;
            }

            var source = Object(item, "source", path);
            var destination = Object(item, "destination", path);
            var sourceType = String(source, "type", $"{path}.source", 16);
            var destinationType = String(destination, "type", $"{path}.destination", 16);
            masterReturnsByConnectionIndex.TryGetValue(i, out var masterReturn);
            var sourceId = sourceType == "master" ? System.Guid.Empty : Guid(source, "id", $"{path}.source");
            var destinationId = destinationType == "master" ? System.Guid.Empty : Guid(destination, "id", $"{path}.destination");

            if (sourceType == "track" && trackById.TryGetValue(sourceId, out var sourceTrack))
            {
                EnsureStrip(sourceTrack);
            }

            if (destinationType == "track" && trackById.TryGetValue(destinationId, out var destinationTrack))
            {
                EnsureStrip(destinationTrack);
            }

            JsonObject migrated = (JsonObject)item.DeepClone();
            var purpose = "internal";
            if (masterReturn is not null)
            {
                migrated["destination"] = Node("track", masterReturn.TrackId);
            }
            else if (sourceType == "track" && destinationType == "master")
            {
                purpose = "output";
            }
            else if (destinationType == "mixer" && infos.FirstOrDefault(c => c.Id == destinationId) is { } destinationChannel)
            {
                if (sourceType == "track" && trackById.TryGetValue(sourceId, out var member))
                {
                    if (!destinationChannel.IsBus)
                    {
                        throw new ProjectFormatException($"{path}.destination", "an ordinary track output does not target a generated Group.");
                    }

                    SetParent(member, destinationChannel.GroupId, $"{path}.destination");
                    if (trackIndexById.TryGetValue(sourceId, out var memberIndex))
                    {
                        destinationChannel.MemberIndices.Add(memberIndex);
                    }

                    migrated["destination"] = Node("track", destinationChannel.GroupId);
                    purpose = "output";
                }
                else if (sourceType == "mixer" && infos.FirstOrDefault(c => c.Id == sourceId) is { } sourceChannel)
                {
                    if (!destinationChannel.IsBus)
                    {
                        throw new ProjectFormatException($"{path}.destination", "an ordinary channel output does not target a generated Group.");
                    }

                    SetParent(sourceChannel.PrimaryTrack!, destinationChannel.GroupId, $"{path}.destination");
                    migrated["source"] = Node("track", sourceChannel.PrimaryId);
                    migrated["destination"] = Node("track", destinationChannel.GroupId);
                    purpose = "output";
                }
                else
                {
                    migrated["destination"] = Node("track", destinationChannel.PrimaryId);
                }
            }
            else if (sourceType == "mixer" && infos.FirstOrDefault(c => c.Id == sourceId) is { } sourceStrip)
            {
                migrated["source"] = Node("track", sourceStrip.PrimaryId);
                purpose = destinationType == "master" ? "output" : "internal";
            }
            else if (destinationType == "mixer")
            {
                // An absent legacy channel remains an absent target instead of being silently rerouted.
                purpose = "output";
            }

            migrated["purpose"] = purpose;
            migratedConnections.Add(migrated);
        }

        var transformedParked = TransformParked(project, masterReturns.Where(route => route.Parked).ToList());
        foreach (var track in trackById.Values)
        {
            var role = OptionalRole(track);
            if (role is "audio" or "hybrid" or "effect" or "group")
            {
                EnsureStrip(track);
            }
        }

        var newTracks = InsertGeneratedTracks(tracks, infos, masterReturns);
        if (newTracks.Count > MaxTracks)
        {
            throw new ProjectFormatException($"{ProjectPath}.sequence.tracks", $"migration would exceed {MaxTracks} tracks.");
        }

        sequence["tracks"] = newTracks;
        project["connections"] = migratedConnections;
        if (transformedParked is not null)
        {
            project["parkedConnections"] = transformedParked;
        }

        project["masterStrip"] = new JsonObject
        {
            ["gain"] = Double(mixer, "masterGain", $"{ProjectPath}.mixer", AudioStrip.MinGainDecibels, AudioStrip.MaxGainDecibels),
            ["pan"] = 0.0,
            ["muted"] = false,
            ["soloed"] = false,
        };
        project.Remove("mixer");
    }

    private static List<ChannelInfo> ReadChannels(JsonArray channels, JsonArray connections, Guid projectId)
    {
        var result = new List<ChannelInfo>(channels.Count);
        var byId = new Dictionary<Guid, ChannelInfo>();
        for (var i = 0; i < channels.Count; i++)
        {
            var path = $"{ProjectPath}.mixer.channels[{i}]";
            var json = Object(channels[i], path);
            var id = Guid(json, "id", path);
            if (byId.ContainsKey(id))
            {
                throw new ProjectFormatException($"{path}.id", $"mixer channel ID {id:D} appears more than once.");
            }

            var sends = new List<LegacySend>();
            var sendTargets = new HashSet<Guid>();
            if (json["sends"] is not null)
            {
                var sendArray = Array(json, "sends", path, MixerChannel.MaxSends);
                for (var j = 0; j < sendArray.Count; j++)
                {
                    var sendPath = $"{path}.sends[{j}]";
                    var send = Object(sendArray[j], sendPath);
                    var target = Guid(send, "target", sendPath);
                    if (target == id)
                    {
                        throw new ProjectFormatException($"{sendPath}.target", "a channel cannot send to itself.");
                    }

                    if (!sendTargets.Add(target))
                    {
                        throw new ProjectFormatException($"{sendPath}.target", $"a send to channel {target:D} is duplicated.");
                    }

                    sends.Add(new LegacySend(
                        target,
                        Double(send, "level", sendPath, MixerChannel.MinGainDecibels, MixerChannel.MaxGainDecibels),
                        Bool(send, "preFader", sendPath, false),
                        j,
                        sendPath));
                }
            }

            Guid? output = json["output"] is null ? null : Guid(json, "output", path);
            var info = new ChannelInfo(
                i,
                id,
                path,
                String(json, "name", path, MixerChannel.MaxNameLength),
                output,
                new AudioStrip
                {
                    GainDecibels = Double(json, "gain", path, AudioStrip.MinGainDecibels, AudioStrip.MaxGainDecibels),
                    Pan = Double(json, "pan", path, -1, 1),
                    IsMuted = Bool(json, "muted", path, false),
                    IsSoloed = Bool(json, "soloed", path, false),
                },
                sends,
                projectId);
            byId.Add(id, info);
            result.Add(info);
        }

        foreach (var info in result)
        {
            if (info.Output is { } output && byId.TryGetValue(output, out var target))
            {
                target.HasOrdinaryInput = true;
            }

            foreach (var send in info.Sends)
            {
                if (byId.TryGetValue(send.Target, out var sendTarget))
                {
                    sendTarget.HasSendInput = true;
                }
            }
        }

        for (var i = 0; i < connections.Count; i++)
        {
            var path = $"{ProjectPath}.connections[{i}]";
            var connection = Object(connections[i], path);
            if (String(connection, "kind", path, 16) != "audio")
            {
                continue;
            }

            var source = Object(connection, "source", path);
            var destination = Object(connection, "destination", path);
            if (String(destination, "type", $"{path}.destination", 16) != "mixer")
            {
                continue;
            }

            var sourceType = String(source, "type", $"{path}.source", 16);
            if (sourceType is "track" or "mixer" && byId.TryGetValue(Guid(destination, "id", $"{path}.destination"), out var target))
            {
                target.HasOrdinaryInput = true;
            }
        }

        foreach (var info in result)
        {
            info.IsBus = info.HasOrdinaryInput;
            info.PrimaryId = GeneratedId(projectId, info.Id, info.IsBus ? "group" : "effect");
            info.GroupId = info.IsBus ? info.PrimaryId : System.Guid.Empty;
            if (info.IsBus && info.HasSendInput)
            {
                info.ReturnId = GeneratedId(projectId, info.Id, "return");
            }
        }

        return result;
    }

    private static void CheckLegacyCycles(List<ChannelInfo> channels)
    {
        var byId = channels.ToDictionary(channel => channel.Id);
        var visited = new HashSet<Guid>();
        var active = new HashSet<Guid>();
        foreach (var channel in channels)
        {
            Visit(channel);
        }

        void Visit(ChannelInfo channel)
        {
            if (visited.Contains(channel.Id))
            {
                return;
            }

            if (!active.Add(channel.Id))
            {
                throw new ProjectFormatException($"{channel.Path}.output", "legacy bus outputs contain a cycle that cannot become nested Groups.");
            }

            if (channel.Output is { } target && byId.TryGetValue(target, out var next))
            {
                Visit(next);
            }

            active.Remove(channel.Id);
            visited.Add(channel.Id);
        }
    }

    private static void ValidateOrdinaryOutputs(JsonArray connections, List<ChannelInfo> channels)
    {
        var existing = new Dictionary<Guid, (int Count, string Path)>();
        var channelIds = channels.Select(channel => channel.Id).ToHashSet();
        for (var i = 0; i < connections.Count; i++)
        {
            var path = $"{ProjectPath}.connections[{i}]";
            var connection = Object(connections[i], path);
            if (String(connection, "kind", path, 16) != "audio")
            {
                continue;
            }

            var source = Object(connection, "source", path);
            var destination = Object(connection, "destination", path);
            if (String(source, "type", $"{path}.source", 16) != "track")
            {
                continue;
            }

            var destinationType = String(destination, "type", $"{path}.destination", 16);
            var ordinary = destinationType == "master"
                || destinationType == "mixer" && channelIds.Contains(Guid(destination, "id", $"{path}.destination"));
            if (!ordinary)
            {
                continue;
            }

            var sourceId = Guid(source, "id", $"{path}.source");
            var prior = existing.GetValueOrDefault(sourceId);
            if (prior.Count > 0)
            {
                throw new ProjectFormatException($"{path}.destination", $"track {sourceId:D} has more than one ordinary audio output; its first is at {prior.Path}.destination.");
            }

            existing[sourceId] = (prior.Count + 1, path);
        }
    }

    private static void AddTrackId(Guid id, string path, Dictionary<Guid, JsonObject> existing, HashSet<Guid> generated, HashSet<Guid> referenced)
    {
        if (existing.ContainsKey(id) || referenced.Contains(id) || !generated.Add(id))
        {
            throw new ProjectFormatException($"{path}.id", $"generated track ID {id:D} collides with an existing track, a route reference, or another generated track.");
        }
    }

    private static IEnumerable<Guid> TrackReferences(JsonArray connections, string path)
    {
        for (var i = 0; i < connections.Count; i++)
        {
            var itemPath = $"{path}[{i}]";
            var connection = Object(connections[i], itemPath);
            foreach (var endpointName in new[] { "source", "destination" })
            {
                var endpointPath = $"{itemPath}.{endpointName}";
                var endpoint = Object(connection[endpointName], endpointPath);
                if (String(endpoint, "type", endpointPath, 16) == "track")
                {
                    yield return Guid(endpoint, "id", endpointPath);
                }
            }
        }
    }

    private static HashSet<Guid> FindExistingChannelOutputs(JsonArray connections, List<ChannelInfo> channels)
    {
        var byId = channels.ToDictionary(channel => channel.Id);
        var preserved = new HashSet<Guid>();
        for (var i = 0; i < connections.Count; i++)
        {
            var path = $"{ProjectPath}.connections[{i}]";
            var connection = Object(connections[i], path);
            if (String(connection, "kind", path, 16) != "audio")
            {
                continue;
            }

            var source = Object(connection, "source", path);
            if (String(source, "type", $"{path}.source", 16) != "mixer")
            {
                continue;
            }

            var sourceId = Guid(source, "id", $"{path}.source");
            if (!byId.TryGetValue(sourceId, out var channel))
            {
                continue;
            }

            var destination = Object(connection, "destination", path);
            var destinationType = String(destination, "type", $"{path}.destination", 16);
            var isOrdinary = destinationType is "master" or "mixer";
            if (!isOrdinary)
            {
                continue;
            }

            var connectionOutput = destinationType == "master" ? (Guid?)null : Guid(destination, "id", $"{path}.destination");
            if (channel.Output != connectionOutput)
            {
                throw new ProjectFormatException($"{path}.destination", $"the connection from mixer channel {channel.Id:D} conflicts with its declared output at {channel.Path}.output.");
            }

            if (!preserved.Add(channel.Id))
            {
                throw new ProjectFormatException($"{path}.destination", $"mixer channel {channel.Id:D} has more than one explicit ordinary output.");
            }
        }

        return preserved;
    }

    private static JsonObject CreateTrack(ChannelInfo info, Guid id, string role, AudioStrip strip) =>
        new()
        {
            ["id"] = id.ToString("D"),
            ["name"] = role == "effect" && info.ReturnId == id ? $"{info.Name} Return" : info.Name,
            ["muted"] = false,
            ["soloed"] = false,
            ["role"] = role,
            ["clips"] = new JsonArray(),
            ["audioStrip"] = StripJson(strip),
        };

    private static JsonObject StripJson(AudioStrip strip) =>
        new()
        {
            ["gain"] = strip.GainDecibels,
            ["pan"] = strip.Pan,
            ["muted"] = strip.IsMuted,
            ["soloed"] = strip.IsSoloed,
        };

    private static IEnumerable<JsonObject> AddOutputConnections(ChannelInfo info, List<ChannelInfo> channels, HashSet<Guid> connectionIds, bool includePrimaryOutput)
    {
        if (info.ReturnId is { } returnId)
        {
            SetParent(info.ReturnTrack!, info.GroupId, info.Path);
            yield return CreateConnection(
                GeneratedId(info.ProjectId, info.Id, "return-output"),
                Node("track", returnId),
                Node("track", info.GroupId),
                "output",
                0,
                false,
                connectionIds,
                info.Path);
        }

        if (!includePrimaryOutput)
        {
            yield break;
        }

        var destination = info.Output is { } oldTarget
            ? channels.FirstOrDefault(candidate => candidate.Id == oldTarget) is { } target
                ? target.IsBus
                    ? Node("track", target.GroupId)
                    : Node("mixer", oldTarget)
                : Node("mixer", oldTarget)
            : Node("master");
        if (destination["type"]?.GetValue<string>() == "track")
        {
            var parent = Guid(destination, "id", info.Path);
            if (info.IsBus)
            {
                // The Group track itself is nested in its parent; this is ordinary Group membership.
                SetParent(info.PrimaryTrack!, parent, $"{info.Path}.output");
            }
            else
            {
                SetParent(info.PrimaryTrack!, parent, $"{info.Path}.output");
            }
        }

        yield return CreateConnection(
            GeneratedId(info.ProjectId, info.Id, "output"),
            Node("track", info.PrimaryId),
            destination,
            "output",
            0,
            false,
            connectionIds,
            $"{info.Path}.output");
    }

    private static List<MasterReturnInfo> FindLegacyMasterReturns(JsonArray connections, JsonArray parkedConnections, Guid projectId)
    {
        var result = new List<MasterReturnInfo>();
        Add(connections, $"{ProjectPath}.connections", parked: false);
        Add(parkedConnections, $"{ProjectPath}.parkedConnections", parked: true);
        return result;

        void Add(JsonArray routes, string path, bool parked)
        {
            for (var i = 0; i < routes.Count; i++)
            {
                var routePath = $"{path}[{i}]";
                var route = Object(routes[i], routePath);
                if (String(route, "kind", routePath, 16) != "audio")
                {
                    continue;
                }

                var source = Object(route["source"], $"{routePath}.source");
                var destination = Object(route["destination"], $"{routePath}.destination");
                var sourceType = String(source, "type", $"{routePath}.source", 16);
                if (sourceType is not ("device" or "rack" or "chainInput")
                    || String(destination, "type", $"{routePath}.destination", 16) != "master")
                {
                    continue;
                }

                var connectionId = Guid(route, "id", routePath);
                result.Add(new MasterReturnInfo(
                    parked,
                    i,
                    routePath,
                    connectionId,
                    GeneratedId(projectId, connectionId, "legacy-master-return-track"),
                    $"Legacy Master Return {result.Count + 1}"));
            }
        }
    }

    private static JsonObject CreateLegacyMasterReturn(Guid id, string name) =>
        new()
        {
            ["id"] = id.ToString("D"),
            ["name"] = name,
            ["muted"] = false,
            ["soloed"] = false,
            ["role"] = "effect",
            ["clips"] = new JsonArray(),
            ["audioStrip"] = StripJson(AudioStrip.Default),
        };

    private static JsonArray? TransformParked(JsonObject project, List<MasterReturnInfo> masterReturns)
    {
        if (project["parkedConnections"] is null)
        {
            return null;
        }

        var masterReturnsByIndex = masterReturns.ToDictionary(masterReturn => masterReturn.Index);
        var parked = Array(project, "parkedConnections", ProjectPath, 65_536);
        var result = new JsonArray();
        for (var i = 0; i < parked.Count; i++)
        {
            var path = $"{ProjectPath}.parkedConnections[{i}]";
            var item = Object(parked[i], path);
            var copy = (JsonObject)item.DeepClone();
            if (String(item, "kind", path, 16) == "audio")
            {
                var source = Object(item, "source", path);
                var destination = Object(item, "destination", path);
                var sourceType = String(source, "type", $"{path}.source", 16);
                var destinationType = String(destination, "type", $"{path}.destination", 16);
                // Parked routes are inactive. Keep legacy mixer endpoints intact; if the route can no longer be
                // represented, the ungroup operation will leave it parked rather than guessing a new destination.
                if (masterReturnsByIndex.TryGetValue(i, out var masterReturn))
                {
                    copy["destination"] = Node("track", masterReturn.TrackId);
                    copy["purpose"] = "internal";
                }
                else
                {
                    copy["purpose"] = sourceType == "track" && destinationType is ("mixer" or "master") ? "output" : "internal";
                }
            }

            result.Add(copy);
        }

        return result;
    }

    private static JsonArray InsertGeneratedTracks(JsonArray original, List<ChannelInfo> channels, List<MasterReturnInfo> masterReturns)
    {
        var additions = new Dictionary<int, List<ChannelInfo>>();
        var tail = new List<ChannelInfo>();
        foreach (var channel in channels)
        {
            var firstMember = channel.MemberIndices.Count > 0 ? channel.MemberIndices.Min() : int.MaxValue;
            if (firstMember == int.MaxValue || firstMember >= original.Count)
            {
                tail.Add(channel);
            }
            else
            {
                if (!additions.TryGetValue(firstMember, out var at))
                {
                    additions[firstMember] = at = [];
                }

                at.Add(channel);
            }
        }

        var result = new JsonArray();
        for (var i = 0; i < original.Count; i++)
        {
            if (additions.TryGetValue(i, out var before))
            {
                AddGenerated(result, before);
            }

            result.Add(original[i]!.DeepClone());
        }

        AddGenerated(result, tail);
        foreach (var masterReturn in masterReturns)
        {
            result.Add(masterReturn.Track!.DeepClone());
        }

        return result;

        static void AddGenerated(JsonArray target, IEnumerable<ChannelInfo> infos)
        {
            foreach (var info in infos.OrderBy(info => info.Index))
            {
                target.Add((JsonObject)info.PrimaryTrack!.DeepClone());
                if (info.ReturnTrack is not null)
                {
                    target.Add((JsonObject)info.ReturnTrack.DeepClone());
                }
            }
        }
    }

    private static void EnsureStrip(JsonObject track)
    {
        track["audioStrip"] ??= StripJson(AudioStrip.Default);
    }

    private static string? OptionalRole(JsonObject track) =>
        track["role"] is JsonValue value && value.TryGetValue(out string? role) ? role : null;

    private static void SetParent(JsonObject track, Guid parent, string path)
    {
        if (track["group"] is JsonValue value && value.TryGetValue(out string? existing) && System.Guid.TryParseExact(existing, "D", out var current) && current != parent)
        {
            throw new ProjectFormatException(path, $"the legacy output requires Group {parent:D}, conflicting with existing Group {current:D}.");
        }

        track["group"] = parent.ToString("D");
    }

    private static JsonObject CreateConnection(
        Guid id,
        JsonObject source,
        JsonObject destination,
        string purpose,
        double level,
        bool preFader,
        HashSet<Guid> ids,
        string path)
    {
        if (!ids.Add(id))
        {
            throw new ProjectFormatException(path, $"generated connection ID {id:D} collides with another connection.");
        }

        var result = new JsonObject
        {
            ["id"] = id.ToString("D"),
            ["kind"] = "audio",
            ["source"] = source,
            ["destination"] = destination,
            ["purpose"] = purpose,
        };
        if (purpose == "send")
        {
            result["sendLevel"] = level;
            result["preFader"] = preFader;
        }

        return result;
    }

    private static JsonObject Node(string type, Guid? id = null)
    {
        var node = new JsonObject { ["type"] = type };
        if (id is { } value)
        {
            node["id"] = value.ToString("D");
        }

        return node;
    }

    private static Guid GeneratedId(Guid project, Guid channel, string role)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{project:D}/{channel:D}/{role}"));
        Span<byte> id = stackalloc byte[16];
        bytes.AsSpan(0, id.Length).CopyTo(id);
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id);
    }

    private sealed class ChannelInfo(
        int index,
        Guid id,
        string path,
        string name,
        Guid? output,
        AudioStrip strip,
        List<LegacySend> sends,
        Guid projectId)
    {
        public int Index { get; } = index;
        public Guid Id { get; } = id;
        public string Path { get; } = path;
        public string Name { get; } = name;
        public Guid? Output { get; } = output;
        public AudioStrip Strip { get; } = strip;
        public List<LegacySend> Sends { get; } = sends;
        public Guid ProjectId { get; } = projectId;
        public bool HasOrdinaryInput { get; set; }
        public bool HasSendInput { get; set; }
        public bool IsBus { get; set; }
        public Guid PrimaryId { get; set; }
        public Guid GroupId { get; set; }
        public Guid? ReturnId { get; set; }
        public JsonObject? PrimaryTrack { get; set; }
        public JsonObject? ReturnTrack { get; set; }
        public List<int> MemberIndices { get; } = [];
    }

    private sealed record LegacySend(Guid Target, double Level, bool PreFader, int Index, string Path);

    private sealed class MasterReturnInfo(bool parked, int index, string path, Guid connectionId, Guid trackId, string name)
    {
        public bool Parked { get; } = parked;
        public int Index { get; } = index;
        public string Path { get; } = path;
        public Guid ConnectionId { get; } = connectionId;
        public Guid TrackId { get; } = trackId;
        public string Name { get; } = name;
        public JsonObject? Track { get; set; }
    }
}
