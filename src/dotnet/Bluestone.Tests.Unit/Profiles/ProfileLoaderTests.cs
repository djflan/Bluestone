using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Midi;
using Bluestone.Profiles;
using CsCheck;

namespace Bluestone.Tests.Unit.Profiles;

public sealed class ProfileLoaderTests
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

    private static ProfileLoadResult Load(string json) => ProfileLoader.Load(Encoding.UTF8.GetBytes(json));

    /// <summary>Loads <see cref="Minimal"/> after applying <paramref name="edit"/> to its JSON tree.</summary>
    private static ProfileLoadResult LoadWith(Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(Minimal)!.AsObject();
        edit(root);
        return Load(root.ToJsonString());
    }

    private static void AssertError(ProfileLoadResult result, string path, string fragment)
    {
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Severity == ProfileDiagnosticSeverity.Error && d.Path == path && d.Message.Contains(fragment, StringComparison.Ordinal));
    }

    private static void AssertWarning(ProfileLoadResult result, string path)
    {
        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        Assert.Contains(result.Diagnostics, d => d.Severity == ProfileDiagnosticSeverity.Warning && d.Path == path);
    }

    [Fact]
    public void MinimalProfile_Loads()
    {
        var result = Load(Minimal);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        Assert.Empty(result.Diagnostics);
        Assert.Equal("test.synth", result.Profile!.Id);
        Assert.Equal(VerificationLevel.Unverified, result.Profile.Provenance.Verification);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAllowedForHandEditing() =>
        Assert.True(Load(Minimal.Replace("\"version\": \"1.0.0\",", "// a comment\n\"version\": \"1.0.0\",", StringComparison.Ordinal).Replace("\"unverified\"", "\"unverified\",", StringComparison.Ordinal)).Succeeded);

    [Theory]
    [InlineData("format", "\"other\"", "$.format", "must be")]
    [InlineData("schemaVersion", "2", "$.schemaVersion", "newer version of Bluestone")]
    [InlineData("schemaVersion", "0", "$.schemaVersion", "not supported")]
    [InlineData("id", "\"Not Valid\"", "$.id", "lowercase")]
    [InlineData("name", "\"\"", "$.name", "must not be empty")]
    [InlineData("name", "42", "$.name", "must be a string")]
    [InlineData("name", "\"bad\\u0007name\"", "$.name", "control characters")]
    [InlineData("protocols", "[\"midi3\"]", "$.protocols[0]", "must be one of")]
    [InlineData("drumChannels", "[0]", "$.drumChannels[0]", "1 to 16")]
    public void InvalidTopLevelValues_AreRejectedWithTheirPath(string property, string json, string path, string fragment) =>
        AssertError(LoadWith(root => root[property] = JsonNode.Parse(json)), path, fragment);

    [Fact]
    public void MissingRequiredProperties_AreReported()
    {
        var result = LoadWith(root =>
        {
            root.Remove("name");
            root.Remove("provenance");
        });

        AssertError(result, "$.name", "is required");
        AssertError(result, "$.provenance", "where its data came from");
    }

    [Fact]
    public void Provenance_MustConfirmRedistribution()
    {
        var result = LoadWith(root => root["provenance"]!["redistributionConfirmed"] = false);

        AssertError(result, "$.provenance.redistributionConfirmed", "redistributed");
    }

    [Fact]
    public void Provenance_RequiresSourcesAndAKnownVerificationLevel()
    {
        var result = LoadWith(root =>
        {
            root["provenance"]!["sources"] = new JsonArray();
            root["provenance"]!["verification"] = "probably";
        });

        AssertError(result, "$.provenance.sources", "at least one");
        AssertError(result, "$.provenance.verification", "must be one of");
    }

    [Fact]
    public void Banks_ReportDuplicateAndOutOfRangePrograms()
    {
        var result = LoadWith(root => root["banks"] = JsonNode.Parse("""
            [{ "id": "a", "name": "A", "msb": 0, "programs": [
                { "number": 1, "name": "One" },
                { "number": 1, "name": "Again" },
                { "number": 129, "name": "Too far" }
            ]}]
            """));

        AssertError(result, "$.banks[0].programs[1].number", "more than once");
        AssertError(result, "$.banks[0].programs[2].number", "between 1 and 128");
    }

    [Fact]
    public void Banks_MapOneBasedProgramNumbersToWireValues()
    {
        var result = LoadWith(root => root["banks"] = JsonNode.Parse("""[{ "id": "a", "name": "A", "msb": 3, "lsb": 4, "programs": [{ "number": 1, "name": "One", "category": "Keys" }] }]"""));

        var bank = Assert.Single(result.Profile!.Banks);
        Assert.Equal(new ProgramNumber(0), bank.Programs[0].Program);
        Assert.Equal((new SevenBitValue(3), new SevenBitValue(4)), (bank.Msb!.Value, bank.Lsb!.Value));
    }

    [Fact]
    public void Banks_WarnAboutSharedBankSelectValues() =>
        AssertWarning(
            LoadWith(root => root["banks"] = JsonNode.Parse("""[{ "id": "a", "name": "A", "msb": 1 }, { "id": "b", "name": "B", "msb": 1 }]""")),
            "$.banks[1]");

    [Fact]
    public void DrumKits_MustReferenceDefinedBanks() =>
        AssertError(
            LoadWith(root => root["drumKits"] = JsonNode.Parse("""[{ "id": "k", "name": "Kit", "bank": "nope", "program": 1 }]""")),
            "$.drumKits[0].bank",
            "not defined");

    [Theory]
    [InlineData("43 10 F7", "start with F0")]
    [InlineData("F0 43 10", "end with F7")]
    [InlineData("F0 43 90 F7", "not a data byte")]
    [InlineData("F0 {nope} F7", "not a declared parameter")]
    [InlineData("{device} 43 F7", "placeholders can only appear in between")]
    [InlineData("F0 4G F7", "neither a hex byte")]
    [InlineData("F0", "2-512 bytes")]
    public void SysExTemplates_AreValidatedByteByByte(string bytes, string fragment) =>
        AssertError(
            LoadWith(root => root["sysex"] = new JsonArray(new JsonObject
            {
                ["id"] = "t",
                ["name"] = "T",
                ["effect"] = "parameter",
                ["bytes"] = bytes,
                ["parameters"] = JsonNode.Parse("""[{ "name": "device", "min": 0, "max": 127, "default": 0 }]"""),
            })),
            "$.sysex[0].bytes",
            fragment);

    [Fact]
    public void SysExTemplates_WarnAboutUnusedParameters() =>
        AssertWarning(
            LoadWith(root => root["sysex"] = JsonNode.Parse("""[{ "id": "t", "name": "T", "effect": "reset", "bytes": "F0 7E 7F 09 01 F7", "parameters": [{ "name": "x", "min": 0, "max": 1, "default": 0 }] }]""")),
            "$.sysex[0].bytes");

    [Fact]
    public void SysExTemplateParameters_RequireConsistentRanges() =>
        AssertError(
            LoadWith(root => root["sysex"] = JsonNode.Parse("""[{ "id": "t", "name": "T", "effect": "reset", "bytes": "F0 {x} F7", "parameters": [{ "name": "x", "min": 10, "max": 5, "default": 7 }] }]""")),
            "$.sysex[0].parameters[0]",
            "min ≤ default ≤ max");

    [Fact]
    public void Initialization_MustReferenceValidTemplates() =>
        AssertError(
            LoadWith(root => root["initialization"] = JsonNode.Parse("""[{ "sysex": "missing" }]""")),
            "$.initialization[0].sysex",
            "not defined");

    [Fact]
    public void UnknownProperties_AreWarnings() =>
        AssertWarning(LoadWith(root => root["colour"] = "blue"), "$.colour");

    [Fact]
    public void Extensions_ArePreservedAndShouldBeNamespaced()
    {
        var result = LoadWith(root => root["extensions"] = JsonNode.Parse("""{ "org.example.ui": { "color": "#336699" }, "plain": 1 }"""));

        AssertWarning(result, "$.extensions.plain");
        Assert.Equal("#336699", result.Profile!.Extensions["org.example.ui"].GetProperty("color").GetString());
    }

    [Fact]
    public void Identity_RequiresValidManufacturerIds()
    {
        var result = LoadWith(root => root["identity"] = JsonNode.Parse("""[{ "manufacturerId": "43 10" }, { "manufacturerId": "01 20 29" }, { "manufacturerId": "00 20 29", "family": "01 02" }]"""));

        AssertError(result, "$.identity[0].manufacturerId", "one byte");
        AssertError(result, "$.identity[1].manufacturerId", "start with 00");
    }

    [Fact]
    public void Limits_AreEnforced()
    {
        Assert.False(ProfileLoader.Load(Encoding.UTF8.GetBytes(Minimal), new ProfileLoadOptions { MaxBytes = 10 }).Succeeded);
        AssertError(Load("{ not json"), "$", "not valid JSON");
        AssertError(Load(new string('[', 100) + new string(']', 100)), "$", "not valid JSON");
        AssertError(Load("[]"), "$", "must be an object");
        AssertError(LoadWith(root => root["controllers"] = new JsonArray([.. Enumerable.Range(0, 129).Select(i => (JsonNode)new JsonObject { ["number"] = i % 128, ["name"] = "c" })])), "$.controllers", "at most 128");
    }

    [Fact]
    public void Template_RendersWithDefaultsAndOverrides()
    {
        var result = LoadWith(root => root["sysex"] = JsonNode.Parse("""[{ "id": "on", "name": "On", "effect": "reset", "bytes": "F0 43 {device} 4C 00 00 7E 00 F7", "parameters": [{ "name": "device", "min": 16, "max": 31, "default": 16 }] }]"""));
        var template = result.Profile!.FindTemplate("on")!;

        Assert.Equal([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7], template.Render().Bytes.ToArray());
        Assert.Equal(0x13, template.Render(new Dictionary<string, int> { ["device"] = 19 }).Bytes[2]);
        Assert.Throws<ArgumentException>(() => template.Render(new Dictionary<string, int> { ["device"] = 32 }));
        Assert.Throws<ArgumentException>(() => template.Render(new Dictionary<string, int> { ["other"] = 1 }));
        Assert.True(template.RequiresConfirmation);
        Assert.Equal("F0 43 {device} 4C 00 00 7E 00 F7", template.Pattern);
    }

    [Fact]
    public void Bank_SelectsProgramWithItsBankNumbers()
    {
        var bank = new ProfileBank("b", "B", BankKind.Melodic, new SevenBitValue(127), new SevenBitValue(0), []);

        Assert.Equal(new ProgramSelection(new ProgramNumber(25), new SevenBitValue(127), new SevenBitValue(0)), bank.Select(new ProgramNumber(25)));
        Assert.Equal(new ProgramSelection(new ProgramNumber(0)), (bank with { Msb = null, Lsb = null }).Select(new ProgramNumber(0)));
    }

    [Fact]
    public void MutatedProfiles_NeverThrow() =>
        Gen.Select(Gen.Int[0, Minimal.Length - 1], Gen.Char).Array[1, 5].Sample(edits =>
        {
            var chars = Minimal.ToCharArray();
            foreach (var (index, value) in edits)
            {
                chars[index] = value;
            }

            _ = Load(new string(chars));
            return true;
        }, iter: 2000);
}
