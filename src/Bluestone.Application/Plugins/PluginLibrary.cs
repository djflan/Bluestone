using System.Collections.Immutable;
using Bluestone.Domain.Devices;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Scanning;

namespace Bluestone.Application.Plugins;

public enum PluginModuleState
{
    /// <summary>The module was read; its plugins are available.</summary>
    Loaded,

    /// <summary>The module could not be read (not a plugin module, or its scanner failed); it is tried again on the next scan.</summary>
    Failed,

    /// <summary>The module crashed or hung its scanner and is skipped until it is retried.</summary>
    Quarantined,
}

/// <summary>A plugin a module provides, in words for the plugins view, with its device definition ID.</summary>
public sealed record PluginSummary(string Name, string Vendor, string Kind, string Format, string Version, DeviceDefinitionId Definition);

/// <summary>One module file and what the last scan made of it. <see cref="Problem"/> says why it is not loaded.</summary>
public sealed record PluginModuleSummary(string ModulePath, PluginModuleState State, ImmutableArray<PluginSummary> Plugins, string? Problem)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(ModulePath));
}

/// <summary>How far a scan has got: <see cref="Done"/> of <see cref="Total"/> modules, and the one being read now (null at the end).</summary>
public readonly record struct PluginScanProgress(int Done, int Total, string? Module);

/// <summary>
/// The plugins Bluestone can use: every module file in <see cref="Folders"/>, each read by the scanner in its own process
/// (ADR 0025), with what each scan found. A full scan uses the scanner's cache; <see cref="RescanAllAsync"/> reads every
/// module again; <see cref="RetryAsync"/> reads one quarantined module again. <see cref="Changed"/> follows each of them,
/// and <see cref="PluginDeviceHost"/> takes the new <see cref="Available"/> list from it.
/// </summary>
/// <remarks>
/// One operation runs at a time; others wait. <see cref="Changed"/> and the progress callback may run on any thread.
/// The scanner belongs to the caller.
/// </remarks>
public sealed class PluginLibrary : IDisposable
{
    private readonly IPluginScanner _scanner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ModuleScanResult[] _results = [];

    /// <param name="scanner">Reads modules; <see cref="PluginScanner"/> in the app.</param>
    /// <param name="folders">Where module files are looked for, in order. Missing folders are skipped.</param>
    public PluginLibrary(IPluginScanner scanner, IEnumerable<string> folders)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        ArgumentNullException.ThrowIfNull(folders);
        Folders = [.. folders];
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Raised after a scan, a rescan, or a retry has finished and its results are in place.</summary>
    public event EventHandler? Changed;

    public ImmutableArray<string> Folders { get; }

    /// <summary>The plugins found by the last scan, each once (the first module that provides one wins).</summary>
    public ImmutableArray<PluginIdentity> Available =>
        [.. _results.SelectMany(r => r.Plugins).DistinctBy(PluginDeviceHost.DefinitionIdOf)];

    /// <summary>The module each available plugin is loaded from.</summary>
    public ImmutableDictionary<PluginIdentity, string> ModulePaths
    {
        get
        {
            var paths = ImmutableDictionary.CreateBuilder<PluginIdentity, string>();
            foreach (var result in _results)
            {
                foreach (var plugin in result.Plugins)
                {
                    paths.TryAdd(plugin, result.ModulePath);
                }
            }

            return paths.ToImmutable();
        }
    }

