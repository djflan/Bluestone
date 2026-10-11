using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bluestone.Domain.Midi;

namespace Bluestone.Profiles;

/// <summary>
/// Loads and validates device profiles (schema version 1). Every problem is reported with its JSON
/// path; a profile with any error is rejected. Profiles are untrusted input: size, depth, counts, and
/// string lengths are bounded, and SysEx templates are checked byte by byte.
/// See docs/profiles.md for the format.
/// </summary>
public static partial class ProfileLoader
{
    public const string FormatName = "bluestone-device-profile";
    public const string FileExtension = ".bluestone-profile.json";

    private const int MaxDepth = 32;

    private static readonly Dictionary<string, ProtocolFamily> Protocols = new(StringComparer.Ordinal)
    {
        ["gm1"] = ProtocolFamily.Gm1,
        ["gm2"] = ProtocolFamily.Gm2,
        ["gs"] = ProtocolFamily.Gs,
        ["xg"] = ProtocolFamily.Xg,
        ["custom"] = ProtocolFamily.Custom,
    };

    private static readonly Dictionary<string, VerificationLevel> Verifications = new(StringComparer.Ordinal)
    {
        ["unverified"] = VerificationLevel.Unverified,
        ["simulated"] = VerificationLevel.Simulated,
        ["hardware-verified"] = VerificationLevel.HardwareVerified,
    };

    private static readonly Dictionary<string, SysExEffect> Effects = new(StringComparer.Ordinal)
    {
        ["parameter"] = SysExEffect.Parameter,
        ["reset"] = SysExEffect.Reset,
        ["bulk"] = SysExEffect.Bulk,
    };

    private static readonly Dictionary<string, BankKind> BankKinds = new(StringComparer.Ordinal)
    {
        ["melodic"] = BankKind.Melodic,
        ["drums"] = BankKind.Drums,
    };

    public static ProfileLoadResult LoadFile(string path, ProfileLoadOptions? options = null)
    {
        options ??= ProfileLoadOptions.Default;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Fail("$", "The profile file does not exist.");
        }

        if (info.Length > options.MaxBytes)
        {
            return Fail("$", string.Create(CultureInfo.InvariantCulture, $"The profile is larger than the {options.MaxBytes}-byte limit."));
        }

