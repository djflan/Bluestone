using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bluestone.Domain.Devices;
using static Bluestone.Infrastructure.Projects.NodeReader;

namespace Bluestone.Infrastructure.Projects;

/// <summary>
/// Reads and writes device chain presets (<c>.bluestone-chain</c>): a named, ordered list of device templates
/// with their parameters, bypass state, and plugin state, but no identities and no connections (ADR 0022).
/// Loading a preset always creates new device instances, so two tracks loaded from one preset share no
/// state. Hardware references are not part of a preset, so a preset moves between machines unchanged.
/// </summary>
public static class ChainPresetSerializer
{
    public const string FormatName = "bluestone-chain-preset";
    public const int CurrentFormatVersion = 1;
    public const int MaxBytes = 256 * 1024 * 1024;

    public static byte[] Serialize(DeviceChainPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("formatVersion", CurrentFormatVersion);
            writer.WriteStartObject("preset");
            writer.WriteString("name", preset.Name);
            writer.WriteStartArray("devices");
            foreach (var device in preset.Devices)
            {
                writer.WriteStartObject();
                ProjectSerializer.WriteDeviceBody(writer, device.Definition, device.Name, device.IsBypassed, device.Parameters, device.State);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <exception cref="ProjectFormatException">The data is not a readable preset.</exception>
    /// <exception cref="ProjectVersionException">The preset was written by a newer version.</exception>
    public static DeviceChainPreset Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw new ProjectFormatException("$", "the preset file is too large.");
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
            if (root["format"] is not JsonValue format || !format.TryGetValue(out string? name) || name != FormatName)
            {
                throw new ProjectFormatException("$.format", $"must be \"{FormatName}\"; this is not a Bluestone chain preset.");
            }

            var version = Int(root, "formatVersion", "$", 0, int.MaxValue);
            if (version > CurrentFormatVersion)
            {
                throw new ProjectVersionException(version, CurrentFormatVersion);
            }

            if (version < CurrentFormatVersion)
            {
                throw new ProjectFormatException("$.formatVersion", string.Create(CultureInfo.InvariantCulture, $"format {version} is not supported."));
            }

            const string path = "$.preset";
            var preset = Object(root, "preset", "$");
            var devices = Array(preset, "devices", path, DeviceChain.MaxDevices).Select((item, i) =>
            {
                var at = $"{path}.devices[{i}]";
                var body = ProjectSerializer.ReadDeviceBody(Object(item, at), at);
                var device = new DevicePreset { Definition = body.Definition, Name = body.Name, IsBypassed = body.Bypassed, Parameters = body.Parameters, State = body.State };
                ValidateParameters(device, at);
                return device;
            }).ToList();

            var presetName = String(preset, "name", path, DeviceChainPreset.MaxNameLength);
            return new DeviceChainPreset { Name = presetName, Devices = [.. devices] };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ProjectFormatException("$", $"the file is malformed ({ex.Message}).", ex);
        }
    }

    // Instantiating checks what a device instance checks (a parameter stored twice, for example), so a
    // broken preset fails when it is read, not when it is loaded onto a track.
    private static void ValidateParameters(DevicePreset device, string path)
    {
        try
        {
            device.Instantiate();
        }
        catch (ArgumentException ex)
        {
            throw new ProjectFormatException($"{path}.parameters", ex.Message, ex);
        }
    }
}
