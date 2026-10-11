using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using static Bluestone.Infrastructure.Projects.NodeReader;

namespace Bluestone.Infrastructure.Projects;

/// <summary>
/// Reads and writes the Bluestone project format: versioned, indented JSON that diffs well in version
/// control. Older formats are upgraded through <see cref="IProjectMigration"/>s before reading; newer
/// formats are refused with <see cref="ProjectVersionException"/>. See docs/project-format.md.
/// </summary>
public sealed class ProjectSerializer
{
    public const string FormatName = "bluestone-project";
    public const int CurrentFormatVersion = 4;
    public const int MaxBytes = 256 * 1024 * 1024;

    private const int MaxTracks = 4096;
    private const int MaxEventsPerTrack = 5_000_000;
    private const int MaxClipsPerTrack = 100_000;
    private const int MaxNameLength = 1024;
    private const int MaxInstruments = 4096;
    private const int MaxChains = MaxTracks * 2;
    private const int MaxConnections = 65_536;
    private static readonly string[] KnownRootProperties = ["format", "formatVersion", "project"];

    private readonly int _currentVersion;
    private readonly Dictionary<int, IProjectMigration> _migrations;

    public ProjectSerializer()
        : this(CurrentFormatVersion, [new Format2To3Migration(), new Format3To4Migration()])
    {
    }

    internal ProjectSerializer(int currentVersion, IEnumerable<IProjectMigration> migrations)
    {
        _currentVersion = currentVersion;
        _migrations = migrations.ToDictionary(m => m.FromVersion);
    }

    public static ProjectSerializer Default { get; } = new();

