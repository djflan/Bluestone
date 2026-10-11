using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bluestone.Application.Audio;

/// <summary>The audio output the user chose. Saved per user, not per project.</summary>
/// <param name="Enabled">Open the device when Bluestone starts.</param>
/// <param name="DeviceId">An audio device id, or null for the system's default output.</param>
/// <param name="SampleRate">In Hz.</param>
/// <param name="BufferFrames">Frames per device callback.</param>
/// <param name="QueuedFrames">
/// Frames rendered ahead of the device by Bluestone's render thread (0: render in the device callback). Absorbs .NET
/// garbage-collection pauses at the cost of this much latency; ADR 0032 has the measurements behind the default.
/// </param>
/// <param name="LiveQueuedFrames">
/// The smaller queue used while live input plays a plugin instrument (ADR 0035): it takes over at the first live note
/// and the playback queue comes back after <see cref="AudioPlayback.LiveHold"/> without live input. 0 keeps the
/// playback queue. Lower latency for playing, at a higher risk of dropouts under load.
/// </param>
/// <param name="LowLatencyMonitoring">
/// The plugin instrument played live is not delayed for delay compensation (ADR 0035), so it is heard sooner; its own
/// playback is then that much early against the other tracks while it is monitored.
/// </param>
public sealed record AudioSettings(
    bool Enabled = false,
    string? DeviceId = null,
    int SampleRate = 48_000,
    int BufferFrames = 256,
    int QueuedFrames = AudioSettings.DefaultQueuedFrames,
    int LiveQueuedFrames = AudioSettings.DefaultLiveQueuedFrames,
    bool LowLatencyMonitoring = false)
{
    public const int DefaultQueuedFrames = 1024;

    public const int DefaultLiveQueuedFrames = 512;

    public static IReadOnlyList<int> BufferSizes { get; } = [64, 128, 256, 512, 1024];

    public static IReadOnlyList<int> QueueSizes { get; } = [0, 256, 512, 1024, 2048, 4096];

    /// <summary>Live queue choices; 0 keeps the playback queue.</summary>
    public static IReadOnlyList<int> LiveQueueSizes { get; } = [0, 128, 256, 512];
}

/// <summary>Reads and writes <see cref="AudioSettings"/> as a small JSON file. A missing or unreadable file gives the defaults.</summary>
public sealed class AudioSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public AudioSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(Path), Options) ?? new AudioSettings() : new AudioSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AudioSettings();
        }
    }

    /// <summary>Writes atomically (a temporary file, then a rename).</summary>
    public void Save(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, Path, overwrite: true);
    }
}
