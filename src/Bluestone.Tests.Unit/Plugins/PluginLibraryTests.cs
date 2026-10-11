using Bluestone.Application.Plugins;

namespace Bluestone.Tests.Unit.Plugins;

public sealed class PluginLibraryTests
{
    [Fact]
    public void FindModules_ListsTheFilesInEachFolderInOrder_LeavingOutHiddenFilesAndMissingFolders()
    {
        using var first = new TestDirectory();
        using var second = new TestDirectory();
        File.WriteAllText(first.File("b.plugin"), string.Empty);
        File.WriteAllText(first.File("a.plugin"), string.Empty);
        File.WriteAllText(first.File(".DS_Store"), string.Empty);
        File.WriteAllText(second.File("c.plugin"), string.Empty);
        using var library = new PluginLibrary(new FakePluginScanner(), [first.Path, Path.Combine(first.Path, "missing"), second.Path]);

        var modules = library.FindModules();

        Assert.Equal([first.File("a.plugin"), first.File("b.plugin"), second.File("c.plugin")], modules);
    }

    [Fact]
    public void FindModules_FindsVst3AndClapBundles_AndModulesInVendorSubfolders_WithoutLookingInsideBundles()
    {
        using var folder = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(folder.Path, "Synth.vst3", "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(folder.Path, "Synth.vst3", "Contents", "MacOS", "Synth"), string.Empty);
        Directory.CreateDirectory(Path.Combine(folder.Path, "Effects.clap", "Contents"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "Vendor", "Deeper"));
        File.WriteAllText(Path.Combine(folder.Path, "Vendor", "Legacy.vst3"), string.Empty);
        File.WriteAllText(Path.Combine(folder.Path, "Vendor", "readme.txt"), string.Empty);
        Directory.CreateDirectory(Path.Combine(folder.Path, "Vendor", "Deeper", "Nested.clap"));
        File.WriteAllText(folder.File("Top.clap"), string.Empty);
        using var library = new PluginLibrary(new FakePluginScanner(), [folder.Path]);

        var modules = library.FindModules();

        Assert.Equal(
            [folder.File("Effects.clap"), folder.File("Synth.vst3"), folder.File("Top.clap"), Path.Combine(folder.Path, "Vendor", "Deeper", "Nested.clap"), Path.Combine(folder.Path, "Vendor", "Legacy.vst3")],
            modules);
    }

    [Fact]
    public void StandardFolders_AreTheOperatingSystemsVst3AndClapFolders()
    {
        var folders = PluginLibrary.StandardFolders();

        Assert.Contains(folders, f => f.EndsWith("VST3", StringComparison.Ordinal) || f.EndsWith("vst3", StringComparison.Ordinal));
        Assert.Contains(folders, f => f.EndsWith("CLAP", StringComparison.Ordinal) || f.EndsWith("clap", StringComparison.Ordinal));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains("/Library/Audio/Plug-Ins/VST3", folders);
            Assert.Contains("/Library/Audio/Plug-Ins/CLAP", folders);
        }
    }

    [Fact]
    public async Task Scan_SummarizesEachModule_AndListsItsPluginsAsAvailable()
    {
        using var folder = new TestDirectory();
        File.WriteAllText(folder.File("synths.plugin"), "plugins: Bass, Lead");
        File.WriteAllText(folder.File("crash.plugin"), "crash");
        File.WriteAllText(folder.File("readme.txt"), "hello");
        using var library = new PluginLibrary(new FakePluginScanner(), [folder.Path]);
        var changes = 0;
        library.Changed += (_, _) => changes++;

        await library.ScanAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, changes);
        Assert.Equal(["Bass", "Lead"], library.Available.Select(p => p.DisplayName));
        var modules = library.Modules.ToDictionary(m => m.Name);
        Assert.Equal(PluginModuleState.Quarantined, modules["crash.plugin"].State);
        Assert.Contains("crashed the scanner", modules["crash.plugin"].Problem, StringComparison.Ordinal);
        Assert.Equal(PluginModuleState.Failed, modules["readme.txt"].State);
        Assert.Contains("Not a plugin module", modules["readme.txt"].Problem, StringComparison.Ordinal);
        Assert.Equal(PluginModuleState.Loaded, modules["synths.plugin"].State);
        Assert.Equal(["Instrument", "Instrument"], modules["synths.plugin"].Plugins.Select(p => p.Kind));
    }

    [Fact]
    public async Task Retry_ReadsAQuarantinedModuleAgain_AndRescanAllSkipsIt()
    {
        using var folder = new TestDirectory();
        File.WriteAllText(folder.File("crash.plugin"), "crash");
        var scanner = new FakePluginScanner();
        using var library = new PluginLibrary(scanner, [folder.Path]);
        var ct = TestContext.Current.CancellationToken;
        await library.ScanAsync(cancellationToken: ct);

        await library.RescanAllAsync(cancellationToken: ct);

        Assert.Equal(1, scanner.Reads);
        Assert.Equal(PluginModuleState.Quarantined, Assert.Single(library.Modules).State);

        File.WriteAllText(folder.File("crash.plugin"), "plugins: Fixed");
        var retried = await library.RetryAsync(folder.File("crash.plugin"), ct);

        Assert.Equal(2, scanner.Reads);
        Assert.Equal(PluginModuleState.Loaded, retried.State);
        Assert.Equal("Fixed", Assert.Single(library.Available).DisplayName);
        Assert.Empty(scanner.Quarantined);
    }
}
