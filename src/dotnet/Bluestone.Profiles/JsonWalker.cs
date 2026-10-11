using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Bluestone.Profiles;

/// <summary>Reads JSON values while collecting path-qualified diagnostics instead of throwing.</summary>
internal sealed class JsonWalker
{
    private readonly List<ProfileDiagnostic> _diagnostics = [];

    public IReadOnlyList<ProfileDiagnostic> Diagnostics => _diagnostics;

    public bool HasErrors => _diagnostics.Any(d => d.Severity == ProfileDiagnosticSeverity.Error);

    public void Error(string path, string message) => _diagnostics.Add(new ProfileDiagnostic(ProfileDiagnosticSeverity.Error, path, message));

    public void Warn(string path, string message) => _diagnostics.Add(new ProfileDiagnostic(ProfileDiagnosticSeverity.Warning, path, message));

    public static string Child(string path, string property) => $"{path}.{property}";

    public static string Index(string path, int index) => string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]");

    public bool ExpectObject(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        Error(path, $"must be an object (found {Describe(element)}).");
        return false;
    }

    /// <summary>Warns about properties the schema does not define.</summary>
    public void RejectUnknown(JsonElement obj, string path, params string[] allowed)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                Warn(Child(path, property.Name), "is not part of the profile schema and was ignored. Put custom data under \"extensions\".");
            }
        }
    }

    public string? String(JsonElement obj, string name, string path, bool required, int maxLength = 256)
    {
        if (!TryGet(obj, name, path, required, out var value, out var at))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            Error(at, $"must be a string (found {Describe(value)}).");
            return null;
        }

        var text = value.GetString()!;
        if (required && string.IsNullOrWhiteSpace(text))
        {
            Error(at, "must not be empty.");
            return null;
        }

        if (text.Length > maxLength)
        {
            Error(at, string.Create(CultureInfo.InvariantCulture, $"must be at most {maxLength} characters (found {text.Length})."));
            return null;
        }

        if (text.Any(char.IsControl))
        {
            Error(at, "must not contain control characters.");
            return null;
        }

        return text;
    }

    public int? Int(JsonElement obj, string name, string path, bool required, int min, int max)
    {
        if (!TryGet(obj, name, path, required, out var value, out var at))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            Error(at, $"must be a whole number (found {Describe(value)}).");
            return null;
        }

        if (number < min || number > max)
        {
            Error(at, string.Create(CultureInfo.InvariantCulture, $"must be between {min} and {max} (found {number})."));
            return null;
        }

        return number;
    }

    public bool? Bool(JsonElement obj, string name, string path, bool required)
    {
        if (!TryGet(obj, name, path, required, out var value, out var at))
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Error(at, $"must be true or false (found {Describe(value)}).");
            return null;
        }

        return value.GetBoolean();
    }

    public T? Enum<T>(JsonElement obj, string name, string path, bool required, IReadOnlyDictionary<string, T> values)
        where T : struct
    {
        var text = String(obj, name, path, required, 64);
        if (text is null)
        {
            return null;
        }

        if (values.TryGetValue(text, out var result))
        {
            return result;
        }

        Error(Child(path, name), $"must be one of {string.Join(", ", values.Keys.Select(k => $"\"{k}\""))} (found \"{text}\").");
        return null;
    }

    /// <summary>Yields each item with its path; reports a missing required array or one that is too long.</summary>
    public IEnumerable<(JsonElement Item, string Path)> Array(JsonElement obj, string name, string path, bool required, int maxCount)
    {
        if (!TryGet(obj, name, path, required, out var value, out var at))
        {
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            Error(at, $"must be an array (found {Describe(value)}).");
            yield break;
        }

        var count = value.GetArrayLength();
        if (count > maxCount)
        {
            Error(at, string.Create(CultureInfo.InvariantCulture, $"must have at most {maxCount} items (found {count})."));
            yield break;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            yield return (item, Index(at, index++));
        }
    }

    public ImmutableArray<string> Strings(JsonElement obj, string name, string path, bool required, int maxCount, int maxLength, bool nonEmpty = false)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        var seen = false;
        foreach (var (item, at) in Array(obj, name, path, required, maxCount))
        {
            seen = true;
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                Error(at, "must be a non-empty string.");
                continue;
            }

            var text = item.GetString()!;
            if (text.Length > maxLength || text.Any(char.IsControl))
            {
                Error(at, string.Create(CultureInfo.InvariantCulture, $"must be at most {maxLength} characters with no control characters."));
                continue;
            }

            result.Add(text);
        }

        if (nonEmpty && result.Count == 0 && (seen || obj.TryGetProperty(name, out _)))
        {
            Error(Child(path, name), "must contain at least one item.");
        }

        return result.ToImmutable();
    }

    /// <summary>Parses space-separated hex data bytes such as <c>"00 20 29"</c>.</summary>
    public ImmutableArray<byte>? DataBytes(JsonElement obj, string name, string path, bool required, int minCount, int maxCount)
    {
        var text = String(obj, name, path, required, 64);
        if (text is null)
        {
            return null;
        }

        var at = Child(path, name);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < minCount || parts.Length > maxCount)
        {
            Error(at, string.Create(CultureInfo.InvariantCulture, $"must contain {minCount}-{maxCount} hex bytes (found {parts.Length})."));
            return null;
        }

        var bytes = ImmutableArray.CreateBuilder<byte>(parts.Length);
        foreach (var part in parts)
        {
            if (!TryParseHexByte(part, out var b) || b > 0x7F)
            {
                Error(at, $"\"{part}\" is not a hex data byte (00-7F).");
                return null;
            }

            bytes.Add(b);
        }

        return bytes.ToImmutable();
    }

    public static bool TryParseHexByte(string text, out byte value) =>
        byte.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && text.Length == 2;

    private bool TryGet(JsonElement obj, string name, string path, bool required, out JsonElement value, out string at)
    {
        at = Child(path, name);
        if (obj.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        if (required)
        {
            Error(at, "is required.");
        }

        return false;
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };
}
