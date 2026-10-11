using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Workers;

namespace Bluestone.Plugins.Scanning;

public enum ModuleScanStatus
{
    /// <summary>A scanner process read the module.</summary>
    Scanned,

    /// <summary>The cached result still matches the file's fingerprint; no scanner was launched.</summary>
    Cached,

    /// <summary>The scan failed; see <see cref="ModuleScanResult.Failure"/>.</summary>
    Failed,

    /// <summary>The module is quarantined after an earlier crash or timeout; no scanner was launched.</summary>
    SkippedQuarantined,
}

public sealed record ModuleScanResult(string ModulePath, ModuleScanStatus Status, ImmutableArray<PluginIdentity> Plugins, ScanFailure? Failure);

public sealed record PluginScannerOptions
{
    public PluginScannerOptions(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheDirectory);
        CacheDirectory = cacheDirectory;
    }

    /// <summary>Where <c>scan-cache.json</c> and <c>scan-quarantine.json</c> live.</summary>
    public string CacheDirectory { get; }

    public string? WorkerPath { get; init; }

    /// <summary>Hard limit for one module: launch, handshake, and scan. A scanner still running then is killed.</summary>
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Discovers plugins with one separate scanner process per module file, never in this process. A module that
/// crashes or hangs its scanner is reported and quarantined; later scans skip it without launching anything until
/// <see cref="RescanAsync"/> with <c>ignoreQuarantine</c> or <see cref="ClearQuarantine"/>. A malformed file is an
/// ordinary failure (cached, not quarantined). Scanning never throws because of a bad module.
/// </summary>
public sealed class PluginScanner : IDisposable, IPluginScanner
{
    public const string CacheFileName = "scan-cache.json";
    public const string QuarantineFileName = "scan-quarantine.json";

    private readonly PluginScannerOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ScanCache _cache;
    private readonly ScanQuarantine _quarantine;
    private int _launches;

    public PluginScanner(PluginScannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _cache = ScanCache.Load(Path.Combine(options.CacheDirectory, CacheFileName));
        _quarantine = ScanQuarantine.Load(Path.Combine(options.CacheDirectory, QuarantineFileName));
        Diagnostics = [.. _cache.Diagnostics, .. _quarantine.Diagnostics];
    }

    /// <summary>Problems found while loading the cache or quarantine list (each was reset to empty).</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Scanner processes launched by this instance.</summary>
    public int ScannerLaunchCount => Volatile.Read(ref _launches);

    public IReadOnlyList<QuarantineEntry> Quarantined => _quarantine.Entries;