        return Load(File.ReadAllBytes(path), options);
    }

    public static ProfileLoadResult Load(ReadOnlySpan<byte> utf8Json, ProfileLoadOptions? options = null)
    {
        options ??= ProfileLoadOptions.Default;
        if (utf8Json.Length > options.MaxBytes)
        {
            return Fail("$", string.Create(CultureInfo.InvariantCulture, $"The profile is larger than the {options.MaxBytes}-byte limit."));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = MaxDepth,
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            return Fail("$", $"The profile is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var walker = new JsonWalker();
            var profile = Read(document.RootElement, walker);
            return new ProfileLoadResult(walker.HasErrors ? null : profile, walker.Diagnostics);
        }
    }

    private static ProfileLoadResult Fail(string path, string message) =>
        new(null, [new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, path, message)]);

    private static DeviceProfile? Read(JsonElement root, JsonWalker w)
    {
        const string path = "$";
        if (!w.ExpectObject(root, path))
        {
            return null;
        }

        w.RejectUnknown(root, path, "$schema", "format", "schemaVersion", "id", "version", "name", "manufacturer", "model", "description",
            "notices", "provenance", "protocols", "identity", "drumChannels", "banks", "drumKits", "controllers", "parameters", "sysex",
            "parameterMap", "initialization", "limitations", "extensions");

        var format = w.String(root, "format", path, required: true, 64);
        if (format is not null && format != FormatName)
        {
            w.Error("$.format", $"must be \"{FormatName}\".");
        }

        var schemaVersion = w.Int(root, "schemaVersion", path, required: true, 0, int.MaxValue);
        if (schemaVersion is { } version && version != DeviceProfile.CurrentSchemaVersion)
        {
            w.Error("$.schemaVersion", version > DeviceProfile.CurrentSchemaVersion
                ? string.Create(CultureInfo.InvariantCulture, $"version {version} needs a newer version of Bluestone (this version reads {DeviceProfile.CurrentSchemaVersion}).")
                : string.Create(CultureInfo.InvariantCulture, $"version {version} is not supported."));
            return null;
        }

        var id = w.String(root, "id", path, required: true, 100);
        if (id is not null && !ProfileIdPattern().IsMatch(id))
        {
            w.Error("$.id", "must be lowercase letters and digits separated by '.' or '-', e.g. \"community.vendor.model\".");
        }

        var banks = ReadBanks(root, w);
        var sysEx = ReadSysEx(root, w);
        var profile = new DeviceProfile
        {
            Id = id ?? string.Empty,
            Version = w.String(root, "version", path, required: true, 32) ?? string.Empty,
            Name = w.String(root, "name", path, required: true, 128) ?? string.Empty,
            Manufacturer = w.String(root, "manufacturer", path, required: false, 128),
            Model = w.String(root, "model", path, required: false, 128),
            Description = w.String(root, "description", path, required: false, 4096),
            Notices = w.Strings(root, "notices", path, required: false, 16, 1024),
            Provenance = ReadProvenance(root, w),
            Protocols = ReadProtocols(root, w),
            Identity = ReadIdentity(root, w),
            DrumChannels = ReadDrumChannels(root, w),
            Banks = banks,
            DrumKits = ReadDrumKits(root, w, banks),
            Controllers = ReadControllers(root, w),
            Parameters = ReadParameters(root, w),
            Templates = sysEx,
            ParameterMap = ReadParameterMap(root, w),
            Initialization = ReadInitialization(root, w, sysEx),
            Limitations = w.Strings(root, "limitations", path, required: false, 64, 512),
            Extensions = ReadExtensions(root, w),
        };

        return profile;
    }

    private static ProfileProvenance ReadProvenance(JsonElement root, JsonWalker w)
    {
        const string path = "$.provenance";
        var fallback = new ProfileProvenance([], [], string.Empty, false, VerificationLevel.Unverified, null);
        if (!root.TryGetProperty("provenance", out var p))
        {
            w.Error(path, "is required: every profile must say where its data came from.");
            return fallback;
        }

        if (!w.ExpectObject(p, path))
        {
            return fallback;
        }

        w.RejectUnknown(p, path, "sources", "contributors", "license", "redistributionConfirmed", "verification", "notes");
        var confirmed = w.Bool(p, "redistributionConfirmed", path, required: true);
        if (confirmed == false)
        {
            w.Error(JsonWalker.Child(path, "redistributionConfirmed"), "must be true: only data that may be redistributed can be shared in a profile.");
        }

        return new ProfileProvenance(
            w.Strings(p, "sources", path, required: true, 32, 512, nonEmpty: true),
            w.Strings(p, "contributors", path, required: true, 64, 128, nonEmpty: true),
            w.String(p, "license", path, required: true, 64) ?? string.Empty,
            confirmed ?? false,
            w.Enum(p, "verification", path, required: true, Verifications) ?? VerificationLevel.Unverified,
            w.String(p, "notes", path, required: false, 2048));
    }

    private static ImmutableArray<ProtocolFamily> ReadProtocols(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<ProtocolFamily>();
        foreach (var (item, at) in w.Array(root, "protocols", "$", required: false, 8))
        {
            if (item.ValueKind == JsonValueKind.String && Protocols.TryGetValue(item.GetString()!, out var protocol))
            {
                if (result.Contains(protocol))
                {
                    w.Warn(at, "is listed more than once.");
                }
                else
                {
                    result.Add(protocol);
                }
            }
            else
            {
                w.Error(at, $"must be one of {string.Join(", ", Protocols.Keys.Select(k => $"\"{k}\""))}.");
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<IdentityMatch> ReadIdentity(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<IdentityMatch>();
        foreach (var (item, at) in w.Array(root, "identity", "$", required: false, 16))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "manufacturerId", "family", "member");
            var manufacturer = w.DataBytes(item, "manufacturerId", at, required: true, 1, 3);
            if (manufacturer is { Length: 2 })
            {
                w.Error(JsonWalker.Child(at, "manufacturerId"), "must be one byte, or three bytes starting with 00.");
                continue;
            }

            if (manufacturer is { Length: 3 } && manufacturer.Value[0] != 0)
            {
                w.Error(JsonWalker.Child(at, "manufacturerId"), "three-byte manufacturer IDs start with 00.");
                continue;
            }

            var family = w.DataBytes(item, "family", at, required: false, 2, 2);
            var member = w.DataBytes(item, "member", at, required: false, 2, 2);
            if (manufacturer is { } m)
            {
                result.Add(new IdentityMatch(m, family, member));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<MidiChannel> ReadDrumChannels(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<MidiChannel>();
        foreach (var (item, at) in w.Array(root, "drumChannels", "$", required: false, 16))
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) || number is < 1 or > 16)
            {
                w.Error(at, "must be a channel number from 1 to 16.");
                continue;
            }

            var channel = MidiChannel.FromNumber(number);
            if (result.Contains(channel))
            {
                w.Warn(at, "is listed more than once.");
                continue;
            }

            result.Add(channel);
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<ProfileBank> ReadBanks(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<ProfileBank>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new Dictionary<(int?, int?, BankKind), string>();
        foreach (var (item, at) in w.Array(root, "banks", "$", required: false, 1024))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "id", "name", "kind", "msb", "lsb", "programs");
            var id = ReadLocalId(item, at, w);
            var name = w.String(item, "name", at, required: true, 128);
            var kind = w.Enum(item, "kind", at, required: false, BankKinds) ?? BankKind.Melodic;
            var msb = w.Int(item, "msb", at, required: false, 0, 127);
            var lsb = w.Int(item, "lsb", at, required: false, 0, 127);

            var programs = ImmutableArray.CreateBuilder<ProfileProgram>();
            var numbers = new HashSet<int>();
            foreach (var (program, programPath) in w.Array(item, "programs", at, required: false, 128))
            {
                if (!w.ExpectObject(program, programPath))
                {
                    continue;
                }

                w.RejectUnknown(program, programPath, "number", "name", "category");
                var number = w.Int(program, "number", programPath, required: true, 1, 128);
                var programName = w.String(program, "name", programPath, required: true, 64);
                var category = w.String(program, "category", programPath, required: false, 64);
                if (number is { } n && !numbers.Add(n))
                {
                    w.Error(JsonWalker.Child(programPath, "number"), string.Create(CultureInfo.InvariantCulture, $"program {n} is defined more than once in this bank."));
                    continue;
                }

                if (number is { } value && programName is not null)
                {
                    programs.Add(new ProfileProgram(ProgramNumber.FromNumber(value), programName, category));
                }
            }

            if (id is not null && !ids.Add(id))
            {
                w.Error(JsonWalker.Child(at, "id"), $"bank \"{id}\" is defined more than once.");
                continue;
            }

            if (addresses.TryGetValue((msb, lsb, kind), out var existing))
            {
                w.Warn(at, $"has the same bank select values as bank \"{existing}\".");
            }
            else if (id is not null)
            {
                addresses[(msb, lsb, kind)] = id;
            }

            if (id is not null && name is not null)
            {
                result.Add(new ProfileBank(id, name, kind, msb is { } m ? new SevenBitValue(m) : null, lsb is { } l ? new SevenBitValue(l) : null, programs.ToImmutable()));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<DrumKit> ReadDrumKits(JsonElement root, JsonWalker w, ImmutableArray<ProfileBank> banks)
    {
        var result = ImmutableArray.CreateBuilder<DrumKit>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, at) in w.Array(root, "drumKits", "$", required: false, 256))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "id", "name", "bank", "program", "notes");
            var id = ReadLocalId(item, at, w);
            var name = w.String(item, "name", at, required: true, 128);
            var bank = w.String(item, "bank", at, required: false, 64);
            if (bank is not null && !banks.Any(b => b.Id == bank))
            {
                w.Error(JsonWalker.Child(at, "bank"), $"refers to bank \"{bank}\", which is not defined.");
            }

            var program = w.Int(item, "program", at, required: true, 1, 128);
            var notes = ImmutableArray.CreateBuilder<DrumNote>();
            var seen = new HashSet<int>();
            foreach (var (note, notePath) in w.Array(item, "notes", at, required: false, 128))
            {
                if (!w.ExpectObject(note, notePath))
                {
                    continue;
                }

                w.RejectUnknown(note, notePath, "note", "name");
                var number = w.Int(note, "note", notePath, required: true, 0, 127);
                var noteName = w.String(note, "name", notePath, required: true, 64);
                if (number is { } n && !seen.Add(n))
                {
                    w.Error(JsonWalker.Child(notePath, "note"), string.Create(CultureInfo.InvariantCulture, $"note {n} is named more than once in this kit."));
                }
                else if (number is { } value && noteName is not null)
                {
                    notes.Add(new DrumNote(new NoteNumber(value), noteName));
                }
            }

            if (id is not null && !ids.Add(id))
            {
                w.Error(JsonWalker.Child(at, "id"), $"drum kit \"{id}\" is defined more than once.");
            }
            else if (id is not null && name is not null && program is { } p)
            {
                result.Add(new DrumKit(id, name, bank, ProgramNumber.FromNumber(p), notes.ToImmutable()));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<ProfileController> ReadControllers(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<ProfileController>();
        var seen = new HashSet<int>();
        foreach (var (item, at) in w.Array(root, "controllers", "$", required: false, 128))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "number", "name", "default");
            var number = w.Int(item, "number", at, required: true, 0, 127);
            var name = w.String(item, "name", at, required: true, 64);
            var defaultValue = w.Int(item, "default", at, required: false, 0, 127);
            if (number is { } n && !seen.Add(n))
            {
                w.Error(JsonWalker.Child(at, "number"), string.Create(CultureInfo.InvariantCulture, $"controller {n} is defined more than once."));
            }
            else if (number is { } value && name is not null)
            {
                result.Add(new ProfileController(new ControllerNumber(value), name, defaultValue is { } d ? new SevenBitValue(d) : null));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<ProfileParameter> ReadParameters(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<ProfileParameter>();
        foreach (var (item, at) in w.Array(root, "parameters", "$", required: false, 256))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "msb", "lsb", "name", "min", "max", "default");
            var msb = w.Int(item, "msb", at, required: true, 0, 127);
            var lsb = w.Int(item, "lsb", at, required: true, 0, 127);
            var name = w.String(item, "name", at, required: true, 64);
            var min = w.Int(item, "min", at, required: true, 0, FourteenBitValue.MaxValue);
            var max = w.Int(item, "max", at, required: true, 0, FourteenBitValue.MaxValue);
            var defaultValue = w.Int(item, "default", at, required: false, 0, FourteenBitValue.MaxValue);
            if (min > max)
            {
                w.Error(at, "min must not exceed max.");
                continue;
            }

            if (defaultValue is { } d && (d < min || d > max))
            {
                w.Error(JsonWalker.Child(at, "default"), "must be between min and max.");
                continue;
            }

            if (msb is { } m && lsb is { } l && name is not null && min is { } lo && max is { } hi)
            {
                result.Add(new ProfileParameter(new SevenBitValue(m), new SevenBitValue(l), name, lo, hi, defaultValue));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<SysExTemplate> ReadSysEx(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<SysExTemplate>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, at) in w.Array(root, "sysex", "$", required: false, 256))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "id", "name", "effect", "description", "bytes", "parameters");
            var id = ReadLocalId(item, at, w);
            var name = w.String(item, "name", at, required: true, 128);
            var effect = w.Enum(item, "effect", at, required: true, Effects);
            var description = w.String(item, "description", at, required: false, 1024);
            var parameters = ReadTemplateParameters(item, at, w);
            var tokens = ReadTemplateTokens(item, at, w, parameters);

            if (id is not null && !ids.Add(id))
            {
                w.Error(JsonWalker.Child(at, "id"), $"template \"{id}\" is defined more than once.");
            }
            else if (id is not null && name is not null && effect is { } e && tokens is { } t)
            {
                result.Add(new SysExTemplate(id, name, e, description, t, parameters));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<TemplateParameter> ReadTemplateParameters(JsonElement template, string path, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<TemplateParameter>();
        foreach (var (item, at) in w.Array(template, "parameters", path, required: false, 16))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "name", "min", "max", "default");
            var name = w.String(item, "name", at, required: true, 32);
            if (name is not null && !ParameterNamePattern().IsMatch(name))
            {
                w.Error(JsonWalker.Child(at, "name"), "must start with a letter and contain only letters, digits, and underscores.");
                continue;
            }

            var min = w.Int(item, "min", at, required: true, 0, 127);
            var max = w.Int(item, "max", at, required: true, 0, 127);
            var defaultValue = w.Int(item, "default", at, required: true, 0, 127);
            if (min > max || defaultValue < min || defaultValue > max)
            {
                w.Error(at, "requires min ≤ default ≤ max.");
                continue;
            }

            if (result.Any(p => p.Name == name))
            {
                w.Error(JsonWalker.Child(at, "name"), $"parameter \"{name}\" is defined more than once.");
                continue;
            }

            if (name is not null && min is { } lo && max is { } hi && defaultValue is { } d)
            {
                result.Add(new TemplateParameter(name, lo, hi, d));
            }
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<TemplateToken>? ReadTemplateTokens(JsonElement template, string path, JsonWalker w, ImmutableArray<TemplateParameter> parameters)
    {
        var text = w.String(template, "bytes", path, required: true, SysExTemplate.MaxTokens * 8);
        if (text is null)
        {
            return null;
        }

        var at = JsonWalker.Child(path, "bytes");
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > SysExTemplate.MaxTokens)
        {
            w.Error(at, string.Create(CultureInfo.InvariantCulture, $"must contain 2-{SysExTemplate.MaxTokens} bytes."));
            return null;
        }

        var tokens = ImmutableArray.CreateBuilder<TemplateToken>(parts.Length);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var valid = true;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isFirst = i == 0;
            var isLast = i == parts.Length - 1;
            if (part.Length > 2 && part[0] == '{' && part[^1] == '}')
            {
                var name = part[1..^1];
                if (isFirst || isLast)
                {
                    w.Error(at, "must start with F0 and end with F7; placeholders can only appear in between.");
                    valid = false;
                }
                else if (!parameters.Any(p => p.Name == name))
                {
                    w.Error(at, $"uses {{{name}}}, which is not a declared parameter.");
                    valid = false;
                }

                used.Add(name);
                tokens.Add(new TemplateToken(0, name));
                continue;
            }

            if (!JsonWalker.TryParseHexByte(part, out var literal))
            {
                w.Error(at, $"token {i + 1} (\"{part}\") is neither a hex byte nor a {{parameter}}.");
                valid = false;
                continue;
            }

            if ((isFirst && literal != SysExMessage.Start) || (isLast && literal != SysExMessage.End))
            {
                w.Error(at, "must start with F0 and end with F7.");
                valid = false;
            }
            else if (!isFirst && !isLast && literal > 0x7F)
            {
                w.Error(at, $"token {i + 1} ({part}) is not a data byte; bytes between F0 and F7 must be 00-7F.");
                valid = false;
            }

            tokens.Add(new TemplateToken(literal, null));
        }

        foreach (var parameter in parameters.Where(p => !used.Contains(p.Name)))
        {
            w.Warn(at, $"never uses parameter \"{parameter.Name}\".");
        }

        return valid ? tokens.ToImmutable() : null;
    }

    private static ImmutableArray<InitializationStep> ReadInitialization(JsonElement root, JsonWalker w, ImmutableArray<SysExTemplate> templates)
    {
        var result = ImmutableArray.CreateBuilder<InitializationStep>();
        foreach (var (item, at) in w.Array(root, "initialization", "$", required: false, 64))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            w.RejectUnknown(item, at, "sysex", "delayAfterMs");
            var template = w.String(item, "sysex", at, required: true, 64);
            var delay = w.Int(item, "delayAfterMs", at, required: false, 0, 5000) ?? 0;
            if (template is null)
            {
                continue;
            }

            if (!templates.Any(t => t.Id == template))
            {
                w.Error(JsonWalker.Child(at, "sysex"), $"refers to template \"{template}\", which is not defined or is invalid.");
                continue;
            }

            result.Add(new InitializationStep(template, TimeSpan.FromMilliseconds(delay)));
        }

        return result.ToImmutable();
    }

    private static ImmutableDictionary<string, JsonElement> ReadExtensions(JsonElement root, JsonWalker w)
    {
        if (!root.TryGetProperty("extensions", out var extensions))
        {
            return ImmutableDictionary<string, JsonElement>.Empty;
        }

        if (!w.ExpectObject(extensions, "$.extensions"))
        {
            return ImmutableDictionary<string, JsonElement>.Empty;
        }

        var result = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in extensions.EnumerateObject())
        {
            if (!property.Name.Contains('.', StringComparison.Ordinal))
            {
                w.Warn(JsonWalker.Child("$.extensions", property.Name), "extension keys should be namespaced, e.g. \"org.example.feature\".");
            }

            result[property.Name] = property.Value.Clone();
        }

        return result.ToImmutable();
    }

    private static string? ReadLocalId(JsonElement obj, string path, JsonWalker w)
    {
        var id = w.String(obj, "id", path, required: true, 64);
        if (id is not null && !LocalIdPattern().IsMatch(id))
        {
            w.Error(JsonWalker.Child(path, "id"), "must contain only lowercase letters, digits, '-', '_', or '.'.");
            return null;
        }

        return id;
    }

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex ProfileIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*$")]
    private static partial Regex LocalIdPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ParameterNamePattern();
}
