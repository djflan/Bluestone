using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Scanning;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class ScanCacheTests : IDisposable
{
    private static readonly PluginIdentity Gain = new("bluestone-reference", "bluestone.reference", "reference.gain", "Gain", "Bluestone", PluginKind.AudioEffect, "1.0.0");

    private readonly TestDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string CachePath => _directory.File(PluginScanner.CacheFileName);

    [Fact]
    public void SavedResults_LoadAgain_WhileTheFingerprintMatches()
    {
        var cache = ScanCache.Load(CachePath);
        var fingerprint = new ModuleFingerprint(120, 638_000_000_000_000_000);
        cache.Set("/m/a.bluestone-reference-plugin", fingerprint, [Gain], null);
        cache.Set("/m/b.txt", new ModuleFingerprint(3, 1), [], new ScanFailure(ScanFailureKind.Malformed, "nope"));
        cache.Save();

        var loaded = ScanCache.Load(CachePath);

        Assert.Empty(loaded.Diagnostics);
        Assert.True(loaded.TryGet("/m/a.bluestone-reference-plugin", fingerprint, out var plugins, out var failure));
        Assert.Equal([Gain], plugins);
        Assert.Null(failure);
        Assert.True(loaded.TryGet("/m/b.txt", new ModuleFingerprint(3, 1), out _, out var malformed));
        Assert.Equal(new ScanFailure(ScanFailureKind.Malformed, "nope"), malformed);
    }

    [Fact]
    public void AChangedFingerprint_IsACacheMiss()
    {
        var cache = ScanCache.Load(CachePath);
        cache.Set("/m/a", new ModuleFingerprint(120, 5), [Gain], null);

        Assert.False(cache.TryGet("/m/a", new ModuleFingerprint(121, 5), out _, out _));
        Assert.False(cache.TryGet("/m/a", new ModuleFingerprint(120, 6), out _, out _));
    }

    [Fact]
    public void Save_IsAtomic_AndLeavesNoTemporaryFiles()
    {
        var cache = ScanCache.Load(CachePath);
        cache.Set("/m/a", new ModuleFingerprint(1, 1), [Gain], null);
        cache.Save();
        cache.Set("/m/b", new ModuleFingerprint(2, 2), [Gain], null);
        cache.Save();

        Assert.Equal([PluginScanner.CacheFileName], Directory.GetFiles(_directory.Path).Select(Path.GetFileName));
        Assert.Equal(2, ScanCache.Load(CachePath).Count);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"version\": 99, \"entries\": []}")]
    public void ACorruptCache_LoadsEmpty_WithADiagnostic(string content)
    {
        File.WriteAllText(CachePath, content);

        var cache = ScanCache.Load(CachePath);

        Assert.Equal(0, cache.Count);
        Assert.Single(cache.Diagnostics);
    }

    [Fact]
    public void AMissingCache_IsEmpty_WithoutADiagnostic()
    {
        var cache = ScanCache.Load(CachePath);

        Assert.Equal(0, cache.Count);
        Assert.Empty(cache.Diagnostics);
    }

    [Fact]
    public void FilesWrittenBeforeSourceGeneratedJson_StillLoad_AndSaveTheSame()
    {
        // Exactly what the reflection-based serializer wrote before the scan files moved to source-generated JSON.
        File.WriteAllText(CachePath, """
            {
              "version": 1,
              "entries": [
                {
                  "path": "/m/a.vst3",
                  "size": 120,
                  "lastWriteUtcTicks": 638000000000000000,
                  "plugins": [
                    {
                      "format": "vst3",
                      "moduleId": "acme.mod",
                      "pluginId": "acme.verb",
                      "displayName": "Verb",
                      "vendor": "Acme",
                      "kind": "AudioEffect",
                      "version": "2.1"
                    }
                  ],
                  "failureKind": null,
                  "failureMessage": null
                },
                {
                  "path": "/m/b.txt",
                  "size": 3,
                  "lastWriteUtcTicks": 1,
                  "plugins": [],
                  "failureKind": "Malformed",
                  "failureMessage": "nope"
                }
              ]
            }
            """);
        var quarantinePath = _directory.File(PluginScanner.QuarantineFileName);
        File.WriteAllText(quarantinePath, """
            {
              "version": 1,
              "entries": [
                {
                  "modulePath": "/m/crash.clap",
                  "reason": "Crashed",
                  "message": "boom",
                  "quarantinedAt": "2026-11-03T10:00:00-06:00"
                }
              ]
            }
            """);
        var cacheBefore = File.ReadAllText(CachePath);
        var quarantineBefore = File.ReadAllText(quarantinePath);

        var cache = ScanCache.Load(CachePath);
        var quarantine = ScanQuarantine.Load(quarantinePath);

        Assert.Empty(cache.Diagnostics);
        Assert.True(cache.TryGet("/m/a.vst3", new ModuleFingerprint(120, 638_000_000_000_000_000), out var plugins, out _));
        Assert.Equal([new PluginIdentity("vst3", "acme.mod", "acme.verb", "Verb", "Acme", PluginKind.AudioEffect, "2.1")], plugins);
        Assert.True(cache.TryGet("/m/b.txt", new ModuleFingerprint(3, 1), out _, out var failure));
        Assert.Equal(new ScanFailure(ScanFailureKind.Malformed, "nope"), failure);
        Assert.Equal([new QuarantineEntry("/m/crash.clap", ScanFailureKind.Crashed, "boom", new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(-6)))], quarantine.Entries);

        cache.Save();
        quarantine.Save();
        Assert.Equal(Normalize(cacheBefore), Normalize(File.ReadAllText(CachePath)));
        Assert.Equal(Normalize(quarantineBefore), Normalize(File.ReadAllText(quarantinePath)));

        static string Normalize(string json) => json.ReplaceLineEndings("\n").Trim();
    }

    [Fact]
    public void TheQuarantineList_Persists()
    {
        var path = _directory.File(PluginScanner.QuarantineFileName);
        var quarantine = ScanQuarantine.Load(path);
        var entry = new QuarantineEntry("/m/crash.bluestone-reference-plugin", ScanFailureKind.Crashed, "boom", DateTimeOffset.UnixEpoch);
        quarantine.Add(entry);
        quarantine.Save();

        var loaded = ScanQuarantine.Load(path);

        Assert.True(loaded.Contains(entry.ModulePath));
        Assert.Equal([entry], loaded.Entries);
        Assert.True(loaded.Remove(entry.ModulePath));
        loaded.Save();
        Assert.Empty(ScanQuarantine.Load(path).Entries);
    }

    [Fact]
    public void ACorruptQuarantineList_LoadsEmpty_WithADiagnostic()
    {
        File.WriteAllText(_directory.File(PluginScanner.QuarantineFileName), "[1, 2");

        using var scanner = new PluginScanner(new PluginScannerOptions(_directory.Path));

        Assert.Empty(scanner.Quarantined);
        Assert.Single(scanner.Diagnostics);
    }

    [Fact]
    public async Task AQuarantinedModule_IsSkippedWithoutLaunchingAScanner()
    {
        var module = _directory.File("crash.bluestone-reference-plugin");
        File.WriteAllText(module, "x");
        var quarantine = ScanQuarantine.Load(_directory.File(PluginScanner.QuarantineFileName));
        quarantine.Add(new QuarantineEntry(module, ScanFailureKind.Crashed, "boom", DateTimeOffset.UnixEpoch));
        quarantine.Save();
        using var scanner = new PluginScanner(new PluginScannerOptions(_directory.Path) { WorkerPath = _directory.File("no-such-worker") });

        var result = await scanner.ScanAsync([module], TestContext.Current.CancellationToken);

        Assert.Equal(ModuleScanStatus.SkippedQuarantined, result[0].Status);
        Assert.Equal(0, scanner.ScannerLaunchCount);
    }

    [Fact]
    public async Task AScannerThatCannotStart_FailsTheModule_WithoutQuarantiningIt()
    {
        var module = _directory.File("good.bluestone-reference-plugin");
        File.WriteAllText(module, "{}");
        using var scanner = new PluginScanner(new PluginScannerOptions(_directory.Path) { WorkerPath = _directory.File("no-such-worker") });

        var result = await scanner.ScanAsync([module], TestContext.Current.CancellationToken);

        Assert.Equal(ModuleScanStatus.Failed, result[0].Status);
        Assert.Empty(scanner.Quarantined);
    }

    [Fact]
    public void Fingerprint_OfABundle_ChangesWhenItsBinaryChanges()
    {
        using var folder = new TestDirectory();
        var binary = Path.Combine(folder.Path, "Synth.vst3", "Contents", "MacOS", "Synth");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllBytes(binary, [1, 2, 3]);
        var before = ModuleFingerprint.Of(Path.Combine(folder.Path, "Synth.vst3"));

        File.WriteAllBytes(binary, [1, 2, 3, 4]);
        var after = ModuleFingerprint.Of(Path.Combine(folder.Path, "Synth.vst3"));

        Assert.NotNull(before);
        Assert.Equal(3, before.Value.Size);
        Assert.NotEqual(before, after);
        Assert.Null(ModuleFingerprint.Of(Path.Combine(folder.Path, "Missing.vst3")));
    }
}
