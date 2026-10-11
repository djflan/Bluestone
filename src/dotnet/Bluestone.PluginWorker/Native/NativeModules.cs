using System.Collections.Immutable;
using System.Text.Json;
using Bluestone.Plugins.Protocol;

namespace Bluestone.PluginWorker.Native;

/// <summary>Third-party plugin formats and their modules, loaded through the native plugin host. Main thread only.</summary>
internal static class NativeModules
{
    public const string Vst3 = "vst3";
    public const string Clap = "clap";

    private static readonly Dictionary<string, nint> Open = new(StringComparer.Ordinal);

    public static bool IsNativeFormat(string format) => format is Vst3 or Clap;

    /// <summary>The format a module path belongs to by its extension, or null.</summary>
    public static string? FormatOf(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return Path.GetExtension(trimmed).ToLowerInvariant() switch
        {
            ".vst3" => Vst3,
            ".clap" => Clap,
            _ => null,
        };
    }

    /// <summary>The module id Bluestone uses for a module file: its name without the extension, the same on every machine.</summary>
    public static string ModuleIdOf(string path) => Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Loads a module once per process and keeps it loaded. Throws <see cref="InvalidOperationException"/> with the reason on failure.</summary>
    public static nint Load(string format, string path)
    {
        WorkerMainThread.Current?.EnsureNative();
        var key = format + "|" + Path.GetFullPath(path);
        if (Open.TryGetValue(key, out var module))
        {
            return module;
        }

        var code = format == Vst3 ? NativeMethods.FormatVst3 : NativeMethods.FormatClap;
        if (NativeMethods.ModuleOpen(code, Path.GetFullPath(path), out module, out var error) != 0)
        {
            throw new InvalidOperationException(NativeMethods.TakeString(error) ?? "The module could not be loaded.");
        }

        Open[key] = module;
        return module;
    }

    /// <summary>The plugins a module provides.</summary>
    public static ImmutableArray<PluginIdentity> Describe(string format, string path)
    {
        var module = Load(format, path);
        var json = NativeMethods.TakeString(NativeMethods.ModuleDescribe(module)) ?? "[]";
        using var document = JsonDocument.Parse(json);
        var plugins = ImmutableArray.CreateBuilder<PluginIdentity>();
        foreach (var plugin in document.RootElement.EnumerateArray().Take(ProtocolLimits.MaxPluginsPerModule))
        {
            var kind = plugin.GetProperty("kind").GetInt32() switch
            {
                1 => PluginKind.Instrument,
                2 => PluginKind.MidiEffect,
                _ => PluginKind.AudioEffect,
            };
            plugins.Add(new PluginIdentity(
                format,
                Truncate(ModuleIdOf(path)),
                Truncate(plugin.GetProperty("id").GetString() ?? string.Empty),
                Truncate(plugin.GetProperty("name").GetString() ?? string.Empty),
                Truncate(plugin.GetProperty("vendor").GetString() ?? string.Empty),
                kind,
                Truncate(plugin.GetProperty("version").GetString() ?? string.Empty)));
        }

        return plugins.ToImmutable();
    }

    private static string Truncate(string text) => text.Length <= 256 ? text : text[..256];
}
