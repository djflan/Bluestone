using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluestone.Presentation;

/// <summary>Where a Device View's signal indicators (a junction's level and note lamp) sit, top to bottom of the panels.</summary>
public enum JunctionIndicatorPosition
{
    /// <summary>Flush with the top of the device, beside its header.</summary>
    Top,

    /// <summary>In the middle of the upper half of the device's body.</summary>
    UpperHalf,

    /// <summary>In the middle of the lower half of the device's body (the default).</summary>
    LowerHalf,

    /// <summary>At the bottom of the device.</summary>
    Bottom,
}

/// <summary>What a junction's level shows.</summary>
public enum JunctionLevelStyle
{
    /// <summary>Lit to the average (RMS) level, with the peak held as one line above it (the default).</summary>
    RmsAndPeak,

    /// <summary>A single bar lit to the peak level.</summary>
    Peak,
}

/// <summary>How Bluestone draws things that are a matter of taste, not of the project. One set for the whole application.</summary>
public sealed record DisplaySettings
{
    public JunctionIndicatorPosition IndicatorPosition { get; init; } = JunctionIndicatorPosition.LowerHalf;

    public JunctionLevelStyle LevelStyle { get; init; } = JunctionLevelStyle.RmsAndPeak;
}

/// <summary>Reads and writes <see cref="DisplaySettings"/> as a small JSON file. A missing or unreadable file gives the defaults.</summary>
public sealed class DisplaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new LenientEnum<JunctionIndicatorPosition>(), new LenientEnum<JunctionLevelStyle>() },
    };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public DisplaySettings Load()
    {
        try
        {
            var settings = File.Exists(Path) ? JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(Path), Options) : null;
            if (settings is null)
            {
                return new DisplaySettings();
            }

            // Each preference on its own: one unknown value does not reset the others.
            return new DisplaySettings
            {
                IndicatorPosition = Enum.IsDefined(settings.IndicatorPosition) ? settings.IndicatorPosition : JunctionIndicatorPosition.LowerHalf,
                LevelStyle = Enum.IsDefined(settings.LevelStyle) ? settings.LevelStyle : JunctionLevelStyle.RmsAndPeak,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DisplaySettings();
        }
    }

    // Reads an enum written by name (camelCase); anything else reads as an undefined value, which Load replaces with that
    // preference's default, so one unknown value (from a newer version, or hand-edited) does not reset the others.
    private sealed class LenientEnum<T> : JsonConverter<T>
        where T : struct, Enum
    {
        private static readonly T Unknown = (T)Enum.ToObject(typeof(T), -1);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                return !string.IsNullOrEmpty(text) && char.IsLetter(text[0]) && Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : Unknown;
            }

            reader.Skip();
            return Unknown;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }

    /// <summary>Writes atomically (a temporary file, then a rename).</summary>
    public void Save(DisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Path, overwrite: true);
    }
}

/// <summary>A choice of indicator position, with what the musician reads.</summary>
public sealed record IndicatorPositionOption(JunctionIndicatorPosition Position, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A choice of level style, with what the musician reads.</summary>
public sealed record LevelStyleOption(JunctionLevelStyle Style, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Settings ▸ Display: application-wide display preferences, saved as soon as they change.</summary>
public sealed partial class DisplayViewModel : ObservableObject
{
    private DisplaySettingsStore? _store;
    private bool _loading;

    public static IReadOnlyList<IndicatorPositionOption> Positions { get; } =
    [
        new(JunctionIndicatorPosition.Top, "Top of the device"),
        new(JunctionIndicatorPosition.UpperHalf, "Upper half"),
        new(JunctionIndicatorPosition.LowerHalf, "Lower half"),
        new(JunctionIndicatorPosition.Bottom, "Bottom of the device"),
    ];

    public static IReadOnlyList<LevelStyleOption> LevelStyles { get; } =
    [
        new(JunctionLevelStyle.RmsAndPeak, "Average level, with the peak held"),
        new(JunctionLevelStyle.Peak, "Peak level"),
    ];

    /// <summary>Where every Device View draws its junctions' levels and note lamps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorPositionChoice))]
    public partial JunctionIndicatorPosition IndicatorPosition { get; set; } = JunctionIndicatorPosition.LowerHalf;

    /// <summary><see cref="IndicatorPosition"/> as a list choice, for the Settings page.</summary>
    public IndicatorPositionOption IndicatorPositionChoice
    {
        get => Positions.First(p => p.Position == IndicatorPosition);
        set => IndicatorPosition = value?.Position ?? JunctionIndicatorPosition.LowerHalf;
    }

    /// <summary>What every Device View's junction levels show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelStyleChoice))]
    public partial JunctionLevelStyle LevelStyle { get; set; } = JunctionLevelStyle.RmsAndPeak;

    /// <summary><see cref="LevelStyle"/> as a list choice, for the Settings page.</summary>
    public LevelStyleOption LevelStyleChoice
    {
        get => LevelStyles.First(s => s.Style == LevelStyle);
        set => LevelStyle = value?.Style ?? JunctionLevelStyle.RmsAndPeak;
    }

    /// <summary>Reads the saved settings from <paramref name="store"/> and saves every later change there.</summary>
    public void Use(DisplaySettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loading = true;
        try
        {
            var settings = store.Load();
            IndicatorPosition = settings.IndicatorPosition;
            LevelStyle = settings.LevelStyle;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIndicatorPositionChanged(JunctionIndicatorPosition value) => Save();

    partial void OnLevelStyleChanged(JunctionLevelStyle value) => Save();

    private void Save()
    {
        if (!_loading && _store is { } store)
        {
            try
            {
                store.Save(new DisplaySettings { IndicatorPosition = IndicatorPosition, LevelStyle = LevelStyle });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A preference that could not be saved still applies for this session.
            }
        }
    }
}
