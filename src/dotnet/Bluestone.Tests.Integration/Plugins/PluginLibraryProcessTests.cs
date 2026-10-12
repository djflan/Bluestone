using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Plugins.Scanning;
using Bluestone.Presentation;
using Bluestone.Profiles;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// The plugins view with real scanner processes: a module whose scanner crashes (a real <c>Environment.FailFast</c>)
/// shows as quarantined, a retry that crashes again keeps it there, and a retry after the module is fixed takes it out
/// and puts its plugin in the device strip, where it runs in a worker, without restarting Bluestone. Nothing is mocked.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginLibraryProcessTests
{
    private const string GoodManifest = """
        {
          "format": "bluestone-reference-plugin",
          "version": 1,
          "moduleId": "bluestone.reference",
          "plugins": [
            { "pluginId": "reference.gain", "name": "Reference Gain", "vendor": "Bluestone", "kind": "AudioEffect", "version": "1.0.0" }
          ]
        }
        """;

    private const string TransposeManifest = """
        {
          "format": "bluestone-reference-plugin",
          "version": 1,
          "moduleId": "bluestone.reference",
          "plugins": [
            { "pluginId": "reference.transpose", "name": "Reference Transpose", "vendor": "Bluestone", "kind": "MidiEffect", "version": "1.0.0" }
          ]
        }
        """;

    [Fact]
    public async Task AQuarantinedModule_ShowsInThePluginsView_AndRetryingItOnceFixed_PutsItsPluginInTheBrowser()
    {
        await using var host = new PluginTestHost();
        var folder = Path.Combine(host.Directory, "plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "good.bluestone-reference-plugin"), GoodManifest);
        var crash = Path.Combine(folder, "crash.bluestone-reference-plugin");
        File.WriteAllText(crash, "#bluestone-test: crash-scanner\n{}");
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "cache")) { ScanTimeout = TimeSpan.FromSeconds(30) });
        using var library = new PluginLibrary(scanner, [folder]);
        await library.ScanAsync(cancellationToken: Ct);

        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        using var endpoints = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Keys", TrackRole.Instrument)));
        var bridge = new PluginDeviceHost(host.Manager, session, library, maxBlockFrames: 64);
        var dispatcher = new Immediate();
        var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, clock, startThread: false);
        var vm = new MainViewModel(session, playback, endpoints, new NoUi(), dispatcher, plugins: bridge) { Plugins = new PluginsViewModel(library, dispatcher) };
        try
        {
            await vm.InitializeAsync();
            vm.Select(vm.Tracks[0]);
            var plugins = vm.Plugins;
            var browser = vm.Browser;

            Assert.Equal(2, scanner.ScannerLaunchCount);
            Assert.Equal(["Reference Gain"], plugins.Plugins.Select(p => p.Name));
            Assert.Equal("crash.bluestone-reference-plugin", Assert.Single(plugins.Quarantined).Module);
            Assert.Contains("crashed the scanner", plugins.Quarantined[0].Problem, StringComparison.Ordinal);
            Assert.DoesNotContain(browser.Items, d => d.Name == "Reference Transpose");

            await plugins.RetryCommand.ExecuteAsync(plugins.Quarantined[0]);

            Assert.Equal(3, scanner.ScannerLaunchCount);
            Assert.Single(plugins.Quarantined);
            Assert.StartsWith("crash.bluestone-reference-plugin is still quarantined.", plugins.StatusText, StringComparison.Ordinal);

            File.WriteAllText(crash, TransposeManifest);
            await plugins.RetryCommand.ExecuteAsync(plugins.Quarantined[0]);

            Assert.Empty(plugins.Quarantined);
            Assert.Empty(scanner.Quarantined);
            Assert.Equal("crash.bluestone-reference-plugin was read: 1 plugin available.", plugins.StatusText);
            Assert.Equal(["Reference Transpose", "Reference Gain"], plugins.Plugins.Select(p => p.Name));
            await WaitUntilAsync(() => browser.Items.Any(d => d.Name == "Reference Transpose"), StatusTimeout, "the browser to offer the new plugin");

            var strip = vm.DeviceView!;
            Assert.True(browser.InsertIntoActive(browser.Items.Single(d => d.Name == "Reference Transpose")));
            await WaitUntilAsync(() => strip.Devices.Count == 1 && strip.Devices[0].Status == "Running", StatusTimeout, "the new plugin to run");
            host.Track(bridge.InstanceOf(strip.Devices[0].Id)!);

            await plugins.RescanAllCommand.ExecuteAsync(null);

            Assert.Equal(6, scanner.ScannerLaunchCount);
            Assert.Equal("Rescanned. 2 plugins available from 2 modules.", plugins.StatusText);
            Assert.Equal("Running", strip.Devices[0].Status);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    private sealed class Immediate : IUiDispatcher
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