    /// <summary>
    /// The operating system's standard VST3 and CLAP folders, in the order they are searched: the user's own first.
    /// macOS: ~/Library/Audio/Plug-Ins/VST3 and /Library/Audio/Plug-Ins/VST3 (and the same for CLAP). Windows:
    /// %LocalAppData%\Programs\Common\VST3 and %CommonProgramFiles%\VST3 (and CLAP). Linux: ~/.vst3,
    /// /usr/local/lib/vst3, /usr/lib/vst3, ~/.clap, /usr/local/lib/clap, /usr/lib/clap.
    /// </summary>
    public static ImmutableArray<string> StandardFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return
            [
                Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3"),
                "/Library/Audio/Plug-Ins/VST3",
                Path.Combine(home, "Library", "Audio", "Plug-Ins", "CLAP"),
                "/Library/Audio/Plug-Ins/CLAP",
            ];
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common");
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            return [Path.Combine(local, "VST3"), Path.Combine(common, "VST3"), Path.Combine(local, "CLAP"), Path.Combine(common, "CLAP")];
        }

        return
        [
            Path.Combine(home, ".vst3"),
            "/usr/local/lib/vst3",
            "/usr/lib/vst3",
            Path.Combine(home, ".clap"),
            "/usr/local/lib/clap",
            "/usr/lib/clap",
        ];
    }

    /// <summary>Every module the last scan looked at, in folder order.</summary>
    public ImmutableArray<PluginModuleSummary> Modules
    {
        get
        {
            var quarantined = _scanner.Quarantined.ToDictionary(q => q.ModulePath, PathComparer);
            return [.. _results.Select(r => Summarize(r, quarantined.GetValueOrDefault(r.ModulePath)))];
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The modules in <see cref="Folders"/>, by name: every file directly in each folder, leaving out hidden files (such
    /// as .DS_Store); every VST3 or CLAP bundle (a <c>.vst3</c> or <c>.clap</c> directory); and, in subfolders (vendors
    /// often install into one), the <c>.vst3</c> and <c>.clap</c> modules, files or bundles, up to <see cref="MaxDepth"/>
    /// levels down. A bundle is a module, never searched inside.
    /// </summary>
    public IReadOnlyList<string> FindModules() =>
        [.. Folders
            .Where(Directory.Exists)
            .SelectMany(folder => ModulesIn(folder, depth: 0))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)];

    /// <summary>How many levels of subfolders are searched for VST3 and CLAP modules.</summary>
    public const int MaxDepth = 3;

    private static IEnumerable<string> ModulesIn(string folder, int depth)
    {
        IEnumerable<string> entries;
        try
        {
            entries = [.. Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (Path.GetFileName(entry).StartsWith('.'))
            {
                continue;
            }

            var isModule = IsPluginModuleName(entry);
            if (Directory.Exists(entry))
            {
                if (isModule)
                {
                    yield return entry;
                }
                else if (depth < MaxDepth)
                {
                    foreach (var nested in ModulesIn(entry, depth + 1))
                    {
                        yield return nested;
                    }
                }
            }
            else if (depth == 0 || isModule)
            {
                yield return entry;
            }
        }
    }

    private static bool IsPluginModuleName(string path) =>
        Path.GetExtension(path).Equals(".vst3", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".clap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Scans every module, using the scanner's cache and skipping quarantined modules: what the app does at start.</summary>
    public Task ScanAsync(Action<PluginScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ScanEachAsync(async path => (await _scanner.ScanAsync([path], cancellationToken).ConfigureAwait(false))[0], progress, cancellationToken);

    /// <summary>Reads every module again, ignoring the cache, so new, changed, and removed modules are found. Quarantined modules stay skipped.</summary>
    public Task RescanAllAsync(Action<PluginScanProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ScanEachAsync(path => _scanner.RescanAsync(path, ignoreQuarantine: false, cancellationToken), progress, cancellationToken);

    /// <summary>
    /// Reads one module again even though it is quarantined. If it reads cleanly it leaves quarantine and its plugins
    /// become available; if it crashes or hangs its scanner again it stays quarantined.
    /// </summary>
    public async Task<PluginModuleSummary> RetryAsync(string modulePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modulePath);
        var path = Path.GetFullPath(modulePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ModuleScanResult result;
        try
        {
            result = await _scanner.RescanAsync(path, ignoreQuarantine: true, cancellationToken).ConfigureAwait(false);
            var results = _results;
            var index = -1;
            for (var i = 0; i < results.Length; i++)
            {
                if (PathComparer.Equals(results[i].ModulePath, result.ModulePath))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                results = [.. results];
                results[index] = result;
            }
            else
            {
                results = [.. results, result];
            }

            _results = results;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Summarize(result, _scanner.Quarantined.FirstOrDefault(q => PathComparer.Equals(q.ModulePath, result.ModulePath)));
    }

    private async Task ScanEachAsync(Func<string, Task<ModuleScanResult>> scan, Action<PluginScanProgress>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var modules = FindModules();
            var results = new ModuleScanResult[modules.Count];
            for (var i = 0; i < modules.Count; i++)
            {
                progress?.Invoke(new PluginScanProgress(i, modules.Count, modules[i]));
                results[i] = await scan(modules[i]).ConfigureAwait(false);
            }

            progress?.Invoke(new PluginScanProgress(modules.Count, modules.Count, null));
            _results = results;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static PluginModuleSummary Summarize(ModuleScanResult result, QuarantineEntry? quarantine)
    {
        var plugins = result.Plugins.Select(Summarize).ToImmutableArray();
        if (quarantine is not null)
        {
            var why = quarantine.Reason == ScanFailureKind.TimedOut ? "It hung the scanner" : "It crashed the scanner";
            return new PluginModuleSummary(result.ModulePath, PluginModuleState.Quarantined, [], $"{why} and is skipped until retried. {quarantine.Message}".Trim());
        }

        if (result.Failure is { } failure)
        {
            var problem = failure.Kind switch
            {
                ScanFailureKind.Malformed => $"Not a plugin module Bluestone can read: {failure.Message}",
                ScanFailureKind.Unsupported => $"Cannot run here: {failure.Message}",
                ScanFailureKind.ProtocolError => $"The scanner misbehaved: {failure.Message}",
                _ => failure.Message,
            };
            return new PluginModuleSummary(result.ModulePath, PluginModuleState.Failed, [], problem);
        }

        return new PluginModuleSummary(result.ModulePath, PluginModuleState.Loaded, plugins, null);
    }

    private static PluginSummary Summarize(PluginIdentity plugin) =>
        new(plugin.DisplayName, plugin.Vendor, plugin.Kind switch
        {
            PluginKind.Instrument => "Instrument",
            PluginKind.MidiEffect => "MIDI effect",
            _ => "Audio effect",
        }, plugin.Format, plugin.Version, PluginDeviceHost.DefinitionIdOf(plugin));
}
