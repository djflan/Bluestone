using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Plugins;
using Bluestone.Presentation;
using Bluestone.Profiles;
using Bluestone.Tests.Unit.Plugins;

namespace Bluestone.Tests.Unit.Presentation;

/// <summary>The plugins view with a scanner that launches no processes; the real scanner is covered by integration tests.</summary>
public sealed class PluginsViewModelTests : IDisposable
{
    private readonly TestDirectory _folder = new();
    private readonly FakePluginScanner _scanner = new();
    private readonly PluginLibrary _library;

    public PluginsViewModelTests()
    {
        File.WriteAllText(_folder.File("synths.plugin"), "plugins: Bass, Lead");
        File.WriteAllText(_folder.File("crash.plugin"), "crash");
        File.WriteAllText(_folder.File("readme.txt"), "hello");
        _library = new PluginLibrary(_scanner, [_folder.Path]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _library.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task TheView_ListsPlugins_ModulesThatFailed_AndQuarantinedModules()
    {
        await _library.ScanAsync(cancellationToken: Ct);

        var vm = new PluginsViewModel(_library, new ImmediateDispatcher());

        Assert.Equal([_folder.Path], vm.Folders);
        Assert.Equal(["Bass", "Lead"], vm.Plugins.Select(p => p.Name));
        Assert.Equal("Instrument · Bluestone · v1.0.0", vm.Plugins[0].Detail);
        Assert.Equal("synths.plugin", vm.Plugins[0].Module);
        Assert.Equal("readme.txt", Assert.Single(vm.Failures).Module);
        var quarantined = Assert.Single(vm.Quarantined);
        Assert.Equal("crash.plugin", quarantined.Module);
        Assert.Equal(_folder.File("crash.plugin"), quarantined.ModulePath);
        Assert.True(vm.HasFailures);
        Assert.True(vm.HasQuarantined);
        Assert.Equal("2 plugins available from 3 modules. 1 module could not be read. 1 module quarantined.", vm.StatusText);
    }

    [Fact]
    public void TheView_SaysSo_WhenTheFoldersHoldNoModules()
    {
        using var empty = new TestDirectory();
        using var library = new PluginLibrary(_scanner, [empty.Path]);

        var vm = new PluginsViewModel(library, new ImmediateDispatcher());

        Assert.Empty(vm.Plugins);
        Assert.False(vm.HasQuarantined);
        Assert.Equal("No plugin modules found in the plugin folders.", vm.StatusText);
    }

    [Fact]
    public async Task RescanAll_FindsANewModule_ShowsProgress_AndKeepsTheQuarantinedOneSkipped()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        var vm = new PluginsViewModel(_library, new ImmediateDispatcher());
        var progress = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PluginsViewModel.ProgressText) && vm.ProgressText.Length > 0)
            {
                progress.Add(vm.ProgressText);
            }
        };
        File.WriteAllText(_folder.File("drums.plugin"), "plugins: Kit");
        var reads = _scanner.Reads;

        await vm.RescanAllCommand.ExecuteAsync(null);

        Assert.Equal(["Kit", "Bass", "Lead"], vm.Plugins.Select(p => p.Name));
        Assert.Equal(3, _scanner.Reads - reads);
        Assert.Single(vm.Quarantined);
        Assert.Equal(["Scanning 1 of 4: crash.plugin", "Scanning 2 of 4: drums.plugin", "Scanning 3 of 4: readme.txt", "Scanning 4 of 4: synths.plugin"], progress);
        Assert.Equal("Rescanned. 3 plugins available from 4 modules. 1 module could not be read. 1 module quarantined.", vm.StatusText);
        Assert.False(vm.IsBusy);
        Assert.Equal(string.Empty, vm.ProgressText);
    }

    [Fact]
    public async Task Retry_TakesAModuleThatNowReadsOutOfQuarantine_AndListsItsPlugins()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        var vm = new PluginsViewModel(_library, new ImmediateDispatcher());
        File.WriteAllText(_folder.File("crash.plugin"), "plugins: Pad");

        await vm.RetryCommand.ExecuteAsync(vm.Quarantined[0]);

        Assert.Empty(vm.Quarantined);
        Assert.False(vm.HasQuarantined);
        Assert.Contains(vm.Plugins, p => p.Name == "Pad" && p.Module == "crash.plugin");
        Assert.Equal("crash.plugin was read: 1 plugin available.", vm.StatusText);
    }

    [Fact]
    public async Task Retry_KeepsAModuleThatCrashesAgainInQuarantine_AndSaysSo()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        var vm = new PluginsViewModel(_library, new ImmediateDispatcher());

        await vm.RetryCommand.ExecuteAsync(vm.Quarantined[0]);

        Assert.Single(vm.Quarantined);
        Assert.StartsWith("crash.plugin is still quarantined. It crashed the scanner", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhileAScanRuns_ItsCommandsAreDisabled_AndItReportsProgress()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        var vm = new PluginsViewModel(_library, new ImmediateDispatcher());
        _scanner.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var scan = vm.RescanAllCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.RescanAllCommand.CanExecute(null));
        Assert.False(vm.RetryCommand.CanExecute(vm.Quarantined[0]));
        // The quarantined module is skipped without a read; the scan waits in the next one.
        Assert.Equal("Scanning 2 of 3: readme.txt", vm.ProgressText);
        _scanner.Hold.SetResult();
        await scan;
        Assert.True(vm.RescanAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task PluginsFoundByARescan_JoinTheBrowser_WithoutARestart()
    {
        using var data = new TestDirectory();
        await _library.ScanAsync(cancellationToken: Ct);
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        using var endpoints = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Keys", TrackRole.Instrument)));
        await using var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = data.Path });
        var bridge = new PluginDeviceHost(manager, session, _library);
        var dispatcher = new ImmediateDispatcher();
        var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, clock, startThread: false);
        await using var main = new MainViewModel(session, playback, endpoints, new NoUi(), dispatcher, plugins: bridge) { Plugins = new PluginsViewModel(_library, dispatcher) };
        await main.InitializeAsync();
        main.Select(main.Tracks[0]);
        Assert.Contains(main.Browser.Items, d => d.Name == "Bass");
        Assert.DoesNotContain(main.Browser.Items, d => d.Name == "Kit");
        File.WriteAllText(_folder.File("drums.plugin"), "plugins: Kit");

        await main.Plugins!.RescanAllCommand.ExecuteAsync(null);

        Assert.Contains(main.Browser.Items, d => d.Name == "Kit");
        Assert.Contains(main.Browser.Items, d => d.Name == "Bass");
        Assert.Equal(0, manager.WorkerLaunchCount);
    }

    [Fact]
    public async Task WithoutTheDeviceHost_TheViewHasNoHostingSection()
    {
        await _library.ScanAsync(cancellationToken: Ct);

        var vm = new PluginsViewModel(_library, new ImmediateDispatcher());

        Assert.False(vm.HasHosting);
        Assert.Null(vm.GlobalHosting);
        Assert.All(vm.Plugins, p => Assert.Null(p.Hosting));
    }

    [Fact]
    public async Task TheHostingSection_ShowsTheSavedSettings_ByPluginAndNoAutomaticRestartByDefault()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        await using var hosting = await HostingAsync();

        var vm = hosting.ViewModel;

        Assert.True(vm.HasHosting);
        Assert.Equal(["Together", "By manufacturer", "By plugin", "Individually"], vm.HostingModes.Select(m => m.Name));
        Assert.Equal(HostingMode.ByPlugin, vm.GlobalHosting!.Mode);
        Assert.Equal("One process per plugin. A crash takes down every copy of that plugin.", vm.GlobalHostingText);
        Assert.False(vm.AutoRestart);
        Assert.Equal(["Global (By plugin)", "Together", "By manufacturer", "By plugin", "Individually"], vm.PluginHostingChoices.Select(c => c.Name));
        Assert.All(vm.Plugins, p => Assert.Null(p.Hosting!.Mode));
        Assert.All(vm.Plugins, p => Assert.False(p.HasCrashed));
    }

    [Fact]
    public async Task ChoosingTheGlobalModeAndAutomaticRestart_SavesThem_AndAppliesThemToTheDeviceHost()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        await using var hosting = await HostingAsync();
        var vm = hosting.ViewModel;

        vm.GlobalHosting = vm.HostingModes.Single(m => m.Mode == HostingMode.Together);
        await WaitIdleAsync(vm);
        vm.AutoRestart = true;
        await WaitIdleAsync(vm);

        Assert.Equal(new PluginHostingSettings { Mode = HostingMode.Together, AutoRestart = true }, hosting.Bridge.Hosting);
        Assert.Equal(hosting.Bridge.Hosting, hosting.Store.Load());
        Assert.Equal("Crashed plugins now restart by themselves, up to 3 times in 5 minutes.", vm.StatusText);
        Assert.Equal("Global (Together)", vm.PluginHostingChoices[0].Name);
        Assert.Equal("Global (Together)", vm.Plugins[0].Hosting!.Name);
    }

    [Fact]
    public async Task FloatingEditorsFollowTheSelection_ByDefault_AndTurningItOff_IsSaved()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        await using var hosting = await HostingAsync();
        var vm = hosting.ViewModel;
        Assert.True(vm.EditorsFollowSelection);
        var changed = 0;
        hosting.Bridge.HostingChanged += (_, _) => changed++;

        vm.EditorsFollowSelection = false;
        await WaitIdleAsync(vm);

        Assert.False(hosting.Bridge.Hosting.EditorsFollowSelection);
        Assert.False(hosting.Bridge.EditorsFollowSelection);
        Assert.False(hosting.Store.Load().EditorsFollowSelection);
        Assert.Equal(1, changed);
        Assert.Equal("Plugin editors now stay open until closed.", vm.StatusText);
    }

    [Fact]
    public async Task ChoosingAModeForOnePlugin_OverridesTheGlobalMode_AndGlobalClearsItAgain()
    {
        await _library.ScanAsync(cancellationToken: Ct);
        await using var hosting = await HostingAsync();
        var vm = hosting.ViewModel;
        var bass = vm.Plugins.Single(p => p.Name == "Bass");

        bass.Hosting = vm.PluginHostingChoices.Single(c => c.Mode == HostingMode.Individually);
        await WaitIdleAsync(vm);

        Assert.Equal(HostingMode.Individually, hosting.Bridge.Hosting.ModeFor(bass.Definition));
        Assert.Equal(HostingMode.ByPlugin, hosting.Bridge.Hosting.ModeFor(vm.Plugins.Single(p => p.Name == "Lead").Definition));
        Assert.Equal(HostingMode.Individually, hosting.Store.Load().Overrides[bass.Definition.Value]);
        Assert.Equal("Bass is now hosted individually.", vm.StatusText);

        bass.Hosting = vm.PluginHostingChoices[0];
        await WaitIdleAsync(vm);

        Assert.Empty(hosting.Store.Load().Overrides);
        Assert.Equal("Bass is now hosted as the global mode says.", vm.StatusText);
    }

    [Theory]
    [InlineData(1, 0, "Crashed once, hosted alone.")]
    [InlineData(3, 3, "Crashed 3 times, each time sharing a process with other plugins (any of them may have caused it).")]
    [InlineData(3, 1, "Crashed 3 times, 1 of them sharing a process with other plugins.")]
    public void CrashText_SaysHowOftenAndWhetherThePluginWasAlone(int crashes, int shared, string expected) =>
        Assert.Equal(expected, PluginsViewModel.CrashTextOf(new PluginCrashCount(crashes, shared)));

    private static async Task WaitIdleAsync(PluginsViewModel vm)
    {
        for (var i = 0; vm.IsBusy && i < 500; i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.False(vm.IsBusy);
    }

    private async Task<HostingFixture> HostingAsync()
    {
        var data = new TestDirectory();
        var manager = new PluginHostManager(new PluginHostOptions { DataDirectory = data.File("data") });
        var store = new PluginHostingSettingsStore(data.File("plugin-hosting.json"));
        var bridge = new PluginDeviceHost(manager, new ProjectSession(), _library, hosting: store.Load());
        await bridge.SyncAsync(new ProjectSession().Project, Ct);
        return new HostingFixture(data, manager, bridge, store, new PluginsViewModel(_library, new ImmediateDispatcher(), bridge, store));
    }

    private sealed record HostingFixture(TestDirectory Data, PluginHostManager Manager, PluginDeviceHost Bridge, PluginHostingSettingsStore Store, PluginsViewModel ViewModel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            await Manager.DisposeAsync();
            Assert.Equal(0, Manager.WorkerLaunchCount);
            Data.Dispose();
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