    public byte[] Serialize(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var project = document.Project;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("formatVersion", _currentVersion);
            writer.WriteStartObject("project");
            writer.WriteString("id", project.Id.Value.ToString("D"));
            writer.WriteString("name", project.Name);
            if (project.Loop is { } loop)
            {
                writer.WriteStartObject("loop");
                writer.WriteNumber("start", loop.Start.Value);
                writer.WriteNumber("end", loop.End.Value);
                writer.WriteEndObject();
            }

            WriteSequence(writer, project.Sequence);
            WriteRoutingModel(writer, project);
            WriteMixer(writer, project.Mixer);
            writer.WriteEndObject();

            foreach (var (name, value) in document.Preserved)
            {
                writer.WritePropertyName(name);
                if (value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <exception cref="ProjectFormatException">The data is not a readable project.</exception>
    /// <exception cref="ProjectVersionException">The project was written by a newer version.</exception>
    public ProjectDocument Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw new ProjectFormatException("$", "the project file is too large.");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8Json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("$", $"the file is not valid JSON ({ex.Message}).", ex);
        }

        try
        {
            var root = Object(node, "$");
            if (root["format"] is not JsonValue format || !format.TryGetValue(out string? formatName) || formatName != FormatName)
            {
                throw new ProjectFormatException("$.format", $"must be \"{FormatName}\"; this is not a Bluestone project.");
            }

            var version = Int(root, "formatVersion", "$", 0, int.MaxValue);
            if (version > _currentVersion)
            {
                throw new ProjectVersionException(version, _currentVersion);
            }

            while (version < _currentVersion)
            {
                if (!_migrations.TryGetValue(version, out var migration))
                {
                    throw new ProjectFormatException("$.formatVersion", string.Create(CultureInfo.InvariantCulture, $"format {version} is not supported."));
                }

                migration.Migrate(root);
                version++;
                root["formatVersion"] = version;
            }

            var (project, notes) = TrackRoleConversion.Reconcile(ReadProject(Object(root, "project", "$")));
            var preserved = new JsonObject();
            foreach (var (name, value) in root.ToList())
            {
                if (!KnownRootProperties.Contains(name, StringComparer.Ordinal))
                {
                    root.Remove(name);
                    preserved[name] = value;
                }
            }

            return new ProjectDocument(project, preserved) { Notes = notes };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // JsonNode reports structural problems such as duplicate property names this way.
            throw new ProjectFormatException("$", $"the file is malformed ({ex.Message}).", ex);
        }
    }

    private static void WriteSequence(Utf8JsonWriter writer, Sequence sequence)
    {
        writer.WriteStartObject("sequence");
        writer.WriteNumber("ppqn", sequence.Ppqn.TicksPerQuarterNote);

        writer.WriteStartArray("tempo");
        foreach (var change in sequence.TempoMap.Changes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", change.Position.Value);
            writer.WriteNumber("microsecondsPerQuarter", change.Tempo.MicrosecondsPerQuarterNote);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("meter");
        foreach (var change in sequence.MeterMap.Changes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", change.Position.Value);
            writer.WriteNumber("numerator", change.Signature.Numerator);
            writer.WriteNumber("denominator", change.Signature.Denominator);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("markers");
        foreach (var marker in sequence.Markers)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", marker.Position.Value);
            writer.WriteString("name", marker.Name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("tracks");
        foreach (var track in sequence.Tracks)
        {
            writer.WriteStartObject();
            writer.WriteString("id", track.Id.Value.ToString("D"));
            writer.WriteString("name", track.Name);
            writer.WriteBoolean("muted", track.IsMuted);
            writer.WriteBoolean("soloed", track.IsSoloed);
            writer.WriteString("role", RoleName(track.Role));
            if (track.Group is { } group)
            {
                writer.WriteString("group", group.Value.ToString("D"));
            }

            writer.WriteStartArray("clips");
            foreach (var clip in track.Clips)
            {
                WriteClip(writer, clip);
            }

            writer.WriteEndArray();
            if (!track.Automation.IsEmpty)
            {
                writer.WriteStartArray("automation");
                foreach (var lane in track.Automation)
                {
                    WriteLane(writer, lane);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteClip(Utf8JsonWriter writer, Clip clip)
    {
        writer.WriteStartObject();
        writer.WriteString("id", clip.Id.Value.ToString("D"));
        writer.WriteString("type", clip switch
        {
            NoteClip => "note",
            AudioClip => "audio",
            _ => throw new NotSupportedException($"Clip type {clip.GetType().Name} cannot be saved."),
        });
        writer.WriteNumber("start", clip.Start.Value);
        writer.WriteNumber("length", clip.Length.Value);
        writer.WriteNumber("offset", clip.ContentOffset.Value);
        if (clip.Name.Length > 0)
        {
            writer.WriteString("name", clip.Name);
        }

        if (clip is NoteClip notes)
        {
            writer.WriteStartArray("events");
            foreach (var e in notes.Content.Items)
            {
                WriteEvent(writer, e);
            }

            writer.WriteEndArray();
        }
        else if (clip is AudioClip audio)
        {
            writer.WriteStartObject("source");
            writer.WriteString("location", audio.Source.Location);
            writer.WriteNumber("sampleRate", audio.Source.SampleRate);
            writer.WriteNumber("frames", audio.Source.Frames);
            writer.WriteNumber("channels", audio.Source.Channels);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteLane(Utf8JsonWriter writer, AutomationLane lane)
    {
        writer.WriteStartObject();
        writer.WriteString("id", lane.Id.Value.ToString("D"));
        writer.WriteStartObject("target");
        var target = lane.Target;
        writer.WriteString("type", target.Parameter switch
        {
            AutomationParameter.PitchBend => "pitchBend",
            AutomationParameter.ChannelPressure => "channelPressure",
            AutomationParameter.DeviceParameter => "device",
            AutomationParameter.InstrumentParameter => "instrument",
            _ => "controller",
        });
        if (target.IsMidi)
        {
            writer.WriteNumber("channel", target.Channel.Number);
        }
        else if (target.IsInstrument)
        {
            writer.WriteString("instrument", target.Instrument.Value.ToString("D"));
            writer.WriteString("port", target.Port);
            writer.WriteNumber("channel", target.Channel.Number);
            writer.WriteString("parameter", target.InstrumentParameter);
        }
        else
        {
            writer.WriteString("device", target.Device.Value.ToString("D"));
            writer.WriteNumber("parameter", target.DeviceParameter.Value);
        }

        if (target.Parameter == AutomationParameter.Controller)
        {
            writer.WriteNumber("controller", target.Controller.Value);
        }

        writer.WriteEndObject();
        writer.WriteStartArray("points");
        foreach (var point in lane.Points)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", point.Position.Value);
            writer.WriteNumber("value", point.Value.Value);
            writer.WriteString("curve", point.Curve == AutomationCurve.Hold ? "hold" : "linear");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEvent(Utf8JsonWriter writer, TrackEvent e)
    {
        writer.WriteStartObject();
        writer.WriteString("id", e.Id.Value.ToString("D"));
        writer.WriteNumber("tick", e.Position.Value);
        switch (e)
        {
            case NoteEvent note:
                writer.WriteString("type", "note");
                writer.WriteNumber("length", note.Duration.Value);
                writer.WriteNumber("channel", note.Channel.Number);
                writer.WriteNumber("note", note.Note.Value);
                writer.WriteNumber("velocity", note.Velocity.Value);
                writer.WriteNumber("release", note.ReleaseVelocity.Value);
                break;
            case NoteOffEvent off:
                writer.WriteString("type", "noteOff");
                writer.WriteNumber("channel", off.Channel.Number);
                writer.WriteNumber("note", off.Note.Value);
                writer.WriteNumber("release", off.ReleaseVelocity.Value);
                break;
            case ControllerEvent controller:
                writer.WriteString("type", "controller");
                writer.WriteNumber("channel", controller.Channel.Number);
                writer.WriteNumber("controller", controller.Controller.Value);
                writer.WriteNumber("value", controller.Value.Value);
                break;
            case ProgramEvent program:
                writer.WriteString("type", "program");
                writer.WriteNumber("channel", program.Channel.Number);
                writer.WriteNumber("program", program.Selection.Program.Number);
                if (program.Selection.BankMsb is { } msb)
                {
                    writer.WriteNumber("bankMsb", msb.Value);
                }

                if (program.Selection.BankLsb is { } lsb)
                {
                    writer.WriteNumber("bankLsb", lsb.Value);
                }

                break;
            case PitchBendEvent bend:
                writer.WriteString("type", "pitchBend");
                writer.WriteNumber("channel", bend.Channel.Number);
                writer.WriteNumber("value", bend.Value.Value);
                break;
            case ChannelPressureEvent pressure:
                writer.WriteString("type", "channelPressure");
                writer.WriteNumber("channel", pressure.Channel.Number);
                writer.WriteNumber("value", pressure.Pressure.Value);
                break;
            case PolyPressureEvent pressure:
                writer.WriteString("type", "polyPressure");
                writer.WriteNumber("channel", pressure.Channel.Number);
                writer.WriteNumber("note", pressure.Note.Value);
                writer.WriteNumber("value", pressure.Pressure.Value);
                break;
            case SysExEvent sysEx:
                writer.WriteString("type", "sysex");
                writer.WriteString("bytes", ToHex(sysEx.Message.Bytes.Span));
                break;
            case RawMidiEvent raw:
                writer.WriteString("type", "raw");
                writer.WriteString("bytes", ToHex(raw.Bytes.Span));
                break;
            case MetaEvent meta:
                writer.WriteString("type", "meta");
                writer.WriteNumber("metaType", meta.Type);
                writer.WriteString("bytes", ToHex(meta.Data.Span));
                break;
            default:
                throw new NotSupportedException($"Event type {e.GetType().Name} cannot be saved.");
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes the instruments, chains, and connections. Also used by <see cref="Format3To4Migration"/>.</summary>
    internal static void WriteRoutingModel(Utf8JsonWriter writer, Project project)
    {
        writer.WriteStartArray("instruments");
        foreach (var instrument in project.Instruments)
        {
            writer.WriteStartObject();
            writer.WriteString("id", instrument.Id.Value.ToString("D"));
            writer.WriteString("name", instrument.Name);
            WriteProfile(writer, instrument.Profile);
            WriteOptional(writer, "operatingMode", instrument.OperatingMode);
            writer.WriteStartArray("ports");
            foreach (var port in instrument.Ports)
            {
                writer.WriteStartObject();
                writer.WriteString("id", port.Id);
                writer.WriteString("name", port.Name);
                WriteEndpoint(writer, port.Endpoint);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("chains");
        foreach (var chain in project.Chains)
        {
            WriteChain(writer, chain);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("connections");
        foreach (var connection in project.Connections)
        {
            writer.WriteStartObject();
            writer.WriteString("id", connection.Id.Value.ToString("D"));
            writer.WriteString("kind", connection.Kind == SignalKind.Events ? "events" : "audio");
            WriteNode(writer, "source", connection.Source);
            WriteNode(writer, "destination", connection.Destination);
            if (!connection.Mapping.IsPreserving)
            {
                var mapping = connection.Mapping;
                writer.WriteStartObject("mapping");
                if (!mapping.Only.IsEmpty)
                {
                    writer.WriteStartArray("only");
                    foreach (var channel in mapping.Only)
                    {
                        writer.WriteNumberValue(channel.Number);
                    }

                    writer.WriteEndArray();
                }

                if (mapping.Force is { } force)
                {
                    writer.WriteNumber("force", force.Number);
                }

                if (!mapping.Remap.IsEmpty)
                {
                    writer.WriteStartArray("remap");
                    foreach (var remap in mapping.Remap)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("from", remap.From.Number);
                        writer.WriteNumber("to", remap.To.Number);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            if (connection.Voice is { } voice)
            {
                writer.WriteStartObject("voice");
                writer.WriteString("bank", voice.BankId);
                writer.WriteNumber("program", voice.Program.Number);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>Writes a chain: its owner, name, and devices with parameters, bypass, and plugin state.</summary>
    internal static void WriteChain(Utf8JsonWriter writer, DeviceChain chain)
    {
        writer.WriteStartObject();
        writer.WriteString("id", chain.Id.Value.ToString("D"));
        writer.WriteStartObject("owner");
        if (chain.Owner.Kind == ChainOwnerKind.Track)
        {
            writer.WriteString("type", "track");
            writer.WriteString("track", chain.Owner.Track.Value.ToString("D"));
        }
        else
        {
            writer.WriteString("type", "rack");
        }

        writer.WriteEndObject();
        if (chain.Name.Length > 0)
        {
            writer.WriteString("name", chain.Name);
        }

        writer.WriteStartArray("devices");
        foreach (var device in chain.Devices)
        {
            writer.WriteStartObject();
            writer.WriteString("id", device.Id.Value.ToString("D"));
            WriteDeviceBody(writer, device.Definition, device.Name, device.IsBypassed, device.Parameters, device.State);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The parts of a device that a preset shares with an instance: everything but its identity.</summary>
    internal static void WriteDeviceBody(Utf8JsonWriter writer, DeviceReference definition, string name, bool bypassed, IEnumerable<ParameterValue> parameters, PluginState? state)
    {
        writer.WriteStartObject("definition");
        writer.WriteString("id", definition.Id.Value);
        WriteOptional(writer, "name", definition.DisplayName);
        WriteOptional(writer, "version", definition.Version);
        writer.WriteEndObject();
        if (name.Length > 0)
        {
            writer.WriteString("name", name);
        }

        if (bypassed)
        {
            writer.WriteBoolean("bypassed", true);
        }

        writer.WriteStartArray("parameters");
        foreach (var parameter in parameters)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", parameter.Id.Value);
            writer.WriteNumber("value", parameter.Value.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (state is not null)
        {
            writer.WriteStartObject("state");
            writer.WriteString("format", state.Format);
            writer.WriteBase64String("data", state.Data.Span);
            writer.WriteEndObject();
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, string name, SignalNode node)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", node.Kind switch
        {
            SignalNodeKind.Track => "track",
            SignalNodeKind.Rack => "rack",
            SignalNodeKind.Device => "device",
            SignalNodeKind.ExternalInstrument => "instrument",
            SignalNodeKind.MixerChannel => "mixer",
            _ => "master",
        });
        if (node.Kind != SignalNodeKind.Master)
        {
            writer.WriteString("id", node.Id.ToString("D"));
        }

        WriteOptional(writer, "port", node.Port);
        writer.WriteEndObject();
    }

    private static void WriteMixer(Utf8JsonWriter writer, Mixer mixer)
    {
        writer.WriteStartObject("mixer");
        writer.WriteNumber("masterGain", mixer.MasterGainDecibels);
        writer.WriteStartArray("channels");
        foreach (var channel in mixer.Channels)
        {
            writer.WriteStartObject();
            writer.WriteString("id", channel.Id.Value.ToString("D"));
            writer.WriteString("name", channel.Name);
            writer.WriteNumber("gain", channel.GainDecibels);
            writer.WriteNumber("pan", channel.Pan);
            writer.WriteBoolean("muted", channel.IsMuted);
            writer.WriteBoolean("soloed", channel.IsSoloed);
            if (channel.Output is { } output)
            {
                writer.WriteString("output", output.Value.ToString("D"));
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProfile(Utf8JsonWriter writer, ProfileReference? profile)
    {
        if (profile is null)
        {
            return;
        }

        writer.WriteStartObject("profile");
        writer.WriteString("id", profile.ProfileId);
        WriteOptional(writer, "name", profile.DisplayName);
        writer.WriteEndObject();
    }

    private static void WriteEndpoint(Utf8JsonWriter writer, EndpointReference? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        writer.WriteStartObject("endpoint");
        writer.WriteString("provider", endpoint.ProviderId);
        writer.WriteString("key", endpoint.EndpointKey);
        WriteOptional(writer, "name", endpoint.DisplayName);
        WriteOptional(writer, "manufacturer", endpoint.Manufacturer);
        WriteOptional(writer, "model", endpoint.Model);
        writer.WriteEndObject();
    }

    private static string RoleName(TrackRole role) => role switch
    {
        TrackRole.Audio => "audio",
        TrackRole.Hybrid => "hybrid",
        TrackRole.Effect => "effect",
        TrackRole.Group => "group",
        _ => "instrument",
    };

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static Project ReadProject(JsonObject json)
    {
        const string path = "$.project";
        var sequence = ReadSequence(Object(json, "sequence", path));
        TickRange? loop = null;
        if (OptionalObject(json, "loop", path) is { } loopJson)
        {
            var start = Long(loopJson, "start", $"{path}.loop", 0, long.MaxValue);
            var end = Long(loopJson, "end", $"{path}.loop", 0, long.MaxValue);
            loop = end > start ? new TickRange(new Tick(start), new Tick(end)) : throw new ProjectFormatException($"{path}.loop", "must end after it starts.");
        }

        var instruments = Array(json, "instruments", path, MaxInstruments).Select((node, i) => ReadInstrument(Object(node, $"{path}.instruments[{i}]"), $"{path}.instruments[{i}]")).ToList();
        var chains = Array(json, "chains", path, MaxChains).Select((node, i) => ReadChain(Object(node, $"{path}.chains[{i}]"), $"{path}.chains[{i}]")).ToList();
        var connections = Array(json, "connections", path, MaxConnections).Select((node, i) => ReadConnection(Object(node, $"{path}.connections[{i}]"), $"{path}.connections[{i}]")).ToList();
        var mixer = ReadMixer(Object(json, "mixer", path), $"{path}.mixer");
        return Guard($"{path}", () => new Project
        {
            Id = new ProjectId(Guid(json, "id", path)),
            Name = String(json, "name", path, Project.MaxNameLength),
            Sequence = sequence,
            Instruments = [.. instruments],
            Chains = [.. chains],
            Connections = [.. connections],
            Mixer = mixer,
            Loop = loop,
        });
    }

    private static Sequence ReadSequence(JsonObject json)
    {
        const string path = "$.project.sequence";
        var ppqn = ReadPpqn(json, path);

        var tempo = Array(json, "tempo", path, 1_000_000).Select((node, i) =>
        {
            var at = $"{path}.tempo[{i}]";
            var item = Object(node, at);
            return new TempoChange(new Tick(Long(item, "tick", at, 0, long.MaxValue)), new Tempo(Int(item, "microsecondsPerQuarter", at, 1, Tempo.MaxMicrosecondsPerQuarterNote)));
        }).ToList();

        var markers = Array(json, "markers", path, 1_000_000).Select((node, i) =>
        {
            var at = $"{path}.markers[{i}]";
            var item = Object(node, at);
            return new Marker(new Tick(Long(item, "tick", at, 0, long.MaxValue)), String(item, "name", at, MaxNameLength));
        }).ToList();

        var tracks = Array(json, "tracks", path, MaxTracks).Select((node, i) => ReadTrack(Object(node, $"{path}.tracks[{i}]"), $"{path}.tracks[{i}]")).ToList();

        var tempoMap = new TempoMap(ppqn, tempo);
        var meterMap = ReadMeterMap(json, path, ppqn);
        return Guard($"{path}.tracks", () => new Sequence(tempoMap, meterMap, tracks, markers));
    }

    internal static Ppqn ReadPpqn(JsonObject sequence, string path) => new(Int(sequence, "ppqn", path, 1, Ppqn.MaxValue));

    internal static MeterMap ReadMeterMap(JsonObject sequence, string path, Ppqn ppqn)
    {
        var meter = Array(sequence, "meter", path, 1_000_000).Select((node, i) =>
        {
            var at = $"{path}.meter[{i}]";
            var item = Object(node, at);
            var numerator = Int(item, "numerator", at, 1, 255);
            var denominator = Int(item, "denominator", at, 1, TimeSignature.MaxDenominator);
            var signature = Guard(at, () => new TimeSignature(numerator, denominator));
            return new MeterChange(new Tick(Long(item, "tick", at, 0, long.MaxValue)), signature);
        }).ToList();
        return Guard($"{path}.meter", () => new MeterMap(ppqn, meter));
    }

    private static Track ReadTrack(JsonObject json, string path)
    {
        var eventCount = 0;
        var clips = Array(json, "clips", path, MaxClipsPerTrack).Select((node, i) => ReadClip(Object(node, $"{path}.clips[{i}]"), $"{path}.clips[{i}]", ref eventCount)).ToList();
        // Automation points share the track's budget with clip events.
        var lanes = json["automation"] is null
            ? []
            : Array(json, "automation", path, Track.MaxAutomationLanes).Select((node, i) => ReadLane(Object(node, $"{path}.automation[{i}]"), $"{path}.automation[{i}]", ref eventCount)).ToList();
        var id = new TrackId(Guid(json, "id", path));
        var name = String(json, "name", path, Track.MaxNameLength);
        var muted = Bool(json, "muted", path, false);
        var soloed = Bool(json, "soloed", path, false);
        var role = String(json, "role", path, 16) switch
        {
            "instrument" => TrackRole.Instrument,
            "audio" => TrackRole.Audio,
            "hybrid" => TrackRole.Hybrid,
            "effect" => TrackRole.Effect,
            "group" => TrackRole.Group,
            var other => throw new ProjectFormatException($"{path}.role", $"\"{other}\" is not a known track role."),
        };
        TrackId? group = json["group"] is null ? null : new TrackId(Guid(json, "group", path));
        var track = Guard($"{path}.clips", () => new Track(id, name, clips, muted, soloed, role: role, group: group));
        return Guard($"{path}.automation", () => track.WithAutomation(lanes));
    }

    private static Clip ReadClip(JsonObject json, string path, ref int eventCount)
    {
        var id = new ClipId(Guid(json, "id", path));
        var start = new Tick(Long(json, "start", path, 0, long.MaxValue));
        var length = new TickSpan(Long(json, "length", path, 1, long.MaxValue - start.Value));
        var offset = new TickSpan(Long(json, "offset", path, 0, long.MaxValue));
        var name = OptionalString(json, "name", path, Clip.MaxNameLength) ?? string.Empty;
        var type = String(json, "type", path, 16);
        switch (type)
        {
            case "note":
                var array = Array(json, "events", path, MaxEventsPerTrack - eventCount);
                eventCount += array.Count;
                var events = array.Select((node, i) => ReadEvent(Object(node, $"{path}.events[{i}]"), $"{path}.events[{i}]")).ToList();
                return Guard($"{path}.events", () => new NoteClip(id, start, length, offset, new EventList(events), name));
            case "audio":
                var at = $"{path}.source";
                var source = Object(json, "source", path);
                var audio = Guard(at, () => new AudioSource(
                    String(source, "location", at, AudioSource.MaxLocationLength),
                    Int(source, "sampleRate", at, 1000, 768_000),
                    Long(source, "frames", at, 0, long.MaxValue),
                    Int(source, "channels", at, 1, 64)));
                return Guard(path, () => new AudioClip(id, start, length, offset, audio, name));
            default:
                throw new ProjectFormatException($"{path}.type", $"\"{type}\" is not a known clip type.");
        }
    }

    private static AutomationLane ReadLane(JsonObject json, string path, ref int pointCount)
    {
        var id = new AutomationLaneId(Guid(json, "id", path));
        var at = $"{path}.target";
        var target = Object(json, "target", path);
        var type = String(target, "type", at, 16);
        var automationTarget = type switch
        {
            "controller" => Guard($"{at}.controller", () => AutomationTarget.ForController(Channel(target, at), new ControllerNumber(Int(target, "controller", at, 0, 127)))),
            "pitchBend" => AutomationTarget.ForPitchBend(Channel(target, at)),
            "channelPressure" => AutomationTarget.ForChannelPressure(Channel(target, at)),
            "device" => Guard(at, () => AutomationTarget.ForDevice(new DeviceId(Guid(target, "device", at)), new ParameterId((uint)Long(target, "parameter", at, 0, uint.MaxValue)))),
            "instrument" => Guard(at, () => AutomationTarget.ForInstrument(
                new ExternalInstrumentId(Guid(target, "instrument", at)),
                String(target, "port", at, AutomationTarget.MaxKeyLength),
                Channel(target, at),
                String(target, "parameter", at, AutomationTarget.MaxKeyLength))),
            _ => throw new ProjectFormatException($"{at}.type", $"\"{type}\" is not a known automation target."),
        };
        var array = Array(json, "points", path, Math.Min(AutomationLane.MaxPoints, MaxEventsPerTrack - pointCount));
        pointCount += array.Count;
        var points = array.Select((node, i) =>
        {
            var point = $"{path}.points[{i}]";
            var item = Object(node, point);
            var curve = String(item, "curve", point, 16) switch
            {
                "hold" => AutomationCurve.Hold,
                "linear" => AutomationCurve.Linear,
                var other => throw new ProjectFormatException($"{point}.curve", $"\"{other}\" is not a known curve."),
            };
            return new AutomationPoint(new Tick(Long(item, "tick", point, 0, long.MaxValue)), Value(item, point), curve);
        }).ToList();
        return Guard($"{path}.points", () => new AutomationLane(id, automationTarget, points));
    }

    private static TrackEvent ReadEvent(JsonObject json, string path)
    {
        var id = new EventId(Guid(json, "id", path));
        var tick = new Tick(Long(json, "tick", path, 0, long.MaxValue));
        var type = String(json, "type", path, 16);
        return type switch
        {
            "note" => Guard(path, () => new NoteEvent(
                id,
                tick,
                new TickSpan(Long(json, "length", path, 1, long.MaxValue)),
                MidiChannel.FromNumber(Int(json, "channel", path, 1, 16)),
                new NoteNumber(Int(json, "note", path, 0, 127)),
                new Velocity(Int(json, "velocity", path, 1, 127)),
                new Velocity(Int(json, "release", path, 0, 127)))),
            "noteOff" => new NoteOffEvent(id, tick, Channel(json, path), Note(json, path), new Velocity(Int(json, "release", path, 0, 127))),
            "controller" => new ControllerEvent(id, tick, Channel(json, path), new ControllerNumber(Int(json, "controller", path, 0, 127)), Value(json, path)),
            "program" => new ProgramEvent(id, tick, Channel(json, path), new ProgramSelection(
                ProgramNumber.FromNumber(Int(json, "program", path, 1, 128)),
                OptionalInt(json, "bankMsb", path, 0, 127) is { } msb ? new SevenBitValue(msb) : null,
                OptionalInt(json, "bankLsb", path, 0, 127) is { } lsb ? new SevenBitValue(lsb) : null)),
            "pitchBend" => new PitchBendEvent(id, tick, Channel(json, path), Value(json, path)),
            "channelPressure" => new ChannelPressureEvent(id, tick, Channel(json, path), Value(json, path)),
            "polyPressure" => new PolyPressureEvent(id, tick, Channel(json, path), Note(json, path), Value(json, path)),
            "sysex" => new SysExEvent(id, tick, SysExMessage.TryCreate(Hex(json, "bytes", path, SysExMessage.MaxLength), out var message, out var error)
                ? message
                : throw new ProjectFormatException($"{path}.bytes", error)),
            "raw" => new RawMidiEvent(id, tick, ByteBlock.Copy(Hex(json, "bytes", path, RawMidiEvent.MaxLength))),
            "meta" => new MetaEvent(id, tick, (byte)Int(json, "metaType", path, 0, 127), ByteBlock.Copy(Hex(json, "bytes", path, MetaEvent.MaxLength))),
            _ => throw new ProjectFormatException($"{path}.type", $"\"{type}\" is not a known event type."),
        };
    }

    private static MidiChannel Channel(JsonObject json, string path) => MidiChannel.FromNumber(Int(json, "channel", path, 1, 16));

    private static NoteNumber Note(JsonObject json, string path) => new(Int(json, "note", path, 0, 127));

    private static ControlValue Value(JsonObject json, string path) => new((uint)Long(json, "value", path, 0, uint.MaxValue));

    private static ExternalInstrument ReadInstrument(JsonObject json, string path)
    {
        var ports = Array(json, "ports", path, ExternalInstrument.MaxPorts).Select((node, i) =>
        {
            var at = $"{path}.ports[{i}]";
            var port = Object(node, at);
            return Guard(at, () => new ExternalPort(String(port, "id", at, ExternalPort.MaxLength), String(port, "name", at, MaxNameLength), ReadEndpoint(port, at)));
        }).ToList();
        return Guard(path, () => new ExternalInstrument
        {
            Id = new ExternalInstrumentId(Guid(json, "id", path)),
            Name = String(json, "name", path, ExternalInstrument.MaxNameLength),
            Profile = ReadProfile(json, path),
            OperatingMode = OptionalString(json, "operatingMode", path, 64),
            Ports = [.. ports],
        });
    }

    internal static ProfileReference? ReadProfile(JsonObject json, string path)
    {
        if (OptionalObject(json, "profile", path) is not { } p)
        {
            return null;
        }

        var at = $"{path}.profile";
        return Guard(at, () => new ProfileReference(String(p, "id", at, 100), OptionalString(p, "name", at, MaxNameLength)));
    }

    internal static EndpointReference? ReadEndpoint(JsonObject json, string path)
    {
        if (OptionalObject(json, "endpoint", path) is not { } e)
        {
            return null;
        }

        var at = $"{path}.endpoint";
        return Guard(at, () => new EndpointReference(
            String(e, "provider", at, 256),
            String(e, "key", at, 256),
            OptionalString(e, "name", at, MaxNameLength),
            OptionalString(e, "manufacturer", at, MaxNameLength),
            OptionalString(e, "model", at, MaxNameLength)));
    }

    internal static VoiceAssignment? ReadVoice(JsonObject json, string path)
    {
        if (OptionalObject(json, "voice", path) is not { } v)
        {
            return null;
        }

        return new VoiceAssignment(String(v, "bank", $"{path}.voice", 64), ProgramNumber.FromNumber(Int(v, "program", $"{path}.voice", 1, 128)));
    }

    internal static DeviceChain ReadChain(JsonObject json, string path)
    {
        var at = $"{path}.owner";
        var ownerJson = Object(json, "owner", path);
        var owner = String(ownerJson, "type", at, 16) switch
        {
            "track" => ChainOwner.ForTrack(new TrackId(Guid(ownerJson, "track", at))),
            "rack" => ChainOwner.Rack,
            var other => throw new ProjectFormatException($"{at}.type", $"\"{other}\" is not a known chain owner."),
        };
        var devices = Array(json, "devices", path, DeviceChain.MaxDevices).Select((node, i) =>
        {
            var device = $"{path}.devices[{i}]";
            var item = Object(node, device);
            var body = ReadDeviceBody(item, device);
            return Guard(device, () => new DeviceInstance
            {
                Id = new DeviceId(Guid(item, "id", device)),
                Definition = body.Definition,
                Name = body.Name,
                IsBypassed = body.Bypassed,
                Parameters = body.Parameters,
                State = body.State,
            });
        }).ToList();
        return Guard(path, () => new DeviceChain
        {
            Id = new DeviceChainId(Guid(json, "id", path)),
            Owner = owner,
            Name = OptionalString(json, "name", path, DeviceChain.MaxNameLength) ?? string.Empty,
            Devices = [.. devices],
        });
    }

    internal static (DeviceReference Definition, string Name, bool Bypassed, ImmutableArray<ParameterValue> Parameters, PluginState? State) ReadDeviceBody(JsonObject json, string path)
    {
        var at = $"{path}.definition";
        var definitionJson = Object(json, "definition", path);
        var definition = Guard(at, () => new DeviceReference(
            new DeviceDefinitionId(String(definitionJson, "id", at, DeviceDefinitionId.MaxLength)),
            OptionalString(definitionJson, "name", at, MaxNameLength),
            OptionalString(definitionJson, "version", at, 64)));
        var parameters = Array(json, "parameters", path, 65_536).Select((node, i) =>
        {
            var parameter = $"{path}.parameters[{i}]";
            var item = Object(node, parameter);
            return new ParameterValue(new ParameterId((uint)Long(item, "id", parameter, 0, uint.MaxValue)), Value(item, parameter));
        }).ToImmutableArray();
        PluginState? state = null;
        if (OptionalObject(json, "state", path) is { } stateJson)
        {
            var stateAt = $"{path}.state";
            var data = String(stateJson, "data", stateAt, ((PluginState.MaxLength + 2) / 3) * 4);
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException ex)
            {
                throw new ProjectFormatException($"{stateAt}.data", "is not valid base64.", ex);
            }

            state = Guard(stateAt, () => new PluginState(ByteBlock.Copy(bytes), String(stateJson, "format", stateAt, PluginState.MaxFormatLength)));
        }

        return (
            definition,
            OptionalString(json, "name", path, DeviceInstance.MaxNameLength) ?? string.Empty,
            Bool(json, "bypassed", path, false),
            parameters,
            state);
    }

    private static SignalConnection ReadConnection(JsonObject json, string path)
    {
        var kind = String(json, "kind", path, 16) switch
        {
            "events" => SignalKind.Events,
            "audio" => SignalKind.Audio,
            var other => throw new ProjectFormatException($"{path}.kind", $"\"{other}\" is not a known signal kind."),
        };
        var mapping = ChannelMapping.Preserve;
        if (OptionalObject(json, "mapping", path) is { } m)
        {
            var at = $"{path}.mapping";
            var only = m["only"] is null ? [] : Array(m, "only", at, MidiChannel.Count).Select((node, i) => MidiChannel.FromNumber(IntValue(node, $"{at}.only[{i}]", 1, 16))).ToImmutableArray();
            var remap = m["remap"] is null ? [] : Array(m, "remap", at, MidiChannel.Count).Select((node, i) =>
            {
                var item = Object(node, $"{at}.remap[{i}]");
                return new ChannelRemap(MidiChannel.FromNumber(Int(item, "from", $"{at}.remap[{i}]", 1, 16)), MidiChannel.FromNumber(Int(item, "to", $"{at}.remap[{i}]", 1, 16)));
            }).ToImmutableArray();
            var force = OptionalInt(m, "force", at, 1, 16) is { } forced ? MidiChannel.FromNumber(forced) : (MidiChannel?)null;
            mapping = Guard(at, () => new ChannelMapping { Only = only, Force = force, Remap = remap });
        }

        return new SignalConnection
        {
            Id = new ConnectionId(Guid(json, "id", path)),
            Kind = kind,
            Source = ReadNode(Object(json, "source", path), $"{path}.source"),
            Destination = ReadNode(Object(json, "destination", path), $"{path}.destination"),
            Mapping = mapping,
            Voice = ReadVoice(json, path),
        };
    }

    private static SignalNode ReadNode(JsonObject json, string path)
    {
        var type = String(json, "type", path, 16);
        if (type == "master")
        {
            return SignalNode.Master;
        }

        var id = Guid(json, "id", path);
        return type switch
        {
            "track" => SignalNode.Track(new TrackId(id)),
            "rack" => SignalNode.Rack(new DeviceChainId(id)),
            "device" => SignalNode.Device(new DeviceId(id)),
            "instrument" => SignalNode.ExternalPart(new ExternalInstrumentId(id), OptionalString(json, "port", path, ExternalPort.MaxLength)),
            "mixer" => SignalNode.Mixer(new MixerChannelId(id)),
            _ => throw new ProjectFormatException($"{path}.type", $"\"{type}\" is not a known node type."),
        };
    }

    private static Mixer ReadMixer(JsonObject json, string path)
    {
        var channels = Array(json, "channels", path, Mixer.MaxChannels).Select((node, i) =>
        {
            var at = $"{path}.channels[{i}]";
            var item = Object(node, at);
            return Guard(at, () => new MixerChannel
            {
                Id = new MixerChannelId(Guid(item, "id", at)),
                Name = String(item, "name", at, MixerChannel.MaxNameLength),
                GainDecibels = Double(item, "gain", at, MixerChannel.MinGainDecibels, MixerChannel.MaxGainDecibels),
                Pan = Double(item, "pan", at, -1, 1),
                IsMuted = Bool(item, "muted", at, false),
                IsSoloed = Bool(item, "soloed", at, false),
                Output = item["output"] is null ? null : new MixerChannelId(Guid(item, "output", at)),
            });
        }).ToList();
        var master = Double(json, "masterGain", path, MixerChannel.MinGainDecibels, MixerChannel.MaxGainDecibels);
        return Guard(path, () => new Mixer { Channels = [.. channels], MasterGainDecibels = master });
    }

    /// <summary>Converts domain validation failures into format errors at <paramref name="path"/>.</summary>
    private static T Guard<T>(string path, Func<T> create)
    {
        try
        {
            return create();
        }
        catch (ArgumentException ex)
        {
            throw new ProjectFormatException(path, ex.Message, ex);
        }
    }
}
