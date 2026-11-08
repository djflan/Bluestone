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

/// <summary>How Bluestone draws things that are a matter of taste, not of the project. One set for the whole application.</summary>
public sealed record DisplaySettings
{
    public JunctionIndicatorPosition IndicatorPosition { get; init; } = JunctionIndicatorPosition.LowerHalf;
}

/// <summary>Reads and writes <see cref="DisplaySettings"/> as a small JSON file. A missing or unreadable file gives the defaults.</summary>
public sealed class DisplaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public DisplaySettings Load()
    {
        try
        {
            var settings = File.Exists(Path) ? JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(Path), Options) : null;
            return settings is not null && Enum.IsDefined(settings.IndicatorPosition) ? settings : new DisplaySettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DisplaySettings();
        }
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

    /// <summary>Where every Device View draws its junctions' levels and note lamps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndicatorPositionChoice))]
    public partial JunctionIndicatorPosition IndicatorPosition { get; set; } = JunctionIndicatorPosition.LowerHalf;

    /// <summary><see cref="IndicatorPosition"/> as a list choice, for the Settings page.</summary>
    public IndicatorPositionOption IndicatorPositionChoice
    {
        get => Positions.First(p => p.Position == IndicatorPosition);
        set => IndicatorPosition = (value ?? Positions[3]).Position;
    }

    /// <summary>Reads the saved settings from <paramref name="store"/> and saves every later change there.</summary>
    public void Use(DisplaySettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loading = true;
        try
        {
            IndicatorPosition = store.Load().IndicatorPosition;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnIndicatorPositionChanged(JunctionIndicatorPosition value)
    {
        if (!_loading && _store is { } store)
        {
            try
            {
                store.Save(new DisplaySettings { IndicatorPosition = value });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A preference that could not be saved still applies for this session.
            }
        }
    }
}