    /// <summary>Scans each module in turn, using the cache and skipping quarantined modules.</summary>
    public async Task<IReadOnlyList<ModuleScanResult>> ScanAsync(IEnumerable<string> modulePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modulePaths);
        var results = new List<ModuleScanResult>();
        foreach (var path in modulePaths)
        {
            results.Add(await ScanOneAsync(path, ignoreQuarantine: false, useCache: true, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Scans one module again, ignoring the cache; with <paramref name="ignoreQuarantine"/> also a quarantined one.</summary>
    public Task<ModuleScanResult> RescanAsync(string modulePath, bool ignoreQuarantine, CancellationToken cancellationToken = default) =>
        ScanOneAsync(modulePath, ignoreQuarantine, useCache: false, cancellationToken);

    public void Dispose() => _gate.Dispose();

    /// <summary>Takes a module off the quarantine list. True if it was on it.</summary>
    public bool ClearQuarantine(string modulePath)
    {
        _gate.Wait();
        try
        {
            var removed = _quarantine.Remove(Path.GetFullPath(modulePath));
            if (removed)
            {
                _quarantine.Save();
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModuleScanResult> ScanOneAsync(string modulePath, bool ignoreQuarantine, bool useCache, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(modulePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ignoreQuarantine && _quarantine.Contains(path))
            {
                return new ModuleScanResult(path, ModuleScanStatus.SkippedQuarantined, [], null);
            }

            if (ModuleFingerprint.Of(path) is not { } fingerprint)
            {
                return new ModuleScanResult(path, ModuleScanStatus.Failed, [], new ScanFailure(ScanFailureKind.Malformed, "The module file does not exist."));
            }

            if (useCache && _cache.TryGet(path, fingerprint, out var cachedPlugins, out var cachedFailure))
            {
                return new ModuleScanResult(path, ModuleScanStatus.Cached, cachedPlugins, cachedFailure);
            }

            var (plugins, failure, blameModule) = await RunScannerAsync(path, cancellationToken).ConfigureAwait(false);
            if (blameModule && failure is { Kind: ScanFailureKind.Crashed or ScanFailureKind.TimedOut })
            {
                _quarantine.Add(new QuarantineEntry(path, failure.Kind, failure.Message, _options.TimeProvider.GetUtcNow()));
                _cache.Remove(path);
            }
            else if (blameModule)
            {
                _quarantine.Remove(path);
                if (failure is null or { Kind: ScanFailureKind.Malformed })
                {
                    _cache.Set(path, fingerprint, plugins, failure);
                }
            }

            _cache.Save();
            _quarantine.Save();
            return new ModuleScanResult(path, failure is null ? ModuleScanStatus.Scanned : ModuleScanStatus.Failed, plugins, failure);
        }
        finally
        {
            _gate.Release();
        }
    }

    // The module is read in a separate scanner process; this supervises it. BlameModule is false when the scanner
    // failed before it was given the module, which must not quarantine the module.
    private async Task<(ImmutableArray<PluginIdentity> Plugins, ScanFailure? Failure, bool BlameModule)> RunScannerAsync(string path, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_options.ScanTimeout);
        WorkerProcess? worker = null;
        var reading = false;
        try
        {
            Interlocked.Increment(ref _launches);
            worker = await WorkerProcess.StartAsync(
                WorkerMode.Scanner,
                new WorkerProcessOptions { WorkerPath = _options.WorkerPath, HandshakeTimeout = _options.ScanTimeout, HeartbeatInterval = null },
                onTerminated: null,
                limit.Token).ConfigureAwait(false);
            reading = true;
            var reply = await worker.RequestAsync(new ScanModule(path), _options.ScanTimeout, limit.Token).ConfigureAwait(false);
            return reply switch
            {
                ScanResult { Failure: null } r => (r.Plugins, null, true),
                ScanResult { Failure: ScanFailureKind.Malformed } r => ([], new ScanFailure(ScanFailureKind.Malformed, r.Message ?? "Malformed module."), true),
                _ => ([], new ScanFailure(ScanFailureKind.ProtocolError, $"The scanner answered {reply.Type}."), true),
            };
        }
        catch (WorkerStartException ex)
        {
            var kind = ex.Reason == UnavailableReason.ProtocolError ? ScanFailureKind.ProtocolError : ScanFailureKind.Crashed;
            return ([], new ScanFailure(kind, $"The scanner process could not start: {ex.Message}"), false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            worker?.Kill(WorkerExitReason.Hung);
            return ([], new ScanFailure(ScanFailureKind.TimedOut, $"The scanner did not finish within {_options.ScanTimeout.TotalSeconds:0.###} s."), reading);
        }
        catch (PluginUnavailableException ex)
        {
            var reason = worker is null ? WorkerExitReason.Crashed : await ExitReasonAsync(worker).ConfigureAwait(false);
            if (reason == WorkerExitReason.ProtocolError)
            {
                return ([], new ScanFailure(ScanFailureKind.ProtocolError, ex.Message), false);
            }

            var output = worker is null ? string.Empty : string.Join(" | ", worker.RecentOutput.TakeLast(3));
            return ([], new ScanFailure(ScanFailureKind.Crashed, $"The scanner process crashed while reading the module. {output}".Trim()), reading);
        }
        finally
        {
            if (worker is not null)
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<WorkerExitReason> ExitReasonAsync(WorkerProcess worker)
    {
        try
        {
            return await worker.Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            worker.Kill(WorkerExitReason.Crashed);
            return WorkerExitReason.Crashed;
        }
    }
}
