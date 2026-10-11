namespace Bluestone.Plugins.Scanning;

/// <summary>
/// Scanning as the host's plugin list uses it: <see cref="PluginScanner"/>, with scanner processes, in the app; a fake in
/// view-model tests.
/// </summary>
public interface IPluginScanner
{
    IReadOnlyList<QuarantineEntry> Quarantined { get; }

    /// <summary>Scans each module in turn, using the cache and skipping quarantined modules.</summary>
    Task<IReadOnlyList<ModuleScanResult>> ScanAsync(IEnumerable<string> modulePaths, CancellationToken cancellationToken = default);

    /// <summary>Scans one module again, ignoring the cache; with <paramref name="ignoreQuarantine"/> also a quarantined one.</summary>
    Task<ModuleScanResult> RescanAsync(string modulePath, bool ignoreQuarantine, CancellationToken cancellationToken = default);
}
