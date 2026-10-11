using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bluestone.Infrastructure.Projects;

/// <summary>Strict JSON accessors that throw <see cref="ProjectFormatException"/> with the failing path.</summary>
internal static class NodeReader
{
    public static JsonObject Object(JsonNode? node, string path) =>
        node as JsonObject ?? throw new ProjectFormatException(path, "must be an object.");

    public static JsonObject Object(JsonObject parent, string name, string path) =>
        Object(parent[name], $"{path}.{name}");

    public static JsonObject? OptionalObject(JsonObject parent, string name, string path) =>
        parent[name] is null ? null : Object(parent, name, path);

    public static JsonArray Array(JsonObject parent, string name, string path, int maxCount)
    {
        var at = $"{path}.{name}";
        var array = parent[name] as JsonArray ?? throw new ProjectFormatException(at, "must be an array.");
        return array.Count <= maxCount
            ? array
            : throw new ProjectFormatException(at, string.Create(CultureInfo.InvariantCulture, $"must have at most {maxCount} items."));
    }

    public static string String(JsonObject parent, string name, string path, int maxLength)
    {
        var at = $"{path}.{name}";
        if (parent[name] is not JsonValue value || !value.TryGetValue(out string? text))
        {
            throw new ProjectFormatException(at, "must be a string.");
        }

        return text.Length <= maxLength
            ? text
            : throw new ProjectFormatException(at, string.Create(CultureInfo.InvariantCulture, $"must be at most {maxLength} characters."));
    }

    public static string? OptionalString(JsonObject parent, string name, string path, int maxLength) =>
        parent[name] is null ? null : String(parent, name, path, maxLength);

    public static long Long(JsonObject parent, string name, string path, long min, long max)
    {
        var at = $"{path}.{name}";
        if (parent[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue(out long number))
        {
            throw new ProjectFormatException(at, "must be a whole number.");
        }

        return number >= min && number <= max
            ? number
            : throw new ProjectFormatException(at, string.Create(CultureInfo.InvariantCulture, $"must be between {min} and {max}."));
    }

    public static int Int(JsonObject parent, string name, string path, int min, int max) => (int)Long(parent, name, path, min, max);

    /// <summary>A whole number that is an array element rather than a named property.</summary>
    public static int IntValue(JsonNode? node, string path, int min, int max)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue(out long number))
        {
            throw new ProjectFormatException(path, "must be a whole number.");
        }

        return number >= min && number <= max
            ? (int)number
            : throw new ProjectFormatException(path, string.Create(CultureInfo.InvariantCulture, $"must be between {min} and {max}."));
    }

    public static double Double(JsonObject parent, string name, string path, double min, double max)
    {
        var at = $"{path}.{name}";
        if (parent[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number || !value.TryGetValue(out double number) || !double.IsFinite(number))
        {
            throw new ProjectFormatException(at, "must be a number.");
        }

        return number >= min && number <= max
            ? number
            : throw new ProjectFormatException(at, string.Create(CultureInfo.InvariantCulture, $"must be between {min} and {max}."));
    }

    public static int? OptionalInt(JsonObject parent, string name, string path, int min, int max) =>
        parent[name] is null ? null : Int(parent, name, path, min, max);

    public static bool Bool(JsonObject parent, string name, string path, bool defaultValue)
    {
        if (parent[name] is null)
        {
            return defaultValue;
        }

        return parent[name] is JsonValue value && value.TryGetValue(out bool result)
            ? result
            : throw new ProjectFormatException($"{path}.{name}", "must be true or false.");
    }

    public static Guid Guid(JsonObject parent, string name, string path) =>
        System.Guid.TryParseExact(String(parent, name, path, 36), "D", out var id)
            ? id
            : throw new ProjectFormatException($"{path}.{name}", "must be a GUID.");

    public static byte[] Hex(JsonObject parent, string name, string path, int maxBytes)
    {
        var at = $"{path}.{name}";
        var text = String(parent, name, path, (maxBytes * 3) + 1);
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new ProjectFormatException(at, "must be space-separated two-digit hex bytes.");
            }
        }

        return bytes;
    }

    public static string ToHex(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
}
