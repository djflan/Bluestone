using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Profiles;

/// <summary>Reads a profile's parameter map: named parameters and the MIDI that sets each (ADR 0031).</summary>
public static partial class ProfileLoader
{
    public const int MaxParameterMappings = 512;

    private static readonly Dictionary<string, ParameterMappingKind> MappingKinds = new(StringComparer.Ordinal)
    {
        ["cc"] = ParameterMappingKind.Controller,
        ["rpn"] = ParameterMappingKind.Rpn,
        ["nrpn"] = ParameterMappingKind.Nrpn,
        ["sysex"] = ParameterMappingKind.SystemExclusive,
    };

    private static readonly Dictionary<string, ChecksumConvention> Conventions = new(StringComparer.Ordinal)
    {
        ["roland"] = ChecksumConvention.Roland,
        ["yamaha"] = ChecksumConvention.Yamaha,
    };

    private static readonly string[] CommonMappingProperties = ["id", "name", "kind", "min", "max", "default"];

    private static readonly Dictionary<ParameterMappingKind, string[]> MappingProperties = new()
    {
        [ParameterMappingKind.Controller] = ["bits", "controller"],
        [ParameterMappingKind.Rpn] = ["bits", "msb", "lsb", "nullAfter"],
        [ParameterMappingKind.Nrpn] = ["bits", "msb", "lsb", "nullAfter"],
        [ParameterMappingKind.SystemExclusive] = ["bytes", "device", "checksum"],
    };

