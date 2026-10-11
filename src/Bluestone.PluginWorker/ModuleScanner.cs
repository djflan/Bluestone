using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Bluestone.Plugins.Protocol;
using Bluestone.PluginWorker.Native;
using Bluestone.PluginWorker.Plugins;

namespace Bluestone.PluginWorker;

/// <summary>
/// Scan mode: reads one module and reports the plugins it provides. A VST3 bundle (<c>.vst3</c>) or CLAP module
/// (<c>.clap</c>) is loaded by the native plugin host on the main thread and asked for its plugins; a module built for
/// another processor architecture is <see cref="ScanFailureKind.Unsupported"/> and is not loaded. A reference module
/// is a small JSON manifest with the <see cref="ModuleExtension"/> extension, never loaded as code. Every other file is
/// <see cref="ScanFailureKind.Malformed"/>. Two documented first-line markers exist for tests only: <see cref="CrashMarker"/> makes the
/// scanner call <see cref="Environment.FailFast(string)"/>, and <see cref="HangMarker"/> makes it hang, so the host's
/// crash and timeout handling can be exercised with a real process.
/// </summary>
internal static class ModuleScanner
{
    public const string ModuleExtension = ".bluestone-reference-plugin";
    public const string ManifestFormat = "bluestone-reference-plugin";
    public const string CrashMarker = "#bluestone-test: crash-scanner";
    public const string HangMarker = "#bluestone-test: hang-scanner";
    public const int MaxModuleBytes = 64 * 1024;

    public static ScanResult Scan(string path)
    {
        if (NativeModules.FormatOf(path) is { } format)
        {
            return ScanNative(path, format);
        }

        if (!path.EndsWith(ModuleExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Malformed(path, $"Not a Bluestone reference plugin module (expected a {ModuleExtension} file).");
        }

        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Malformed(path, "The module file does not exist.");
            }

            if (info.Length > MaxModuleBytes)
            {
                return Malformed(path, "The module file is too large to be a manifest.");
            }

            text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Malformed(path, $"The module file cannot be read: {ex.Message}");
        }

        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
        if (firstLine == CrashMarker)
        {
            Environment.FailFast("Bluestone plugin scanner: crash induced by the test marker in " + path);
        }

        if (firstLine == HangMarker)
        {
            Thread.Sleep(Timeout.Infinite);
        }

        try
        {
            return new ScanResult(path, ParseManifest(text), null, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
        {
            return Malformed(path, $"The manifest is invalid: {ex.Message}");
        }
    }

    private static ScanResult ScanNative(string path, string format)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Malformed(path, "The module does not exist.");
        }

        if (BinaryArchitecture.Check(path) is { } unsupported)
        {
            return new ScanResult(path, [], ScanFailureKind.Unsupported, unsupported);
        }

        var main = WorkerMainThread.Current ?? throw new InvalidOperationException("Native modules are scanned on the worker's main thread.");
        try
        {
            var plugins = main.Invoke(() => NativeModules.Describe(format, path));
            return plugins.IsEmpty
                ? Malformed(path, $"The {format.ToUpperInvariant()} module provides no plugins Bluestone can host.")
                : new ScanResult(path, plugins, null, null);
        }
        catch (NotSupportedException ex)
        {
            return new ScanResult(path, [], ScanFailureKind.Unsupported, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException or KeyNotFoundException or FormatException)
        {
            return Malformed(path, ex.Message);
        }
    }

    private static ImmutableArray<PluginIdentity> ParseManifest(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != ManifestFormat)
        {
            throw new InvalidDataException($"'format' must be '{ManifestFormat}'.");
        }

        if (root.GetProperty("version").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported manifest version.");
        }

        var moduleId = Required(root, "moduleId");
        var plugins = root.GetProperty("plugins");
        if (plugins.GetArrayLength() is 0 or > ProtocolLimits.MaxPluginsPerModule)
        {
            throw new InvalidDataException("A module must declare between 1 and 1024 plugins.");
        }

        var result = ImmutableArray.CreateBuilder<PluginIdentity>();
        foreach (var plugin in plugins.EnumerateArray())
        {
            var kind = Enum.Parse<PluginKind>(Required(plugin, "kind"), ignoreCase: false);
            if (!Enum.IsDefined(kind))
            {
                throw new InvalidDataException("Unknown plugin kind.");
            }

            result.Add(new PluginIdentity(
                ReferencePlugin.Format,
                moduleId,
                Required(plugin, "pluginId"),
                Required(plugin, "name"),
                plugin.TryGetProperty("vendor", out var vendor) ? vendor.GetString() ?? string.Empty : string.Empty,
                kind,
                plugin.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty));
        }

        return result.ToImmutable();
    }

    private static string Required(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetString();
        return string.IsNullOrEmpty(value) ? throw new InvalidDataException($"'{name}' is required.") : value;
    }

    private static ScanResult Malformed(string path, string message) => new(path, [], ScanFailureKind.Malformed, message);
}
