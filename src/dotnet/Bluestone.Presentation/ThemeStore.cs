using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bluestone.Presentation;

/// <summary>What <see cref="ThemeStore.Load"/> found: the theme to use, and what went wrong reading it, if anything.</summary>
/// <param name="Theme">The theme: the file's values over the shipped defaults.</param>
/// <param name="Problem">What the musician should know about the file (values it couldn't use, or why it wasn't read), or null.</param>
public sealed record ThemeLoadResult(ThemeDefinition Theme, string? Problem);

/// <summary>
/// Reads and writes one theme as a JSON file (ADR 0053), over its shipped <see cref="Defaults"/>. The file holds only what
/// differs from the shipped theme, so a later Bluestone's improved colors reach every member nobody changed; the shipped
/// values are written beside it (<see cref="ReferencePath"/>) as a reference to copy from. Every member is read on its own:
/// a missing value takes its default, and a value that can't be used takes its default and is reported. Comments and
/// trailing commas are allowed. A file that can't be parsed is kept aside (renamed) and the defaults are used; a file that
/// can't be read, or kept aside, is never written over (<see cref="CanSave"/>).
/// </summary>
public sealed class ThemeStore
{
    /// <summary>The file format's version.</summary>
    public const int Version = 1;

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public ThemeStore(string directory, ThemeDefinition defaults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        Path = System.IO.Path.Combine(directory, ThemeDefinition.FileName(defaults.Name));
        ReferencePath = System.IO.Path.ChangeExtension(Path, ".shipped.json");
    }

    /// <summary>The theme as it ships: what reverting a member, or resetting the theme, puts back.</summary>
    public ThemeDefinition Defaults { get; }

    /// <summary>The theme's file: the musician's changes.</summary>
    public string Path { get; }

    /// <summary>Every member's shipped value, rewritten when it changes; Bluestone never reads it.</summary>
    public string ReferencePath { get; }

    /// <summary>
    /// False once <see cref="Load"/> found a file it could neither read nor move aside: <see cref="Save"/> then refuses, so the
    /// musician's file is never replaced by the defaults.
    /// </summary>
    public bool CanSave { get; private set; } = true;

    /// <summary>Reads the theme. Never throws.</summary>
    public ThemeLoadResult Load()
    {
        CanSave = true;
        WriteReference();
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
            CanSave = false;
            return new ThemeLoadResult(Defaults, $"The {Defaults.Name} theme file couldn't be read ({ex.Message}), so its shipped colors are used until Bluestone starts again. Color changes won't be saved over it.");
        }

        try
        {
            using var document = JsonDocument.Parse(text, Lenient);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var rejected = new List<string>();
                var theme = Read(document.RootElement, rejected);
                return new ThemeLoadResult(theme, rejected.Count == 0
                    ? null
                    : $"{rejected.Count} value{(rejected.Count == 1 ? string.Empty : "s")} in {System.IO.Path.GetFileName(Path)} couldn't be used, so {(rejected.Count == 1 ? "it uses its" : "they use their")} shipped color and {(rejected.Count == 1 ? "is" : "are")} removed from the file the next time a theme change is saved: {string.Join(", ", rejected)}.");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // Not JSON, or JSON that can't be decoded (an unpaired surrogate escape, for example).
        }

