using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bluestone.Presentation;

/// <summary>What <see cref="ThemeStore.Load"/> found: the theme to use, and what went wrong reading it, if anything.</summary>
/// <param name="Theme">The theme: the file's values over the shipped defaults.</param>
/// <param name="Problem">Why the file could not be used, for the musician, or null.</param>
public sealed record ThemeLoadResult(ThemeDefinition Theme, string? Problem);

/// <summary>
/// Reads and writes one theme as a JSON file (ADR 0053), over its shipped <see cref="Defaults"/>. Every member is read on its
/// own: an unknown key is ignored and a missing or invalid value takes its default, so a hand edit gone wrong loses only that
/// value. A missing file is written with the defaults, so it can be edited by hand. A file that cannot be read at all is kept
/// aside (renamed, never overwritten) and the defaults are used.
/// </summary>
public sealed class ThemeStore
{
    /// <summary>The file format's version.</summary>
    public const int Version = 1;

    public ThemeStore(string directory, ThemeDefinition defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        Path = System.IO.Path.Combine(directory, ThemeDefinition.FileName(defaults.Name));
    }

    /// <summary>The theme as it ships: what reverting a member, or resetting the theme, puts back.</summary>
    public ThemeDefinition Defaults { get; }

    /// <summary>The theme's file.</summary>
    public string Path { get; }

    /// <summary>Reads the theme. Never throws for a missing, unreadable, or invalid file.</summary>
    public ThemeLoadResult Load()
    {
        string text;
        try
        {
            if (!File.Exists(Path))
            {
                return new ThemeLoadResult(Defaults, TrySave(Defaults));
            }

            text = File.ReadAllText(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ThemeLoadResult(Defaults, $"The {Defaults.Name} theme file couldn't be read ({ex.Message}), so its shipped colors are used.");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return new ThemeLoadResult(Read(document.RootElement), null);
            }
        }
        catch (JsonException)
        {
        }

        return new ThemeLoadResult(Defaults, KeepAside());
    }

    /// <summary>Writes <paramref name="theme"/> atomically (a temporary file, then a rename).</summary>
    public void Save(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, Write(theme));
        File.Move(temporary, Path, overwrite: true);
    }

    private ThemeDefinition Read(JsonElement root)
    {
        var colors = Defaults.Colors;
        if (root.TryGetProperty("colors", out var saved) && saved.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in saved.EnumerateObject())
            {
                if (Defaults.Colors.ContainsKey(property.Name)
                    && property.Value.ValueKind == JsonValueKind.String
                    && ThemeDefinition.IsColor(property.Value.GetString()))
                {
                    colors = colors.SetItem(property.Name, property.Value.GetString()!.ToUpperInvariant());
                }
            }
        }

        var gradients = root.TryGetProperty("gradients", out var g) && g.ValueKind == JsonValueKind.Object ? g : default;
        return Defaults with
        {
            Variant = root.TryGetProperty("variant", out var variant) && variant.ValueKind == JsonValueKind.String
                && Enum.TryParse<ThemeVariant>(variant.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                && char.IsLetter(variant.GetString()![0])
                    ? parsed
                    : Defaults.Variant,
            Colors = colors,
            MixerGradient = Gradient(gradients, "mixer") ?? Defaults.MixerGradient,
            ChainGradient = Gradient(gradients, "chain") ?? Defaults.ChainGradient,
            GainGradient = Gradient(gradients, "gain") ?? Defaults.GainGradient,
            MidiColor = root.TryGetProperty("midiColor", out var midi) && midi.ValueKind == JsonValueKind.String && SignalGradient.IsColour(midi.GetString())
                ? midi.GetString()!.ToUpperInvariant()
                : Defaults.MidiColor,
        };
    }

    private static SignalGradient? Gradient(JsonElement gradients, string name)
    {
        if (gradients.ValueKind != JsonValueKind.Object || !gradients.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var stops = new List<SignalGradientStop>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.String
                || !element.TryGetProperty("position", out var position) || !position.TryGetDouble(out var value))
            {
                return null;
            }

            stops.Add(new SignalGradientStop(color.GetString()!, value));
        }

        return SignalGradient.TryCreate(stops, out var gradient) ? gradient : null;
    }

    private static byte[] Write(ThemeDefinition theme)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", theme.Name);
            writer.WriteString("variant", theme.Variant.ToString().ToLowerInvariant());
            writer.WriteNumber("version", Version);
            writer.WriteStartObject("gradients");
            WriteGradient(writer, "mixer", theme.MixerGradient);
            WriteGradient(writer, "chain", theme.ChainGradient);
            WriteGradient(writer, "gain", theme.GainGradient);
            writer.WriteEndObject();
            writer.WriteString("midiColor", theme.MidiColor);
            writer.WriteStartObject("colors");
            foreach (var (key, value) in theme.Colors)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteGradient(Utf8JsonWriter writer, string name, SignalGradient gradient)
    {
        writer.WriteStartArray(name);
        foreach (var stop in gradient.Stops)
        {
            writer.WriteStartObject();
            writer.WriteString("color", stop.Colour);
            writer.WriteNumber("position", stop.Position);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    // First run: the file is written so it can be edited by hand. Failing to write it is reported, not fatal.
    private string? TrySave(ThemeDefinition theme)
    {
        try
        {
            Save(theme);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"The {theme.Name} theme file couldn't be created ({ex.Message}); color changes won't be kept.";
        }
    }

    // An unreadable file is renamed out of the way, so a later save never overwrites what the musician wrote.
    private string KeepAside()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var aside = $"{System.IO.Path.ChangeExtension(Path, null)}.unreadable-{stamp}.json";
        var message = new StringBuilder($"The {Defaults.Name} theme file couldn't be read, so its shipped colors are used.");
        try
        {
            File.Move(Path, aside, overwrite: false);
            message.Append(CultureInfo.InvariantCulture, $" It was kept as {System.IO.Path.GetFileName(aside)}.");
            var created = TrySave(Defaults);
            if (created is not null)
            {
                message.Append(' ').Append(created);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            message.Append(CultureInfo.InvariantCulture, $" It couldn't be moved aside ({ex.Message}), so color changes won't be saved over it.");
        }

        return message.ToString();
    }
}
