using Bluestone.Plugins;
using Bluestone.Plugins.Scanning;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// Plugin editors in their worker's window, with installed plugins that have one, on a Mac with a desktop session
/// (skipped elsewhere and on CI, whose runners have no plugins and no display). The editor opens and closes on request;
/// when its worker is killed the window goes with the process, the instance reports a crash, and after a restart the
/// editor opens again.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginEditorTests
{
    public static TheoryData<string, string, string> WithEditors()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new()
        {
            { "vst3", "/Library/Audio/Plug-Ins/VST3/DPA_Reverside.vst3", "REVERSIDE" },
            { "clap", Path.Combine(home, "Library/Audio/Plug-Ins/CLAP/Airwindows Consolidated.clap"), "Airwindows Consolidated" },
        };
    }

    [Theory]
    [MemberData(nameof(WithEditors))]
    public async Task AnEditor_OpensAndCloses_DiesWithItsWorker_AndOpensAgainAfterARestart(string format, string modulePath, string name)
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Editor windows are verified on macOS only.");
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") == "true", "CI runners have no plugins and no desktop session.");
        Assert.SkipUnless(Directory.Exists(modulePath), $"{name} ({format}) is not installed at {modulePath}.");
        await using var host = new PluginTestHost();
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "scan")) { ScanTimeout = TimeSpan.FromSeconds(60) });
        var plugin = (await scanner.RescanAsync(modulePath, ignoreQuarantine: true, Ct)).Plugins.Single(p => p.DisplayName == name);
        var instance = await host.CreateRunningAsync(new PluginInstanceRequest(plugin, 48_000, 256, 2, 2) { ModulePath = modulePath });

        await instance.OpenEditorAsync(name, Ct);
        Assert.True(instance.IsEditorOpen);
        await instance.CloseEditorAsync(Ct);
        Assert.False(instance.IsEditorOpen);
        await instance.OpenEditorAsync(name, Ct);
        Assert.True(instance.IsEditorOpen);

        KillProcess(instance.WorkerProcessId!.Value);
        await WaitForStateAsync(instance, PluginInstanceState.Unavailable);
        Assert.Equal(UnavailableReason.Crashed, instance.Status.Reason);
        Assert.False(instance.IsEditorOpen);

        Assert.Equal(PluginInstanceState.Running, (await instance.RestartAsync(Ct)).State);
        host.Track(instance);
        Assert.False(instance.IsEditorOpen);
        await instance.OpenEditorAsync(name, Ct);
        Assert.True(instance.IsEditorOpen);
        await instance.CloseEditorAsync(Ct);
    }
}