        return new ThemeLoadResult(Defaults, KeepAside());
    }

    /// <summary>Writes what <paramref name="theme"/> changes from <see cref="Defaults"/>, atomically (a temporary file, then a rename).</summary>
    /// <exception cref="IOException">The file couldn't be written, or <see cref="CanSave"/> is false.</exception>
    public void Save(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!CanSave)
        {
            throw new IOException($"{System.IO.Path.GetFileName(Path)} couldn't be read when Bluestone started, so it is left as it is.");
        }

        WriteFile(Path, Write(theme, Defaults));
    }

    private static void WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private ThemeDefinition Read(JsonElement root, List<string> rejected)
    {
        var colors = Defaults.Colors;
        if (root.TryGetProperty("colors", out var saved))
        {
            if (saved.ValueKind != JsonValueKind.Object)
            {
                rejected.Add("colors");
            }
            else
            {
                foreach (var property in saved.EnumerateObject())
                {
                    if (Defaults.Colors.ContainsKey(property.Name)
                        && property.Value.ValueKind == JsonValueKind.String
                        && ThemeDefinition.IsColor(property.Value.GetString()))
                    {
                        colors = colors.SetItem(property.Name, property.Value.GetString()!.ToUpperInvariant());
                    }
                    else
                    {
                        rejected.Add(property.Name);
                    }
                }
            }
        }

        var gradients = default(JsonElement);
        if (root.TryGetProperty("gradients", out var g))
        {
            if (g.ValueKind == JsonValueKind.Object)
            {
                gradients = g;
            }
            else
            {
                rejected.Add("gradients");
            }
        }
        var variant = Defaults.Variant;
        if (root.TryGetProperty("variant", out var v))
        {
            var text = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (text is { Length: > 0 } && char.IsLetter(text[0]) && Enum.TryParse<ThemeVariant>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                variant = parsed;
            }
            else
            {
                rejected.Add("variant");
            }
        }

        var midi = Defaults.MidiColor;
        if (root.TryGetProperty("midiColor", out var m))
        {
            if (m.ValueKind == JsonValueKind.String && SignalGradient.IsColour(m.GetString()))
            {
                midi = m.GetString()!.ToUpperInvariant();
            }
            else
            {
                rejected.Add("midiColor");
            }
        }

        return Defaults with
        {
            Variant = variant,
            Colors = colors,
            MixerGradient = Gradient(gradients, "mixer", rejected) ?? Defaults.MixerGradient,
            ChainGradient = Gradient(gradients, "chain", rejected) ?? Defaults.ChainGradient,
            GainGradient = Gradient(gradients, "gain", rejected) ?? Defaults.GainGradient,
            MidiColor = midi,
        };
    }

    private static SignalGradient? Gradient(JsonElement gradients, string name, List<string> rejected)
    {
        if (gradients.ValueKind != JsonValueKind.Object || !gradients.TryGetProperty(name, out var array))
        {
            return null;
        }

        var stops = new List<SignalGradientStop>();
        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty("color", out var color) || color.ValueKind != JsonValueKind.String
                    || !element.TryGetProperty("position", out var position) || position.ValueKind != JsonValueKind.Number || !position.TryGetDouble(out var value))
                {
                    stops = null;
                    break;
                }

                stops.Add(new SignalGradientStop(color.GetString()!, value));
            }

            if (stops is not null && SignalGradient.TryCreate(stops, out var gradient))
            {
                return gradient;
            }
        }

        rejected.Add($"gradients.{name}");
        return null;
    }

    // Only what differs from the shipped theme is written (with the name, variant, and version), so members the musician
    // never changed follow the shipped theme when a later Bluestone improves it.
    private static byte[] Write(ThemeDefinition theme, ThemeDefinition? shipped)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", theme.Name);
            writer.WriteString("variant", theme.Variant.ToString().ToLowerInvariant());
            writer.WriteNumber("version", Version);
            writer.WriteString("about", shipped is null
                ? $"{theme.Name} as it ships. Bluestone rewrites this file and never reads it: copy a value into {ThemeDefinition.FileName(theme.Name)} to change it."
                : $"Your changes to {theme.Name}. A color or gradient that isn't listed uses its shipped value ({System.IO.Path.ChangeExtension(ThemeDefinition.FileName(theme.Name), ".shipped.json")} lists every one). Colors apply when Bluestone starts.");
            writer.WriteStartObject("gradients");
            WriteGradient(writer, "mixer", theme.MixerGradient, shipped?.MixerGradient);
            WriteGradient(writer, "chain", theme.ChainGradient, shipped?.ChainGradient);
            WriteGradient(writer, "gain", theme.GainGradient, shipped?.GainGradient);
            writer.WriteEndObject();
            if (shipped is null || theme.MidiColor != shipped.MidiColor)
            {
                writer.WriteString("midiColor", theme.MidiColor);
            }

            writer.WriteStartObject("colors");
            foreach (var (key, value) in theme.Colors)
            {
                if (shipped is null || !shipped.Colors.TryGetValue(key, out var original) || original != value)
                {
                    writer.WriteString(key, value);
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteGradient(Utf8JsonWriter writer, string name, SignalGradient gradient, SignalGradient? shipped)
    {
        if (gradient == shipped)
        {
            return;
        }

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

    // The reference copy is best effort: it helps hand editing and is never read, so failing to write it isn't reported.
    private void WriteReference()
    {
        try
        {
            var bytes = Write(Defaults, shipped: null);
            if (!File.Exists(ReferencePath) || !File.ReadAllBytes(ReferencePath).AsSpan().SequenceEqual(bytes))
            {
                WriteFile(ReferencePath, bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
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

    // A file that can't be parsed is renamed out of the way, so a later save never overwrites what the musician wrote. If it
    // can't be moved, nothing is ever saved over it.
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
            CanSave = false;
            message.Append(CultureInfo.InvariantCulture, $" It couldn't be moved aside ({ex.Message}), so color changes won't be saved over it.");
        }

        return message.ToString();
    }
}
