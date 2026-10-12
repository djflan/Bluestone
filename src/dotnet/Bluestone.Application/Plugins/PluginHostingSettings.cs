using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bluestone.Domain.Devices;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Application.Plugins;

/// <summary>
/// How plugins share worker processes (ADR 0034), saved per user, not per project: a global mode, a mode per plugin
/// that overrides it (for example "individually" for a plugin that crashes), and whether crashed plugins restart by
/// themselves.
/// </summary>
public sealed record PluginHostingSettings
{
    /// <summary>The global default: one worker per plugin module, as in Bitwig Studio. ADR 0034 has the reasoning.</summary>
    public const HostingMode DefaultMode = HostingMode.ByPlugin;

    /// <summary>The modes a user can choose, from most sharing to least. Hosting within Bluestone is never offered.</summary>
    public static ImmutableArray<HostingMode> Modes { get; } =
        [HostingMode.Together, HostingMode.ByManufacturer, HostingMode.ByPlugin, HostingMode.Individually];

    public HostingMode Mode { get; init; } = DefaultMode;

    /// <summary>Restart a plugin whose worker failed, a few times, with back-off (<see cref="RestartPolicy"/>); off by default.</summary>
    public bool AutoRestart { get; init; }

    /// <summary>
    /// Whether floating plugin editor windows follow the selected track: unpinned editors hide while their track is not
    /// selected and come back when it is. A workspace preference; it changes nothing that plays.
    /// </summary>
    public bool EditorsFollowSelection { get; init; } = true;

    /// <summary>Modes chosen for single plugins, by device definition ID (<c>plugin:&lt;format&gt;:&lt;module&gt;/&lt;plugin&gt;</c>).</summary>
    public ImmutableDictionary<string, HostingMode> Overrides { get; init; } = ImmutableDictionary<string, HostingMode>.Empty;

    /// <summary>The restart policy plugin instances get under these settings.</summary>
    [JsonIgnore]
    public RestartPolicy RestartPolicy => AutoRestart ? RestartPolicy.Default with { AutoRestart = true } : RestartPolicy.Default;

    public static string NameOf(HostingMode mode) => mode switch
    {
        HostingMode.Together => "Together",
        HostingMode.ByManufacturer => "By manufacturer",
        HostingMode.ByPlugin => "By plugin",
        HostingMode.Individually => "Individually",
        _ => "Within Bluestone",
    };

    public static string DescriptionOf(HostingMode mode) => mode switch
    {
        HostingMode.Together => "All plugins share one process. Fewest processes; a crash takes every plugin down.",
        HostingMode.ByManufacturer => "One process per manufacturer. A crash takes down that manufacturer's plugins.",
        HostingMode.ByPlugin => "One process per plugin. A crash takes down every copy of that plugin.",
        HostingMode.Individually => "One process per device. A crash takes down only that device; uses the most memory.",
        _ => "Not available: plugins never run inside Bluestone.",
    };

    /// <summary>The override for <paramref name="plugin"/>, else the global mode.</summary>
    public HostingMode ModeFor(PluginIdentity plugin) => ModeFor(PluginDeviceHost.DefinitionIdOf(plugin));

    public HostingMode ModeFor(DeviceDefinitionId plugin) => Overrides.TryGetValue(plugin.Value, out var mode) ? mode : Mode;

    /// <summary>These settings with <paramref name="plugin"/> hosted as <paramref name="mode"/>, or following the global mode when null.</summary>
    public PluginHostingSettings WithOverride(DeviceDefinitionId plugin, HostingMode? mode)
    {
        if (mode is { } chosen && !Modes.Contains(chosen))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Plugins are hosted only in worker processes.");
        }

        return this with { Overrides = mode is { } m ? Overrides.SetItem(plugin.Value, m) : Overrides.Remove(plugin.Value) };
    }

    /// <summary>Settings with only choices a user can make: an unknown or in-process mode (from a hand-edited file) is dropped.</summary>
    public PluginHostingSettings Sanitized() => this with
    {
        Mode = Modes.Contains(Mode) ? Mode : DefaultMode,
        Overrides = (Overrides ?? ImmutableDictionary<string, HostingMode>.Empty).Where(o => Modes.Contains(o.Value)).ToImmutableDictionary(),
    };

    public bool Equals(PluginHostingSettings? other) =>
        other is not null && Mode == other.Mode && AutoRestart == other.AutoRestart && EditorsFollowSelection == other.EditorsFollowSelection
        && Overrides.Count == other.Overrides.Count && Overrides.All(o => other.Overrides.TryGetValue(o.Key, out var m) && m == o.Value);

    public override int GetHashCode() => HashCode.Combine(Mode, AutoRestart, EditorsFollowSelection, Overrides.Count);
}

/// <summary>Reads and writes <see cref="PluginHostingSettings"/> as a small JSON file. A missing or unreadable file gives the defaults.</summary>
public sealed class PluginHostingSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public PluginHostingSettings Load()
    {
        try
        {
            return File.Exists(Path)
                ? (JsonSerializer.Deserialize<PluginHostingSettings>(File.ReadAllText(Path), Options) ?? new PluginHostingSettings()).Sanitized()
                : new PluginHostingSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new PluginHostingSettings();
        }
    }

    /// <summary>Writes atomically (a temporary file, then a rename).</summary>
    public void Save(PluginHostingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Path, overwrite: true);
    }
}
