using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bluestone.Plugins.Protocol;

namespace Bluestone.Plugins.Scanning;

/// <summary>Size and last-write time of a module file. A change means the cached result is stale.</summary>
public readonly record struct ModuleFingerprint(long Size, long LastWriteUtcTicks)
{
    /// <summary>
    /// A file's size and last write time; for a bundle (a VST3 or CLAP directory), the total size and latest write time
    /// of its binaries and Info.plist, so a reinstalled or updated plugin is scanned again. Null when nothing is there.
    /// </summary>
    public static ModuleFingerprint? Of(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists)
        {
            return new ModuleFingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
        }

        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return null;
        }

        long size = 0;
        var latest = directory.LastWriteTimeUtc.Ticks;
        foreach (var file in BundleFiles(directory))
        {
            size += file.Length;
            latest = Math.Max(latest, file.LastWriteTimeUtc.Ticks);
        }

        return new ModuleFingerprint(size, latest);
    }

    private static IEnumerable<FileInfo> BundleFiles(DirectoryInfo bundle)
    {
        var contents = new DirectoryInfo(Path.Combine(bundle.FullName, "Contents"));
        if (!contents.Exists)
        {
            yield break;
        }

        var plist = new FileInfo(Path.Combine(contents.FullName, "Info.plist"));
        if (plist.Exists)
        {
            yield return plist;
        }

        foreach (var folder in contents.EnumerateDirectories())
        {
            if (folder.Name == "MacOS" || folder.Name.EndsWith("-win", StringComparison.Ordinal) || folder.Name.EndsWith("-linux", StringComparison.Ordinal))
            {
                foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }
    }
}

public sealed record ScanFailure(ScanFailureKind Kind, string Message);

public sealed record QuarantineEntry(string ModulePath, ScanFailureKind Reason, string Message, DateTimeOffset QuarantinedAt);

/// <summary>
/// A JSON file of earlier scan results, keyed by path plus fingerprint. Writes are atomic (temporary file, then
/// rename). A missing file is empty; an unreadable one is treated as empty and reported in <see cref="Diagnostics"/>.
/// Not thread-safe; <see cref="PluginScanner"/> serializes access.
/// </summary>
internal sealed class ScanCache
{
    private const int FormatVersion = 1;

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;

    private ScanCache(string path, Dictionary<string, Entry> entries, IReadOnlyList<string> diagnostics)
    {
        _path = path;
        _entries = entries;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<string> Diagnostics { get; }

    public int Count => _entries.Count;

    public static ScanCache Load(string path)
    {
        var (file, diagnostic) = JsonFile.Read<CacheFile>(path, "scan cache");
        var entries = new Dictionary<string, Entry>(PathComparer);
        if (file is not null && file.Version == FormatVersion)
        {
            foreach (var entry in file.Entries ?? [])
            {
                if (entry.Path is not null)
                {
                    entries[entry.Path] = entry;
                }
            }
        }
        else if (file is not null)
        {
            diagnostic = $"The scan cache '{path}' has unknown version {file.Version}; it was reset.";
        }

        return new ScanCache(path, entries, diagnostic is null ? [] : [diagnostic]);
    }

    public bool TryGet(string modulePath, ModuleFingerprint fingerprint, out ImmutableArray<PluginIdentity> plugins, out ScanFailure? failure)
    {
        plugins = [];
        failure = null;
        if (!_entries.TryGetValue(modulePath, out var entry) || entry.Size != fingerprint.Size || entry.LastWriteUtcTicks != fingerprint.LastWriteUtcTicks)
        {
            return false;
        }

        try
        {
            plugins = [.. (entry.Plugins ?? []).Select(p => p.ToIdentity())];
        }
        catch (ArgumentException)
        {
            return false;
        }

        failure = entry.FailureKind is { } kind ? new ScanFailure(kind, entry.FailureMessage ?? string.Empty) : null;
        return true;
    }

    public void Set(string modulePath, ModuleFingerprint fingerprint, ImmutableArray<PluginIdentity> plugins, ScanFailure? failure)
    {
        _entries[modulePath] = new Entry
        {
            Path = modulePath,
            Size = fingerprint.Size,
            LastWriteUtcTicks = fingerprint.LastWriteUtcTicks,
            Plugins = [.. plugins.Select(PluginRecord.From)],
            FailureKind = failure?.Kind,
            FailureMessage = failure?.Message,
        };
    }

    public void Remove(string modulePath) => _entries.Remove(modulePath);

    public void Save() => JsonFile.WriteAtomic(_path, new CacheFile { Version = FormatVersion, Entries = [.. _entries.Values] });

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal sealed class CacheFile
    {
        public int Version { get; set; }

        public List<Entry>? Entries { get; set; }
    }

    internal sealed class Entry
    {
        public string? Path { get; set; }

        public long Size { get; set; }

        public long LastWriteUtcTicks { get; set; }

        public List<PluginRecord>? Plugins { get; set; }

        public ScanFailureKind? FailureKind { get; set; }

        public string? FailureMessage { get; set; }
    }
}

/// <summary>
/// Modules that crashed or hung a scanner. They are skipped (no scanner is launched) until rescanned with
/// <c>ignoreQuarantine</c> or cleared. Persisted atomically like <see cref="ScanCache"/>.
/// </summary>
internal sealed class ScanQuarantine
{
    private const int FormatVersion = 1;