    private static ImmutableArray<ParameterMapping> ReadParameterMap(JsonElement root, JsonWalker w)
    {
        var result = ImmutableArray.CreateBuilder<ParameterMapping>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, at) in w.Array(root, "parameterMap", "$", required: false, MaxParameterMappings))
        {
            if (!w.ExpectObject(item, at))
            {
                continue;
            }

            var id = ReadLocalId(item, at, w);
            var name = w.String(item, "name", at, required: true, 64);
            var kind = w.Enum(item, "kind", at, required: true, MappingKinds);
            if (kind is { } known)
            {
                WarnUnusedMappingProperties(item, at, w, known);
            }

            if (id is not null && !ids.Add(id))
            {
                w.Error(JsonWalker.Child(at, "id"), $"parameter \"{id}\" is defined more than once.");
                continue;
            }

            if (id is null || name is null || kind is not { } k)
            {
                continue;
            }

            var mapping = k switch
            {
                ParameterMappingKind.Controller => ReadControllerMapping(item, at, w, id, name),
                ParameterMappingKind.Rpn or ParameterMappingKind.Nrpn => ReadParameterNumberMapping(item, at, w, id, name, k),
                _ => ReadSysExMapping(item, at, w, id, name),
            };
            if (mapping is not null)
            {
                result.Add(mapping);
            }
        }

        return result.ToImmutable();
    }

    private static void WarnUnusedMappingProperties(JsonElement item, string at, JsonWalker w, ParameterMappingKind kind)
    {
        var allowed = MappingProperties[kind];
        foreach (var property in item.EnumerateObject())
        {
            if (CommonMappingProperties.Contains(property.Name, StringComparer.Ordinal) || allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var usedElsewhere = MappingProperties.Values.Any(names => names.Contains(property.Name, StringComparer.Ordinal));
            w.Warn(JsonWalker.Child(at, property.Name), usedElsewhere
                ? $"is not used by this kind of parameter and was ignored."
                : "is not part of the profile schema and was ignored. Put custom data under \"extensions\".");
        }
    }

    private static int? ReadBits(JsonElement item, string at, JsonWalker w)
    {
        var bits = w.Int(item, "bits", at, required: false, 7, 14) ?? 7;
        if (bits is not (7 or 14))
        {
            w.Error(JsonWalker.Child(at, "bits"), string.Create(CultureInfo.InvariantCulture, $"must be 7 or 14 (found {bits})."));
            return null;
        }

        return bits;
    }

    /// <summary>Reads <c>min</c>, <c>max</c>, and <c>default</c>; <c>max</c> defaults to <paramref name="ceiling"/>.</summary>
    private static (int Min, int Max, int? Default)? ReadRange(JsonElement item, string at, JsonWalker w, int ceiling, int defaultMax)
    {
        var min = w.Int(item, "min", at, required: false, 0, ceiling) ?? 0;
        var max = w.Int(item, "max", at, required: false, 0, ceiling) ?? defaultMax;
        var defaultValue = w.Int(item, "default", at, required: false, 0, ceiling);
        if (min >= max)
        {
            w.Error(at, string.Create(CultureInfo.InvariantCulture, $"min must be less than max (found {min} and {max})."));
            return null;
        }

        if (defaultValue is { } d && (d < min || d > max))
        {
            w.Error(JsonWalker.Child(at, "default"), "must be between min and max.");
            return null;
        }

        return (min, max, defaultValue);
    }

    private static ParameterMapping? ReadControllerMapping(JsonElement item, string at, JsonWalker w, string id, string name)
    {
        var bits = ReadBits(item, at, w);
        var controller = w.Int(item, "controller", at, required: true, 0, 127);
        if (bits is not { } b || controller is not { } number)
        {
            return null;
        }

        if (b == 14 && number > 31)
        {
            w.Error(JsonWalker.Child(at, "controller"), "a 14-bit controller is its MSB controller, from 0 to 31; the LSB is that number plus 32.");
            return null;
        }

        if (!AutomationTarget.IsAutomatable(new ControllerNumber(number)))
        {
            w.Error(JsonWalker.Child(at, "controller"), string.Create(CultureInfo.InvariantCulture, $"controller {number} cannot be automated: bank select, LSB controllers (32-63), data entry and increment, RPN/NRPN selectors, and channel mode messages are refused."));
            return null;
        }

        var ceiling = (1 << b) - 1;
        return ReadRange(item, at, w, ceiling, ceiling) is not { } range
            ? null
            : new ParameterMapping(id, name, ParameterMappingKind.Controller, b, range.Min, range.Max, range.Default) { Controller = new ControllerNumber(number) };
    }

    private static ParameterMapping? ReadParameterNumberMapping(JsonElement item, string at, JsonWalker w, string id, string name, ParameterMappingKind kind)
    {
        var bits = ReadBits(item, at, w);
        var msb = w.Int(item, "msb", at, required: true, 0, 127);
        var lsb = w.Int(item, "lsb", at, required: true, 0, 127);
        var nullAfter = w.Bool(item, "nullAfter", at, required: false) ?? true;
        if (bits is not { } b || msb is not { } m || lsb is not { } l)
        {
            return null;
        }

        if (m == ParameterMapping.NullParameter && l == ParameterMapping.NullParameter)
        {
            w.Error(at, "parameter 127/127 is the null parameter, which selects nothing.");
            return null;
        }

        var ceiling = (1 << b) - 1;
        return ReadRange(item, at, w, ceiling, ceiling) is not { } range
            ? null
            : new ParameterMapping(id, name, kind, b, range.Min, range.Max, range.Default)
            {
                ParameterMsb = new SevenBitValue(m),
                ParameterLsb = new SevenBitValue(l),
                NullAfter = nullAfter,
            };
    }

    private static ParameterMapping? ReadSysExMapping(JsonElement item, string at, JsonWalker w, string id, string name)
    {
        var device = w.Int(item, "device", at, required: false, 0, 127);
        var checksum = ReadChecksum(item, at, w);
        var range = ReadRange(item, at, w, ParameterMapping.MaxSysExValue, 127);
        var text = w.String(item, "bytes", at, required: true, SysExTemplate.MaxTokens * 16);
        if (text is null || range is not { } r)
        {
            return null;
        }

        var bytesAt = JsonWalker.Child(at, "bytes");
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts.Length > SysExTemplate.MaxTokens)
        {
            w.Error(bytesAt, string.Create(CultureInfo.InvariantCulture, $"must contain 3-{SysExTemplate.MaxTokens} bytes."));
            return null;
        }

        var tokens = ImmutableArray.CreateBuilder<MappingToken>(parts.Length);
        var valid = true;
        var covered = 0;
        var checksumAt = -1;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var inside = i > 0 && i < parts.Length - 1;
            if (part.StartsWith('{'))
            {
                if (!inside)
                {
                    w.Error(bytesAt, "must start with F0 and end with F7; placeholders can only appear in between.");
                    valid = false;
                    continue;
                }

                if (ParsePlaceholder(part, i, bytesAt, w, device, checksum) is not { } token)
                {
                    valid = false;
                    continue;
                }

                if (token.Kind == MappingTokenKind.Value)
                {
                    covered |= ((1 << token.Width) - 1) << token.Shift;
                }
                else if (token.Kind == MappingTokenKind.Checksum)
                {
                    if (checksumAt >= 0)
                    {
                        w.Error(bytesAt, "uses {checksum} more than once.");
                        valid = false;
                    }

                    checksumAt = i;
                }

                tokens.Add(token);
                continue;
            }

            if (!JsonWalker.TryParseHexByte(part, out var literal))
            {
                w.Error(bytesAt, $"token {i + 1} (\"{part}\") is neither a hex byte nor a placeholder.");
                valid = false;
                continue;
            }

            if ((i == 0 && literal != SysExMessage.Start) || (i == parts.Length - 1 && literal != SysExMessage.End))
            {
                w.Error(bytesAt, "must start with F0 and end with F7.");
                valid = false;
            }
            else if (inside && literal > 0x7F)
            {
                w.Error(bytesAt, $"token {i + 1} ({part}) is not a data byte; bytes between F0 and F7 must be 00-7F.");
                valid = false;
            }

            tokens.Add(new MappingToken(MappingTokenKind.Literal, literal));
        }

        if (!valid)
        {
            return null;
        }

        var bits = 32 - BitOperations.LeadingZeroCount((uint)r.Max);
        var needed = (1 << bits) - 1;
        if (covered == 0)
        {
            w.Error(bytesAt, "must contain {value} or {value[high:low]} placeholders.");
            return null;
        }

        if ((covered & needed) != needed)
        {
            w.Error(bytesAt, string.Create(CultureInfo.InvariantCulture, $"does not send every bit of the value: max {r.Max} needs bits {bits - 1} to 0."));
            return null;
        }

        if (checksum is { } c)
        {
            if (checksumAt < 0)
            {
                w.Error(bytesAt, "declares a checksum but does not contain {checksum}.");
                return null;
            }

            if (c.Start < 1 || c.Start >= checksumAt)
            {
                w.Error(JsonWalker.Child(JsonWalker.Child(at, "checksum"), "start"), string.Create(CultureInfo.InvariantCulture, $"must be the position of a byte after F0 and before {{checksum}} (1 to {checksumAt - 1}; F0 is 0)."));
                return null;
            }

            var manufacturer = tokens[1] is { Kind: MappingTokenKind.Literal } first ? first.Literal : (byte)0xFF;
            var expected = c.Convention == ChecksumConvention.Roland ? GsManufacturer : XgManufacturer;
            if (manufacturer != expected)
            {
                w.Warn(JsonWalker.Child(JsonWalker.Child(at, "checksum"), "convention"), string.Create(CultureInfo.InvariantCulture, $"is usually used with manufacturer ID {expected:X2}, but the template starts F0 {manufacturer:X2}."));
            }
        }

        return new ParameterMapping(id, name, ParameterMappingKind.SystemExclusive, bits, r.Min, r.Max, r.Default)
        {
            Tokens = tokens.ToImmutable(),
            DeviceNumber = new SevenBitValue(device ?? 0),
            Checksum = checksum?.Convention,
            ChecksumStart = checksum?.Start ?? 0,
        };
    }

    private const byte GsManufacturer = 0x41;
    private const byte XgManufacturer = 0x43;

    private static (ChecksumConvention Convention, int Start)? ReadChecksum(JsonElement item, string at, JsonWalker w)
    {
        if (!item.TryGetProperty("checksum", out var checksum))
        {
            return null;
        }

        var path = JsonWalker.Child(at, "checksum");
        if (!w.ExpectObject(checksum, path))
        {
            return null;
        }

        w.RejectUnknown(checksum, path, "convention", "start");
        var convention = w.Enum(checksum, "convention", path, required: true, Conventions);
        var start = w.Int(checksum, "start", path, required: true, 1, SysExTemplate.MaxTokens - 2);
        return convention is { } c && start is { } s ? (c, s) : null;
    }

    private static MappingToken? ParsePlaceholder(string part, int index, string path, JsonWalker w, int? device, (ChecksumConvention, int)? checksum)
    {
        switch (part)
        {
            case "{value}":
                return new MappingToken(MappingTokenKind.Value, Shift: 0, Width: 7);
            case "{channel}":
                return new MappingToken(MappingTokenKind.Channel);
            case "{device}" when device is null:
                w.Error(path, "uses {device}, so the parameter must give its device number byte as \"device\".");
                return null;
            case "{device}":
                return new MappingToken(MappingTokenKind.Device);
            case "{checksum}" when checksum is null:
                w.Error(path, "uses {checksum}, so the parameter must declare \"checksum\" with its convention and start.");
                return null;
            case "{checksum}":
                return new MappingToken(MappingTokenKind.Checksum);
        }

        var slice = ValueSlicePattern().Match(part);
        if (!slice.Success)
        {
            w.Error(path, string.Create(CultureInfo.InvariantCulture, $"token {index + 1} (\"{part}\") is not a known placeholder: use {{value}}, {{value[high:low]}}, {{channel}}, {{device}}, or {{checksum}}."));
            return null;
        }

        var high = int.Parse(slice.Groups[1].Value, CultureInfo.InvariantCulture);
        var low = int.Parse(slice.Groups[2].Value, CultureInfo.InvariantCulture);
        if (high < low || high > 15 || high - low >= 7)
        {
            w.Error(path, string.Create(CultureInfo.InvariantCulture, $"token {index + 1} (\"{part}\") must select 1 to 7 bits, high bit first, within bits 15 to 0."));
            return null;
        }

        return new MappingToken(MappingTokenKind.Value, Shift: low, Width: high - low + 1);
    }

    [GeneratedRegex(@"^\{value\[(\d{1,2}):(\d{1,2})\]\}$")]
    private static partial Regex ValueSlicePattern();
}
