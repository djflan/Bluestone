using System.Text.Json;
using System.Text.Json.Serialization;
using Bluestone.Audio;

namespace Bluestone.Presentation;

/// <summary>A mode a mixer strip or Master meter can display.</summary>
public sealed record MixerMeterModeOption(MixerMeterMode Mode, string Name)
{
    public override string ToString() => Name;
}


/// <summary>
/// Application-local mixer meter preferences; these never belong to a project. Files written before the single LED meter
/// may hold a "presentation" value; it is ignored on load.
/// </summary>
public sealed record MixerMeterDisplaySettings
{
    public MixerMeterMode Mode { get; init; } = MixerMeterMode.PeakRms;


    public int VuReferenceDbfs { get; init; } = -18;

    public static MixerMeterDisplaySettings Default { get; } = new();
}

/// <summary>Reads and writes mixer meter preferences separately from project data.</summary>
public sealed class MixerMeterDisplaySettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public MixerMeterDisplaySettings Load()
    {
        try
        {
            var settings = File.Exists(Path)
                ? JsonSerializer.Deserialize<MixerMeterDisplaySettings>(File.ReadAllText(Path), Options)
                : null;
            if (settings is null)
            {
                return MixerMeterDisplaySettings.Default;
            }

            var defaults = MixerMeterDisplaySettings.Default;
            return new MixerMeterDisplaySettings
            {
                Mode = Enum.IsDefined(settings.Mode) ? settings.Mode : defaults.Mode,
                VuReferenceDbfs = settings.VuReferenceDbfs is >= -36 and <= 0 ? settings.VuReferenceDbfs : defaults.VuReferenceDbfs,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return MixerMeterDisplaySettings.Default;
        }
    }

    /// <summary>Writes atomically (a temporary file, then a rename).</summary>
    public void Save(MixerMeterDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Mode) || settings.VuReferenceDbfs is < -36 or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "Mixer meter settings are outside the supported choices.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Path, overwrite: true);
    }
}
