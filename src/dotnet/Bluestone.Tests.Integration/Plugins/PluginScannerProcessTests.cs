using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Scanning;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>Scanning in separate scanner processes (epic scenario 15). The crash is a real <c>Environment.FailFast</c> in the scanner.</summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginScannerProcessTests : IDisposable
{
    private const string GoodManifest = """
        {
          "format": "bluestone-reference-plugin",
          "version": 1,
          "moduleId": "bluestone.reference",
          "plugins": [
            { "pluginId": "reference.gain", "name": "Reference Gain", "vendor": "Bluestone", "kind": "AudioEffect", "version": "1.0.0" },
            { "pluginId": "reference.sine", "name": "Reference Sine", "vendor": "Bluestone", "kind": "Instrument", "version": "1.0.0" }
          ]
        }
        """;

    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "plugin-test-files", Guid.NewGuid().ToString("N"));

    public PluginScannerProcessTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "modules"));
        Directory.CreateDirectory(Path.Combine(_directory, "cache"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ACrashingModule_IsQuarantined_SkippedNextTime_AndRetriedOnRequest_WhileOthersStillScan()
    {
        var good = Module("good.bluestone-reference-plugin", GoodManifest);
        var crash = Module("crash.bluestone-reference-plugin", "#bluestone-test: crash-scanner\n{}");
        var text = Module("readme.txt", "not a plugin");
        var broken = Module("broken.bluestone-reference-plugin", "{ \"format\": ");
        string[] all = [good, crash, text, broken];
        using var scanner = Scanner();

        var first = await scanner.ScanAsync(all, Ct);

        Assert.Equal(4, scanner.ScannerLaunchCount);
        Assert.Equal(ModuleScanStatus.Scanned, first[0].Status);
        Assert.Equal(["reference.gain", "reference.sine"], first[0].Plugins.Select(p => p.PluginId));
        Assert.Equal(ModuleScanStatus.Failed, first[1].Status);
        Assert.Equal(ScanFailureKind.Crashed, first[1].Failure!.Kind);
        Assert.Equal(ScanFailureKind.Malformed, first[2].Failure!.Kind);
        Assert.Equal(ScanFailureKind.Malformed, first[3].Failure!.Kind);
        Assert.Equal([crash], scanner.Quarantined.Select(q => q.ModulePath));

        using var later = Scanner();
        var second = await later.ScanAsync(all, Ct);

        Assert.Equal(0, later.ScannerLaunchCount);
        Assert.Equal(
            [ModuleScanStatus.Cached, ModuleScanStatus.SkippedQuarantined, ModuleScanStatus.Cached, ModuleScanStatus.Cached],
            second.Select(r => r.Status));
        Assert.Equal(["reference.gain", "reference.sine"], second[0].Plugins.Select(p => p.PluginId));

        var retried = await later.RescanAsync(crash, ignoreQuarantine: true, Ct);

        Assert.Equal(1, later.ScannerLaunchCount);
        Assert.Equal(ScanFailureKind.Crashed, retried.Failure!.Kind);
        Assert.Single(later.Quarantined);

        File.WriteAllText(crash, GoodManifest);
        Assert.True(later.ClearQuarantine(crash));
        var fixedModule = await later.ScanAsync([crash], Ct);

        Assert.Equal(ModuleScanStatus.Scanned, fixedModule[0].Status);
        Assert.Empty(later.Quarantined);
    }

    [Fact]
    public async Task AHangingModule_TimesOut_AndIsQuarantined()
    {
        var hang = Module("hang.bluestone-reference-plugin", "#bluestone-test: hang-scanner\n");
        using var scanner = Scanner(TimeSpan.FromSeconds(5));

        var result = await scanner.ScanAsync([hang], Ct);

        Assert.Equal(ScanFailureKind.TimedOut, result[0].Failure!.Kind);
        Assert.Equal(ScanFailureKind.TimedOut, Assert.Single(scanner.Quarantined).Reason);
    }

    [Fact]
    public async Task AChangedModule_IsScannedAgain()
    {
        var module = Module("good.bluestone-reference-plugin", GoodManifest);
        using var scanner = Scanner();
        await scanner.ScanAsync([module], Ct);

        File.WriteAllText(module, GoodManifest.Replace("\"version\": \"1.0.0\" }\n  ]", "\"version\": \"1.0.0\" }]", StringComparison.Ordinal) + " ");
        File.SetLastWriteTimeUtc(module, DateTime.UtcNow.AddMinutes(1));
        var again = await scanner.ScanAsync([module], Ct);

        Assert.Equal(2, scanner.ScannerLaunchCount);
        Assert.Equal(ModuleScanStatus.Scanned, again[0].Status);
    }

    private PluginScanner Scanner(TimeSpan? timeout = null) =>
        new(new PluginScannerOptions(Path.Combine(_directory, "cache")) { ScanTimeout = timeout ?? TimeSpan.FromSeconds(30) });

    private string Module(string name, string content)
    {
        var path = Path.Combine(_directory, "modules", name);
        File.WriteAllText(path, content);
        return path;
    }
}
