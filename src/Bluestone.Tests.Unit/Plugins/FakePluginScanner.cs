using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Scanning;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>
/// A scanner without processes, for view-model tests. A module file reading <c>plugins: A, B</c> provides instruments
/// named A and B; <c>crash</c> crashes its "scanner" and is quarantined, as <see cref="PluginScanner"/> does; anything
/// else is malformed.
/// </summary>
internal sealed class FakePluginScanner : IPluginScanner
{
    private readonly Dictionary<string, QuarantineEntry> _quarantine = [];

    /// <summary>Modules read so far (scanner processes the real scanner would have launched).</summary>
    public int Reads { get; private set; }

    /// <summary>When set, every read waits for it: a scan that is still running.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public IReadOnlyList<QuarantineEntry> Quarantined => [.. _quarantine.Values];

    public async Task<IReadOnlyList<ModuleScanResult>> ScanAsync(IEnumerable<string> modulePaths, CancellationToken cancellationToken = default)
    {
        var results = new List<ModuleScanResult>();
        foreach (var path in modulePaths)
        {
            results.Add(await RescanAsync(path, ignoreQuarantine: false, cancellationToken));
        }

        return results;
    }

    public async Task<ModuleScanResult> RescanAsync(string modulePath, bool ignoreQuarantine, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(modulePath);
        if (!ignoreQuarantine && _quarantine.ContainsKey(path))
        {
            return new ModuleScanResult(path, ModuleScanStatus.SkippedQuarantined, [], null);
        }

        Reads++;
        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken);
        if (text.StartsWith("crash", StringComparison.Ordinal))
        {
            var failure = new ScanFailure(ScanFailureKind.Crashed, "The scanner process crashed while reading the module.");
            _quarantine[path] = new QuarantineEntry(path, failure.Kind, failure.Message, DateTimeOffset.UnixEpoch);
            return new ModuleScanResult(path, ModuleScanStatus.Failed, [], failure);
        }

        _quarantine.Remove(path);
        if (!text.StartsWith("plugins:", StringComparison.Ordinal))
        {
            return new ModuleScanResult(path, ModuleScanStatus.Failed, [], new ScanFailure(ScanFailureKind.Malformed, "Not a plugin manifest."));
        }

        var module = Path.GetFileNameWithoutExtension(path);
        var plugins = text["plugins:".Length..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new PluginIdentity("bluestone-reference", module, name.ToLowerInvariant(), name, "Bluestone", PluginKind.Instrument, "1.0.0"));
        return new ModuleScanResult(path, ModuleScanStatus.Scanned, [.. plugins], null);
    }
}
