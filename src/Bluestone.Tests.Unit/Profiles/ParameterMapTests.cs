using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Midi.SysEx;
using Bluestone.Profiles;

namespace Bluestone.Tests.Unit.Profiles;

public sealed class ParameterMapTests
{
    private const string Minimal = """
        {
          "format": "bluestone-device-profile",
          "schemaVersion": 1,
          "id": "test.synth",
          "version": "1.0.0",
          "name": "Test Synth",
          "provenance": {
            "sources": ["Written for tests"],
            "contributors": ["Bluestone contributors"],
            "license": "MIT",
            "redistributionConfirmed": true,
            "verification": "unverified"
          }
        }
        """;

    private static readonly MidiChannel Channel3 = MidiChannel.FromNumber(3);

    private static ProfileLoadResult LoadMap(string mapJson)
    {
        var root = JsonNode.Parse(Minimal)!.AsObject();
        root["parameterMap"] = JsonNode.Parse(mapJson);
        return ProfileLoader.Load(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static ParameterMapping LoadOne(string entryJson)
    {
        var result = LoadMap($"[{entryJson}]");
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        return Assert.Single(result.Profile!.ParameterMap);
    }

    private static void AssertError(string entryJson, string path, string fragment)
    {
        var result = LoadMap($"[{entryJson}]");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Severity == ProfileDiagnosticSeverity.Error && d.Path == path && d.Message.Contains(fragment, StringComparison.Ordinal));
    }

    // Each controller message as (controller, 7-bit value), each SysEx as hex.
    private static List<string> Wire(ImmutableArray<TrackEvent> events) =>
        [.. events.Select(e => e switch
        {
            ControllerEvent c => $"CC{c.Controller.Value}={c.Value.ToSevenBit()}",
            SysExEvent s => Convert.ToHexString(s.Message.Bytes.Span),
            _ => throw new InvalidOperationException(),
        })];

    [Fact]
    public void Controller_SevenBit_IsOneControlChange()
    {
        var mapping = LoadOne("""{ "id": "volume", "name": "Volume", "kind": "cc", "controller": 7, "default": 100 }""");

        Assert.Equal((ParameterMappingKind.Controller, 7, 0, 127, 100), (mapping.Kind, mapping.Bits, mapping.Min, mapping.Max, mapping.Default));
        Assert.Equal(["CC7=100"], Wire(mapping.Encode(100, Channel3, new Tick(5))));
        Assert.All(mapping.Encode(1, Channel3, new Tick(5)), e => Assert.Equal((new Tick(5), Channel3), (e.Position, ((ChannelEvent)e).Channel)));
    }

    [Fact]
    public void Controller_FourteenBit_SendsMsbThenLsb()
    {
        var mapping = LoadOne("""{ "id": "mod", "name": "Modulation", "kind": "cc", "bits": 14, "controller": 1 }""");

        Assert.Equal(16383, mapping.Max);
        Assert.Equal(["CC1=64", "CC33=5"], Wire(mapping.Encode((64 << 7) | 5, Channel3, Tick.Zero)));
    }

    [Fact]
    public void Rpn_SevenBit_SelectsSetsAndNulls()
    {
        var mapping = LoadOne("""{ "id": "bend", "name": "Bend Range", "kind": "rpn", "msb": 0, "lsb": 0, "min": 0, "max": 24 }""");

        Assert.True(mapping.NullAfter);
        Assert.Equal(["CC101=0", "CC100=0", "CC6=12", "CC101=127", "CC100=127"], Wire(mapping.Encode(12, Channel3, Tick.Zero)));
    }

    [Fact]
    public void Nrpn_FourteenBit_SendsBothDataEntryBytes()
    {
        var mapping = LoadOne("""{ "id": "cutoff", "name": "Cutoff", "kind": "nrpn", "bits": 14, "msb": 1, "lsb": 32 }""");

        Assert.Equal(["CC99=1", "CC98=32", "CC6=100", "CC38=3", "CC101=127", "CC100=127"], Wire(mapping.Encode((100 << 7) | 3, Channel3, Tick.Zero)));
    }

    [Fact]
    public void Nrpn_WithoutNull_EndsWithDataEntry()
    {
        var mapping = LoadOne("""{ "id": "cutoff", "name": "Cutoff", "kind": "nrpn", "msb": 1, "lsb": 32, "nullAfter": false, "default": 64 }""");

        Assert.Equal(["CC99=1", "CC98=32", "CC6=70"], Wire(mapping.Encode(70, Channel3, Tick.Zero)));
    }

    [Fact]
    public void SysEx_XgPartParameter_FillsDeviceChannelAndValue()
    {
        var mapping = LoadOne("""{ "id": "part-volume", "name": "Part Volume", "kind": "sysex", "bytes": "F0 43 {device} 4C 08 {channel} 0B {value} F7", "device": 16 }""");

        Assert.Equal("F0 43 {device} 4C 08 {channel} 0B {value} F7", mapping.Pattern);
        Assert.Equal(["F043104C08020B64F7"], Wire(mapping.Encode(100, Channel3, Tick.Zero)));
    }

    [Fact]
    public void SysEx_Nibbles_SplitTheValue()
    {
        var mapping = LoadOne("""{ "id": "detune", "name": "Detune", "kind": "sysex", "bytes": "F0 43 {device} 4C 08 {channel} 09 {value[7:4]} {value[3:0]} F7", "device": 16, "max": 255, "default": 128 }""");

        Assert.Equal(8, mapping.Bits);
        Assert.Equal(["F043104C0802090A05F7"], Wire(mapping.Encode(0xA5, Channel3, Tick.Zero)));
    }

    [Fact]
    public void SysEx_RolandChecksum_MatchesKnownGsMessages()
    {
        var mapping = LoadOne("""
            { "id": "master-volume", "name": "Master Volume", "kind": "sysex", "bytes": "F0 41 {device} 42 12 40 00 04 {value} {checksum} F7",
              "device": 16, "checksum": { "convention": "roland", "start": 5 } }
            """);

        // GS master volume 127 is F0 41 10 42 12 40 00 04 7F 3D F7.
        Assert.Equal(["F041104212400004" + "7F3DF7"], Wire(mapping.Encode(127, Channel3, Tick.Zero)));
        var message = ((SysExEvent)mapping.Encode(37, Channel3, Tick.Zero)[0]).Message;
        Assert.True(Assert.IsType<GsDataSet>(SysExInterpreter.Interpret(message)).ChecksumValid);
    }

    [Fact]
    public void SysEx_YamahaChecksum_MakesAValidBulkDump()
    {
        // An XG bulk dump of one multi part byte: byte count 00 01, address 08 pp 0B, data, checksum over count to data.
        var mapping = LoadOne("""
            { "id": "dump-volume", "name": "Volume by bulk dump", "kind": "sysex", "bytes": "F0 43 {device} 4C 00 01 08 {channel} 0B {value} {checksum} F7",
              "device": 0, "checksum": { "convention": "yamaha", "start": 4 } }
            """);

        foreach (var value in new[] { 0, 1, 64, 100, 127 })
        {
            var message = ((SysExEvent)mapping.Encode(value, Channel3, Tick.Zero)[0]).Message;
            var dump = Assert.IsType<XgBulkDump>(SysExInterpreter.Interpret(message));
            Assert.True(dump.ChecksumValid);
            Assert.Equal<byte>([(byte)value], dump.Data);
        }
    }

    [Fact]
    public void SysExChecksum_IsTheGsDialectChecksum() =>
        Assert.Equal(GsDialect.Checksum([0x40, 0x00, 0x7F, 0x00]), SysExChecksum.Compute([0x40, 0x00, 0x7F, 0x00]));

    [Fact]
    public void Scale_FullRange_InvertsMidiScalingExactly()
    {
        var seven = LoadOne("""{ "id": "a", "name": "A", "kind": "cc", "controller": 7 }""");
        var fourteen = LoadOne("""{ "id": "b", "name": "B", "kind": "cc", "bits": 14, "controller": 7 }""");

        for (var value = 0; value <= 127; value++)
        {
            Assert.Equal(value, seven.Scale(ControlValue.FromSevenBit(value)));
        }

        for (var value = 0; value <= 16383; value++)
        {
            Assert.Equal(value, fourteen.Scale(ControlValue.FromFourteenBit(value)));
        }
    }

    [Fact]
    public void Scale_MapsNormalizedValuesOntoTheRange()
    {
        var mapping = LoadOne("""{ "id": "bend", "name": "Bend Range", "kind": "rpn", "msb": 0, "lsb": 0, "min": 0, "max": 24 }""");

        Assert.Equal(0, mapping.Scale(ControlValue.Min));
        Assert.Equal(12, mapping.Scale(ControlValue.Center));
        Assert.Equal(24, mapping.Scale(ControlValue.Max));
    }

    [Fact]
    public void Encode_RefusesValuesOutsideTheRange()
    {
        var mapping = LoadOne("""{ "id": "bend", "name": "Bend Range", "kind": "rpn", "msb": 0, "lsb": 0, "min": 0, "max": 24 }""");

        Assert.Throws<ArgumentOutOfRangeException>(() => mapping.Encode(25, Channel3, Tick.Zero));
    }

    [Theory]
    [InlineData("""{ "id": "x", "name": "X", "kind": "pitch" }""", "$.parameterMap[0].kind", "must be one of")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "controller": 6 }""", "$.parameterMap[0].controller", "cannot be automated")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "controller": 0 }""", "$.parameterMap[0].controller", "cannot be automated")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "controller": 33 }""", "$.parameterMap[0].controller", "cannot be automated")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "bits": 14, "controller": 40 }""", "$.parameterMap[0].controller", "from 0 to 31")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "bits": 8, "controller": 7 }""", "$.parameterMap[0].bits", "must be 7 or 14")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "controller": 7, "max": 200 }""", "$.parameterMap[0].max", "between 0 and 127")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "controller": 7, "min": 10, "max": 10 }""", "$.parameterMap[0]", "min must be less than max")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "cc", "controller": 7, "min": 10, "default": 5 }""", "$.parameterMap[0].default", "between min and max")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "rpn", "msb": 127, "lsb": 127 }""", "$.parameterMap[0]", "null parameter")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "nrpn", "msb": 1 }""", "$.parameterMap[0].lsb", "is required")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 08 00 0B F7" }""", "$.parameterMap[0].bytes", "must contain {value}")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 {device} 4C 08 00 0B {value} F7" }""", "$.parameterMap[0].bytes", "device number byte")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 41 10 42 12 40 00 04 {value} {checksum} F7" }""", "$.parameterMap[0].bytes", "must declare \"checksum\"")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 41 10 42 12 40 00 04 {value} F7", "checksum": { "convention": "roland", "start": 5 } }""", "$.parameterMap[0].bytes", "does not contain {checksum}")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 41 10 42 12 40 00 04 {value} {checksum} F7", "checksum": { "convention": "roland", "start": 9 } }""", "$.parameterMap[0].checksum.start", "before {checksum}")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 41 10 42 12 40 00 04 {value} {checksum} F7", "checksum": { "convention": "sony", "start": 5 } }""", "$.parameterMap[0].checksum.convention", "must be one of")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 08 00 0B {value} F7", "max": 255 }""", "$.parameterMap[0].bytes", "does not send every bit")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 08 00 0B {value[13:0]} F7", "max": 255 }""", "$.parameterMap[0].bytes", "1 to 7 bits")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 08 00 0B {volume} F7" }""", "$.parameterMap[0].bytes", "not a known placeholder")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 80 00 0B {value} F7" }""", "$.parameterMap[0].bytes", "not a data byte")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "{value} 43 10 4C 08 00 0B F7" }""", "$.parameterMap[0].bytes", "placeholders can only appear in between")]
    [InlineData("""{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 08 00 0B {value} F7", "max": 70000 }""", "$.parameterMap[0].max", "between 0 and 65535")]
    public void InvalidMappings_AreRejectedWithClearErrors(string entry, string path, string fragment) =>
        AssertError(entry, path, fragment);

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        var result = LoadMap("""
            [{ "id": "volume", "name": "Volume", "kind": "cc", "controller": 7 },
             { "id": "volume", "name": "Volume again", "kind": "cc", "controller": 11 }]
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Path == "$.parameterMap[1].id" && d.Message.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void PropertiesOfAnotherKind_AreWarnedAboutAndIgnored()
    {
        var result = LoadMap("""[{ "id": "volume", "name": "Volume", "kind": "cc", "controller": 7, "nullAfter": false }]""");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Severity == ProfileDiagnosticSeverity.Warning && d.Path == "$.parameterMap[0].nullAfter" && d.Message.Contains("not used by this kind", StringComparison.Ordinal));
    }

    [Fact]
    public void ChecksumConvention_ForAnotherManufacturer_IsWarnedAbout()
    {
        var result = LoadMap("""
            [{ "id": "x", "name": "X", "kind": "sysex", "bytes": "F0 43 10 4C 00 01 08 00 0B {value} {checksum} F7", "checksum": { "convention": "roland", "start": 4 } }]
            """);

        Assert.True(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Severity == ProfileDiagnosticSeverity.Warning && d.Path == "$.parameterMap[0].checksum.convention");
    }

    [Fact]
    public void FindMapping_FindsByKey()
    {
        var profile = LoadMap("""[{ "id": "volume", "name": "Volume", "kind": "cc", "controller": 7 }]""").Profile!;

        Assert.Equal("Volume", profile.FindMapping("volume")!.Name);
        Assert.Null(profile.FindMapping("pan"));
    }
}