    private readonly string _path;
    private readonly Dictionary<string, QuarantineEntry> _entries;

    private ScanQuarantine(string path, Dictionary<string, QuarantineEntry> entries, IReadOnlyList<string> diagnostics)
    {
        _path = path;
        _entries = entries;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<string> Diagnostics { get; }

    public IReadOnlyList<QuarantineEntry> Entries => [.. _entries.Values];

    public static ScanQuarantine Load(string path)
    {
        var (file, diagnostic) = JsonFile.Read<QuarantineFile>(path, "scan quarantine list");
        var entries = new Dictionary<string, QuarantineEntry>(ScanCache.PathComparer);
        if (file is not null && file.Version == FormatVersion)
        {
            foreach (var entry in file.Entries ?? [])
            {
                if (entry.ModulePath is not null)
                {
                    entries[entry.ModulePath] = entry;
                }
            }
        }
        else if (file is not null)
        {
            diagnostic = $"The scan quarantine list '{path}' has unknown version {file.Version}; it was reset.";
        }

        return new ScanQuarantine(path, entries, diagnostic is null ? [] : [diagnostic]);
    }

    public bool Contains(string modulePath) => _entries.ContainsKey(modulePath);

    public void Add(QuarantineEntry entry) => _entries[entry.ModulePath] = entry;

    public bool Remove(string modulePath) => _entries.Remove(modulePath);

    public void Save() => JsonFile.WriteAtomic(_path, new QuarantineFile { Version = FormatVersion, Entries = [.. _entries.Values] });

    internal sealed class QuarantineFile
    {
        public int Version { get; set; }

        public List<QuarantineEntry>? Entries { get; set; }
    }
}

internal sealed class PluginRecord
{
    public string? Format { get; set; }

    public string? ModuleId { get; set; }

    public string? PluginId { get; set; }

    public string? DisplayName { get; set; }

    public string? Vendor { get; set; }

    public PluginKind Kind { get; set; }

    public string? Version { get; set; }

    public static PluginRecord From(PluginIdentity identity) => new()
    {
        Format = identity.Format,
        ModuleId = identity.ModuleId,
        PluginId = identity.PluginId,
        DisplayName = identity.DisplayName,
        Vendor = identity.Vendor,
        Kind = identity.Kind,
        Version = identity.Version,
    };

    public PluginIdentity ToIdentity() =>
        new(Format ?? string.Empty, ModuleId ?? string.Empty, PluginId ?? string.Empty, DisplayName ?? string.Empty, Vendor ?? string.Empty, Kind, Version ?? string.Empty);
}

internal static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads a file; a missing file is (null, null), an unreadable one is (null, diagnostic).</summary>
    public static (T? Value, string? Diagnostic) Read<T>(string path, string what)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }

            var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options);
            return value is null ? (null, $"The {what} '{path}' was empty; it was reset.") : (value, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, $"The {what} '{path}' could not be read and was reset: {ex.Message}");
        }
    }

    /// <summary>Writes to a temporary file beside the target and renames it over the target, so a reader never sees a partial file.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
