using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Plugins;
using Bluestone.Plugins.Scanning;
using Bluestone.Presentation;
using Bluestone.Profiles;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// Scenario 11 as the musician sees it: a plugin added from the device strip runs in a real worker; when the
/// worker is killed, Bluestone keeps running, the strip shows the device as crashed with a restart action, and
/// restarting brings it back. Nothing is mocked.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginDeviceStripTests
{
    [Fact]
    public async Task AKilledPluginWorker_ShowsAsCrashedInTheStrip_AndCanBeRestarted()
    {
        await using var host = new PluginTestHost();
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        using var endpoints = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Vocals", TrackRole.Audio)));
        var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: 64);
        var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, clock, startThread: false);
        var vm = new MainViewModel(session, playback, endpoints, new NoUi(), new Immediate(), plugins: bridge);
        try
        {
            await vm.InitializeAsync();
            var strip = vm.DeviceView!;
            Assert.True(vm.Browser.InsertIntoActive(vm.Browser.Items.Single(d => d.Name == "Reference Gain")));
            await WaitUntilAsync(() => strip.Devices.Count == 1 && strip.Devices[0].Status == "Running", StatusTimeout, "the plugin to run");
            var device = strip.Devices[0];
            var instance = bridge.InstanceOf(device.Id)!;
            host.Track(instance);
            Assert.Equal("Reference Gain", device.Title);

            KillProcess(instance.WorkerProcessId!.Value);
            await WaitUntilAsync(() => device.Status == "Crashed", StatusTimeout, "the strip to show the crash");

            Assert.Equal("[Reference Gain: Crashed]", device.Title);
            Assert.True(device.NeedsAttention);
            Assert.True(device.CanRestart);
            Assert.Single(session.Project.ChainOf(session.Project.Sequence.Tracks[0].Id)!.Devices);

            await device.RestartCommand.ExecuteAsync(null);
            host.Track(instance);
            await WaitUntilAsync(() => device.Status == "Running", StatusTimeout, "the strip to show the restart");
            Assert.False(device.CanRestart);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASharedWorkerCrash_IsShownInTheStripAndThePluginsView_AndHostIndividuallyGivesEachDeviceItsOwnProcess()
    {
        await using var host = new PluginTestHost();
        var folder = Path.Combine(host.Directory, "plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "reference.bluestone-reference-plugin"), GainManifest);
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "cache")) { ScanTimeout = TimeSpan.FromSeconds(30) });
        using var library = new PluginLibrary(scanner, [folder]);
        await library.ScanAsync(cancellationToken: Ct);
        var store = new PluginHostingSettingsStore(Path.Combine(host.Directory, "plugin-hosting.json"));
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        using var endpoints = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Vocals", TrackRole.Audio)));
        var bridge = new PluginDeviceHost(host.Manager, session, library, maxBlockFrames: 64, hosting: store.Load());
        var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, clock, startThread: false);
        var dispatcher = new Immediate();
        var vm = new MainViewModel(session, playback, endpoints, new NoUi(), dispatcher, plugins: bridge) { Plugins = new PluginsViewModel(library, dispatcher, bridge, store) };
        try
        {
            await vm.InitializeAsync();
            var strip = vm.DeviceView!;
            foreach (var name in new[] { "Lead Gain", "Echo Gain" })
            {
                Assert.True(vm.Browser.InsertIntoActive(vm.Browser.Items.Single(d => d.Name == "Reference Gain")));
                var added = session.Project.ChainOf(session.Project.Sequence.Tracks[0].Id)!.Devices[^1];
                session.Execute(DeviceCommands.RenameDevice(added.Id, name));
            }

            await WaitUntilAsync(() => strip.Devices.Count == 2 && strip.Devices.All(d => d.Status == "Running"), StatusTimeout, "both plugins to run");
            var (lead, echo) = (strip.Devices[0], strip.Devices[1]);
            var instances = new[] { bridge.InstanceOf(lead.Id)!, bridge.InstanceOf(echo.Id)! };
            Assert.Equal(instances[0].WorkerProcessId, instances[1].WorkerProcessId);
            Assert.Equal("Hosted by plugin (bluestone.reference) · shares its process with Echo Gain", lead.Hosting);

            KillProcess(instances[0].WorkerProcessId!.Value);
            await WaitUntilAsync(() => lead.Status == "Crashed" && echo.Status == "Crashed", StatusTimeout, "the strip to show both crashed");
            await WaitUntilAsync(() => vm.Plugins!.Plugins[0].HasCrashed, StatusTimeout, "the plugins view to count the crash");

            Assert.Equal("Echo Gain went down with it: they shared its worker process. Restart starts them all again.", lead.Detail);
            Assert.Contains(vm.Messages, m => m.Text.StartsWith("The plugin process shared by ", StringComparison.Ordinal) && m.Text.Contains("all 2 went down together", StringComparison.Ordinal));
            var row = vm.Plugins!.Plugins.Single(p => p.Name == "Reference Gain");
            Assert.Equal("Crashed once, hosted alone.", row.CrashText);
            Assert.True(row.CanHostIndividually);

            await row.HostIndividuallyCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => !vm.Plugins.IsBusy && lead.Status == "Running" && echo.Status == "Running", StatusTimeout, "both plugins to run individually");
            host.Track(instances[0]);
            host.Track(instances[1]);

            Assert.NotEqual(instances[0].WorkerProcessId, instances[1].WorkerProcessId);
            Assert.Equal("Hosted individually, set for this plugin", lead.Hosting);
            Assert.False(lead.HasDetail);
            Assert.Equal(HostingMode.Individually, store.Load().ModeFor(row.Definition));
            Assert.False(row.CanHostIndividually);
            Assert.Equal("Individually", row.Hosting!.Name);
            Assert.Equal("Reference Gain is now hosted individually. 2 devices restarted in a new process, with their state.", vm.Plugins.StatusText);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    private const string GainManifest = """
        {
          "format": "bluestone-reference-plugin",
          "version": 1,
          "moduleId": "bluestone.reference",
          "plugins": [
            { "pluginId": "reference.gain", "name": "Reference Gain", "vendor": "Bluestone", "kind": "AudioEffect", "version": "1.0.0" }
          ]
        }
        """;

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
