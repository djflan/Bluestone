using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Infrastructure.Projects;

namespace Bluestone.Tests.Unit.Infrastructure;

public sealed class ChainPresetSerializerTests
{
    private static DeviceChainPreset Sample() => new()
    {
        Name = "Arp → Vital → EQ",
        Devices =
        [
            DevicePreset.From(BuiltInDevices.CreateArpeggiator(rate: 3, ArpeggiatorPattern.Down, octaves: 2) with { Name = "Fast arp" }),
            DevicePreset.From(DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId("vst3:00112233445566778899AABBCCDDEEFF"), "Vital", "1.5")) with
            {
                Parameters = [new ParameterValue(new ParameterId(7), ControlValue.Center)],
                State = new PluginState(ByteBlock.Copy([9, 8, 7]), "vst3-component-v1"),
            }),
            DevicePreset.From(TestDevices.Instance(TestDevices.Filter) with { IsBypassed = true }),
        ],
    };

    [Fact]
    public void RoundTrip_KeepsOrderNamesParametersBypassAndState()
    {
        var preset = Sample();

        var restored = ChainPresetSerializer.Deserialize(ChainPresetSerializer.Serialize(preset));

        Assert.Equal(preset.Name, restored.Name);
        Assert.Equal(preset.Devices.Select(d => (d.Definition, d.Name, d.IsBypassed, d.State?.Format)), restored.Devices.Select(d => (d.Definition, d.Name, d.IsBypassed, d.State?.Format)));
        Assert.Equal(preset.Devices.Select(d => d.Parameters.ToArray()), restored.Devices.Select(d => d.Parameters.ToArray()));
        Assert.Equal([9, 8, 7], restored.Devices[1].State!.Data.ToArray());
    }

    [Fact]
    public void APreset_HasNoIdentities()
    {
        var text = Encoding.UTF8.GetString(ChainPresetSerializer.Serialize(Sample()));

        Assert.StartsWith("{\n  \"format\": \"bluestone-chain-preset\",\n  \"formatVersion\": 1,", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\": \"0", text, StringComparison.Ordinal);
        Assert.All(JsonNode.Parse(text)!["preset"]!["devices"]!.AsArray(), d => Assert.Null(d!["id"]));
    }

    [Fact]
    public void NewerPresets_AreRefused_AndBrokenOnesReportTheirPath()
    {
        var json = JsonNode.Parse(ChainPresetSerializer.Serialize(Sample()))!.AsObject();
        json["formatVersion"] = 2;
        Assert.Throws<ProjectVersionException>(() => ChainPresetSerializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));

        json["formatVersion"] = 1;
        json["preset"]!["devices"]![1]!["parameters"]!.AsArray().Add(JsonNode.Parse("""{ "id": 7, "value": 1 }"""));
        var error = Assert.Throws<ProjectFormatException>(() => ChainPresetSerializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.Equal("$.preset.devices[1].parameters", error.JsonPath);

        Assert.Throws<ProjectFormatException>(() => ChainPresetSerializer.Deserialize("""{ "format": "bluestone-project", "formatVersion": 1 }"""u8));
    }
}
