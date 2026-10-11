using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Sequencing;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Scanning;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;
using ParameterValue = Bluestone.Plugins.Protocol.ParameterValue;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// Hosting modes (ADR 0034) with real worker processes: which plugins share a process in each mode, a shared process
/// crashing takes exactly its plugins down together, a mode changed at run time moves plugins to new processes with
/// their state, a plugin's own mode wins over the global one, and crashes are counted per plugin. Reference plugins and
/// Bluestone's Rust VST3 and CLAP test plugins; processes are killed for real.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class HostingModeProcessTests
{
    private const int Frames = 64;
    private const uint GainParameter = 0;

    private const string TwoVendorsManifest = """
        {
          "format": "bluestone-reference-plugin",
          "version": 1,
          "moduleId": "bluestone.reference",
          "plugins": [
            { "pluginId": "reference.gain", "name": "Acme Gain", "vendor": "Acme", "kind": "AudioEffect", "version": "1.0.0" },
            { "pluginId": "reference.sine", "name": "Zenith Sine", "vendor": "Zenith", "kind": "Instrument", "version": "1.0.0" }
          ]
        }
        """;

    private static PluginInstanceRequest GainRequest(HostingMode mode) => new(Gain, 48_000, Frames, 1, 1) { Hosting = mode };

    private static PluginInstanceRequest SineRequest(HostingMode mode) => new(Sine, 48_000, Frames, 0, 1) { Hosting = mode };

    private static float[] Run(PluginInstance instance)
    {
        var driver = new BlockDriver(instance, Frames);
        Array.Fill(driver.Input, 1.0f);
        for (var i = 0; i < 4 && driver.Process().Outcome != ProcessOutcome.Processed; i++)
        {
        }

        driver.Process();
        return driver.Output;
    }

    /// <summary>A project with one track per plugin, each with that plugin as its only device.</summary>
    private static (ProjectSession Session, DeviceInstance[] Devices) ProjectWith(params (string Track, PluginIdentity Plugin)[] plugins)
    {
        var session = new ProjectSession();
        var project = session.Project;
        var devices = new List<DeviceInstance>();
        foreach (var (name, plugin) in plugins)
        {
            var track = Track.Create(name, plugin.Kind == PluginKind.AudioEffect ? TrackRole.Audio : TrackRole.Instrument);
            var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(plugin).ToReference()) with { Name = name };
            project = (project with { Sequence = project.Sequence.WithTrack(track) }).WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [device] });
            devices.Add(device);
        }

        session.Execute(new ProjectCommand("Set up", _ => project));
        return (session, [.. devices]);
    }

    private static PluginInstance Running(PluginDeviceHost bridge, PluginTestHost host, DeviceInstance device)
    {
        var instance = bridge.InstanceOf(device.Id)!;
        Assert.True(instance.Status.State == PluginInstanceState.Running, $"{device.DisplayName} is {instance.Status}: {instance.Status.Message}");
        host.Track(instance);
        return instance;
    }

    [Fact]
    public async Task Together_TwoPluginsShareOneProcess_AndACrashTakesBothDownTogether_AndNothingElse()
    {
        await using var host = new PluginTestHost();
        var losses = new List<WorkerLoss>();
        host.Manager.WorkerLost += (_, loss) =>
        {
            lock (losses)
            {
                losses.Add(loss);
            }
        };
        var gain = await host.CreateRunningAsync(GainRequest(HostingMode.Together));
        var sine = await host.CreateRunningAsync(SineRequest(HostingMode.Together));
        var alone = await host.CreateRunningAsync(GainRequest(HostingMode.Individually));

        Assert.Equal(gain.WorkerProcessId, sine.WorkerProcessId);
        Assert.Equal(gain.WorkerKey, sine.WorkerKey);
        Assert.NotEqual(gain.WorkerProcessId, alone.WorkerProcessId);
        Assert.Equal(2, host.Manager.WorkerLaunchCount);

        var pid = gain.WorkerProcessId!.Value;
        KillProcess(pid);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        await WaitForStateAsync(sine, PluginInstanceState.Unavailable);
        await WaitUntilAsync(() => losses.Count == 1, StatusTimeout, "the worker loss to be reported");

        Assert.Equal(PluginInstanceState.Running, alone.Status.State);
        Assert.All(Run(alone), s => Assert.Equal(1.0f, s, 4));
        var loss = Assert.Single(losses);
        Assert.Equal(pid, loss.ProcessId);
        Assert.Equal(UnavailableReason.Crashed, loss.Reason);
        Assert.Equal([gain, sine], loss.Instances.OrderBy(i => i == sine));
        Assert.Same(loss, gain.LastWorkerLoss);
        Assert.Same(loss, sine.LastWorkerLoss);
        Assert.Null(alone.LastWorkerLoss);
    }

    [Fact]
    public async Task Together_AVst3AndAClapPlugin_ShareOneProcess_AndCrashTogether()
    {
        await using var host = new PluginTestHost();
        var vst3 = await NativePluginTests.ScanAsync(host, "vst3");
        var clap = await NativePluginTests.ScanAsync(host, "clap");
        var gain = await host.CreateRunningAsync(NativePluginTests.Request(vst3["Test Gain"], "vst3", 2, 2) with { Hosting = HostingMode.Together });
        var sine = await host.CreateRunningAsync(NativePluginTests.Request(clap["Test Sine"], "clap", 0, 2) with { Hosting = HostingMode.Together });

        Assert.Equal(gain.WorkerProcessId, sine.WorkerProcessId);
        Assert.Equal(1, host.Manager.WorkerLaunchCount);

        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        await WaitForStateAsync(sine, PluginInstanceState.Unavailable);

        Assert.Equal(2, gain.LastWorkerLoss!.Instances.Length);
        Assert.Equal(PluginInstanceState.Running, (await gain.RestartAsync(Ct)).State);
        Assert.Equal(PluginInstanceState.Running, (await sine.RestartAsync(Ct)).State);
        Assert.Equal(gain.WorkerProcessId, sine.WorkerProcessId);
        host.Track(gain);
    }

    [Fact]
    public async Task Individually_GivesEachInstanceItsOwnProcess_SoACrashTakesDownOnlyOne()
    {
        await using var host = new PluginTestHost();
        var a = await host.CreateRunningAsync(GainRequest(HostingMode.Individually));
        var b = await host.CreateRunningAsync(GainRequest(HostingMode.Individually));

        Assert.NotEqual(a.WorkerProcessId, b.WorkerProcessId);

        KillProcess(a.WorkerProcessId!.Value);
        await WaitForStateAsync(a, PluginInstanceState.Unavailable);

        Assert.Equal(PluginInstanceState.Running, b.Status.State);
        Assert.Equal([a], a.LastWorkerLoss!.Instances);
    }

    [Fact]
    public async Task ChangingTheMode_MovesARunningInstanceToAnotherProcess_WithItsState_AndLeavesTheOthersRunning()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(GainRequest(HostingMode.Together));
        var sine = await host.CreateRunningAsync(SineRequest(HostingMode.Together));
        var shared = gain.WorkerProcessId!.Value;
        await gain.SetParameterAsync(GainParameter, 0.25, Ct);
        Assert.Null(gain.LastSnapshot);
        var statuses = new List<PluginInstanceState>();
        gain.StatusChanged += (_, e) => statuses.Add(e.Current.State);

        var status = await host.Manager.ChangeHostingAsync(gain, HostingMode.Individually, Ct);
        host.Track(gain);

        Assert.Equal(PluginInstanceState.Running, status.State);
        Assert.Equal([PluginInstanceState.Restarting, PluginInstanceState.Running], statuses);
        Assert.Equal(HostingMode.Individually, gain.Hosting);
        Assert.NotEqual(shared, gain.WorkerProcessId);
        Assert.Equal(shared, sine.WorkerProcessId);
        Assert.Equal(PluginInstanceState.Running, sine.Status.State);
        Assert.NotNull(gain.LastSnapshot);
        Assert.Contains(new ParameterValue(GainParameter, 0.25), await gain.GetParametersAsync(Ct));
        Assert.All(Run(gain), s => Assert.Equal(0.5f, s, 4));

        // Back into the shared process; with the same mode again nothing moves.
        await host.Manager.ChangeHostingAsync(gain, HostingMode.Together, Ct);
        Assert.Equal(shared, gain.WorkerProcessId);
        var launches = host.Manager.WorkerLaunchCount;
        await host.Manager.ChangeHostingAsync(gain, HostingMode.Together, Ct);
        Assert.Equal(launches, host.Manager.WorkerLaunchCount);
        Assert.All(Run(gain), s => Assert.Equal(0.5f, s, 4));
    }

    [Fact]
    public async Task MovingTheLastInstanceOutOfAProcess_StopsThatProcess()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(GainRequest(HostingMode.Individually));
        var old = gain.WorkerProcessId!.Value;

        await host.Manager.ChangeHostingAsync(gain, HostingMode.ByPlugin, Ct);
        host.Track(gain);

        Assert.NotEqual(old, gain.WorkerProcessId);
        await WaitUntilAsync(() => !IsRunning(old), TimeSpan.FromSeconds(10), "the old worker to exit");
    }

    [Theory]
    [MemberData(nameof(NativePluginTests.Formats), MemberType = typeof(NativePluginTests))]
    public async Task ChangingTheMode_OfACrashedNativePlugin_RestartsItInItsNewProcess_FromItsLastState(string format)
    {
        await using var host = new PluginTestHost();
        var plugins = await NativePluginTests.ScanAsync(host, format);
        var gain = await host.CreateRunningAsync(NativePluginTests.Request(plugins["Test Gain"], format, 2, 2) with { Hosting = HostingMode.ByPlugin });
        await gain.SetParameterAsync(GainParameter, 0.25, Ct);
        var snapshot = await gain.CaptureStateAsync(Ct);
        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);

        var status = await host.Manager.ChangeHostingAsync(gain, HostingMode.Individually, Ct);
        host.Track(gain);

        Assert.Equal(PluginInstanceState.Running, status.State);
        Assert.Same(snapshot, gain.LastSnapshot);
        Assert.Null(gain.LastWorkerLoss);
        Assert.Contains(new ParameterValue(GainParameter, 0.25), await gain.GetParametersAsync(Ct));
        Assert.All(Run(gain), s => Assert.Equal(0.5f, s, 4));
    }

    [Fact]
    public async Task ByManufacturer_SeparatesTheVendorsTheScanReported_AndKeepsOneVendorsVst3AndClapPluginsTogether()
    {
        await using var host = new PluginTestHost();
        var folder = Path.Combine(host.Directory, "plugins");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "vendors.bluestone-reference-plugin"), TwoVendorsManifest);
        using var scanner = new PluginScanner(new PluginScannerOptions(Path.Combine(host.Directory, "cache")) { ScanTimeout = TimeSpan.FromSeconds(30) });
        using var library = new PluginLibrary(scanner, [folder, Path.GetDirectoryName(NativePluginTests.ModulePath("vst3"))!]);
        await library.ScanAsync(cancellationToken: Ct);
        PluginIdentity Find(string name, string format) => library.Available.Single(p => p.DisplayName == name && p.Format == format);

        var (session, devices) = ProjectWith(
            ("Acme", Find("Acme Gain", "bluestone-reference")),
            ("Zenith", Find("Zenith Sine", "bluestone-reference")),
            ("Vst3 Gain", Find("Test Gain", "vst3")),
            ("Clap Sine", Find("Test Sine", "clap")));
        await using var bridge = new PluginDeviceHost(host.Manager, session, library, maxBlockFrames: Frames, hosting: new PluginHostingSettings { Mode = HostingMode.ByManufacturer });
        await bridge.SyncAsync(session.Project, Ct);
        var instances = devices.Select(d => Running(bridge, host, d)).ToArray();
        var (acme, zenith, vst3, clap) = (instances[0], instances[1], instances[2], instances[3]);

        Assert.Equal(vst3.WorkerProcessId, clap.WorkerProcessId);
        Assert.Equal(3, instances.Select(i => i.WorkerProcessId).Distinct().Count());
        Assert.Equal("Hosted by manufacturer (Bluestone) · shares its process with Clap Sine", bridge.StatusOf(devices[2])!.Hosting);
        Assert.Equal("Hosted by manufacturer (Acme)", bridge.StatusOf(devices[0])!.Hosting);

        KillProcess(vst3.WorkerProcessId!.Value);
        await WaitForStateAsync(vst3, PluginInstanceState.Unavailable);
        await WaitForStateAsync(clap, PluginInstanceState.Unavailable);

        Assert.Equal(PluginInstanceState.Running, acme.Status.State);
        Assert.Equal(PluginInstanceState.Running, zenith.Status.State);
    }

    [Fact]
    public async Task APluginsOwnMode_WinsOverTheGlobalMode()
    {
        await using var host = new PluginTestHost();
        var (session, devices) = ProjectWith(("Vocals", Gain), ("Guitar", Gain), ("Lead", Sine), ("Pad", Sine));
        var settings = new PluginHostingSettings { Mode = HostingMode.Together }.WithOverride(PluginDeviceHost.DefinitionIdOf(Gain), HostingMode.Individually);
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain, Sine], maxBlockFrames: Frames, hosting: settings);

        await bridge.SyncAsync(session.Project, Ct);
        var (vocals, guitar, lead, pad) = (Running(bridge, host, devices[0]), Running(bridge, host, devices[1]), Running(bridge, host, devices[2]), Running(bridge, host, devices[3]));

        Assert.Equal(HostingMode.Individually, vocals.Hosting);
        Assert.NotEqual(vocals.WorkerProcessId, guitar.WorkerProcessId);
        Assert.Equal(lead.WorkerProcessId, pad.WorkerProcessId);
        Assert.Equal(3, host.Manager.WorkerLaunchCount);
        Assert.Equal("Hosted individually, set for this plugin", bridge.StatusOf(devices[0])!.Hosting);
        Assert.Equal("Hosted together · shares its process with Pad", bridge.StatusOf(devices[2])!.Hosting);
    }

    [Fact]
    public async Task ApplyingNewSettings_MovesRunningDevicesIntoOneProcess_WithTheirState_AndTheProjectUntouched()
    {
        await using var host = new PluginTestHost();
        var (session, devices) = ProjectWith(("Vocals", Gain), ("Lead", Sine));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain, Sine], maxBlockFrames: Frames, hosting: new PluginHostingSettings { Mode = HostingMode.Individually });
        await bridge.SyncAsync(session.Project, Ct);
        var gain = Running(bridge, host, devices[0]);
        var sine = Running(bridge, host, devices[1]);
        Assert.NotEqual(gain.WorkerProcessId, sine.WorkerProcessId);
        await gain.SetParameterAsync(GainParameter, 0.25, Ct);
        var project = session.Project;

        var moved = await bridge.ApplyHostingAsync(new PluginHostingSettings { Mode = HostingMode.Together }, Ct);
        host.Track(gain);
        host.Track(sine);

        Assert.Equal([devices[0].Id, devices[1].Id], moved.OrderBy(d => d == devices[1].Id));
        Assert.Equal(gain.WorkerProcessId, sine.WorkerProcessId);
        Assert.Same(gain, bridge.InstanceOf(devices[0].Id));
        Assert.All(Run(gain), s => Assert.Equal(0.5f, s, 4));
        Assert.Equal(PluginInstanceState.Running, sine.Status.State);
        Assert.Same(project, session.Project);
        Assert.Equal(HostingMode.Together, bridge.Hosting.Mode);

        Assert.Empty(await bridge.ApplyHostingAsync(new PluginHostingSettings { Mode = HostingMode.Together }, Ct));
    }

    [Fact]
    public async Task ASharedProcessCrash_ShowsEveryDeviceCrashed_SaysWhichWentDownTogether_AndRestartBringsThemBackTogether()
    {
        await using var host = new PluginTestHost();
        var (session, devices) = ProjectWith(("Vocals", Gain), ("Guitar", Gain), ("Lead", Sine));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain, Sine], maxBlockFrames: Frames);
        var crashes = new List<PluginCrash>();
        bridge.Crashed += (_, crash) =>
        {
            lock (crashes)
            {
                crashes.Add(crash);
            }
        };
        await bridge.SyncAsync(session.Project, Ct);
        var (vocals, guitar, lead) = (Running(bridge, host, devices[0]), Running(bridge, host, devices[1]), Running(bridge, host, devices[2]));
        Assert.Equal(HostingMode.ByPlugin, vocals.Hosting);
        Assert.Equal(vocals.WorkerProcessId, guitar.WorkerProcessId);
        Assert.Equal(lead.WorkerProcessId, vocals.WorkerProcessId);
        Assert.Equal("Hosted by plugin (bluestone.reference) · shares its process with Guitar and Lead", bridge.StatusOf(devices[0])!.Hosting);

        KillProcess(vocals.WorkerProcessId!.Value);
        await WaitUntilAsync(() => crashes.Count == 1, StatusTimeout, "the crash to be reported");

        var crash = Assert.Single(crashes);
        Assert.Equal(devices.Select(d => d.Id).ToHashSet(), crash.Devices.ToHashSet());
        Assert.All(devices, d => Assert.Equal("Crashed", bridge.StatusOf(d)!.Text));
        Assert.Equal("Guitar and Lead went down with it: they shared its worker process. Restart starts them all again.", bridge.StatusOf(devices[0])!.Detail);
        var together = bridge.WentDownTogether(devices[1].Id);
        Assert.Equal(devices[1].Id, together[0]);
        Assert.Equal(devices.Select(d => d.Id).ToHashSet(), together.ToHashSet());
        Assert.Equal(new PluginCrashCount(1, 1), bridge.CrashCounts[PluginDeviceHost.DefinitionIdOf(Gain)]);
        Assert.Equal(new PluginCrashCount(1, 1), bridge.CrashCounts[PluginDeviceHost.DefinitionIdOf(Sine)]);

        Assert.True(await bridge.RestartAsync(devices[1].Id, Ct));
        host.Track(vocals);

        Assert.All(new[] { vocals, guitar, lead }, i => Assert.Equal(PluginInstanceState.Running, i.Status.State));
        Assert.Equal(vocals.WorkerProcessId, guitar.WorkerProcessId);
        Assert.Equal(vocals.WorkerProcessId, lead.WorkerProcessId);
        Assert.Null(bridge.StatusOf(devices[0])!.Detail);
    }

    [Fact]
    public async Task CrashCounts_SayWhetherAPluginCrashedAlone()
    {
        await using var host = new PluginTestHost();
        var (session, devices) = ProjectWith(("Vocals", Gain), ("Lead", Sine));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain, Sine], maxBlockFrames: Frames, hosting: new PluginHostingSettings { Mode = HostingMode.Together });
        await bridge.SyncAsync(session.Project, Ct);
        var gain = Running(bridge, host, devices[0]);
        KillProcess(gain.WorkerProcessId!.Value);
        await WaitUntilAsync(() => bridge.CrashCounts.Count == 2, StatusTimeout, "both plugins' crashes to be counted");

        await bridge.ApplyHostingAsync(bridge.Hosting.WithOverride(PluginDeviceHost.DefinitionIdOf(Gain), HostingMode.Individually), Ct);
        host.Track(gain);
        Assert.Equal(PluginInstanceState.Running, gain.Status.State);
        Assert.Equal(PluginInstanceState.Unavailable, bridge.InstanceOf(devices[1].Id)!.Status.State);
        KillProcess(gain.WorkerProcessId!.Value);
        await WaitUntilAsync(() => bridge.CrashCounts[PluginDeviceHost.DefinitionIdOf(Gain)].Crashes == 2, StatusTimeout, "the second crash to be counted");

        Assert.Equal(new PluginCrashCount(2, 1), bridge.CrashCounts[PluginDeviceHost.DefinitionIdOf(Gain)]);
        Assert.Equal(new PluginCrashCount(1, 1), bridge.CrashCounts[PluginDeviceHost.DefinitionIdOf(Sine)]);
        Assert.Null(bridge.StatusOf(devices[0])!.Detail);
    }

    [Fact]
    public async Task TurningOnAutomaticRestart_AppliesToRunningDevices()
    {
        await using var host = new PluginTestHost();
        var (session, devices) = ProjectWith(("Vocals", Gain));
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames);
        await bridge.SyncAsync(session.Project, Ct);
        var gain = Running(bridge, host, devices[0]);
        Assert.False(gain.RestartPolicy.AutoRestart);

        Assert.Empty(await bridge.ApplyHostingAsync(bridge.Hosting with { AutoRestart = true }, Ct));
        var pid = gain.WorkerProcessId!.Value;
        KillProcess(pid);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        await WaitForStateAsync(gain, PluginInstanceState.Running);
        host.Track(gain);

        Assert.True(gain.RestartPolicy.AutoRestart);
        Assert.NotEqual(pid, gain.WorkerProcessId);
    }
}
