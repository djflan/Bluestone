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
    public async Task PluginsFoundByARescan_JoinTheDeviceStrip_WithoutARestart()
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
        Assert.Contains(main.DeviceStrip.AvailableDevices, d => d.Name == "Bass");
        Assert.DoesNotContain(main.DeviceStrip.AvailableDevices, d => d.Name == "Kit");
        File.WriteAllText(_folder.File("drums.plugin"), "plugins: Kit");

        await main.Plugins!.RescanAllCommand.ExecuteAsync(null);

        Assert.Contains(main.DeviceStrip.AvailableDevices, d => d.Name == "Kit");
        Assert.Contains(main.DeviceStrip.AvailableDevices, d => d.Name == "Bass");
        Assert.Equal(0, manager.WorkerLaunchCount);
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
