using Bluestone.Application.Editing;
using Bluestone.Application.Plugins;
using Bluestone.Application.Sessions;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;
using Bluestone.Plugins;
using Bluestone.Signal;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// The project's plugin devices running in real worker processes through <see cref="PluginDeviceHost"/>:
/// acceptance scenarios 11 (crash isolation), 12 (state recovery), and 13 (several workers) at the level of
/// the project model. Workers are killed with <see cref="System.Diagnostics.Process.Kill()"/>; nothing is mocked.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginDeviceHostTests
{
    private const int Frames = 64;
    private static readonly ParameterId GainParameter = new(0);

    private static (ProjectSession Session, Track Track, DeviceInstance Device) ProjectWithGainOn(string trackName, ProjectSession? session = null)
    {
        session ??= new ProjectSession();
        var track = Track.Create(trackName, TrackRole.Audio);
        var device = DeviceInstance.Create(PluginDeviceHost.DefinitionOf(Gain).ToReference());
        var project = session.Project with { Sequence = session.Project.Sequence.WithTrack(track) };
        Set(session, project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [device] }));
        return (session, track, device);
    }

    private static void Set(ProjectSession session, Project project) => session.Execute(new ProjectCommand("Set up", _ => project));

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

    [Fact]
    public async Task Scenario12_APluginDevice_IsRestoredFromTheProjectsSavedState_AfterReopeningAndAfterARestart()
    {
        await using var host = new PluginTestHost();
        var (session, track, device) = ProjectWithGainOn("Vocals");
        var lane = new AutomationLane(AutomationLaneId.New(), AutomationTarget.ForDevice(device.Id, GainParameter), [new AutomationPoint(Tick.Zero, ControlValue.Max, AutomationCurve.Hold)]);
        Set(session, session.Project with { Sequence = session.Project.Sequence.WithTrack(session.Project.Sequence.FindTrack(track.Id)!.WithAutomation([lane])) });

        // Configure the running plugin, capture its state into the project, and save the project.
        byte[] saved;
        await using (var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames))
        {
            await bridge.SyncAsync(session.Project, Ct);
            var instance = bridge.InstanceOf(device.Id)!;
            host.Track(instance);
            Assert.Equal("Running", bridge.StatusOf(device)!.Text);
            await instance.SetParameterAsync(GainParameter.Value, 0.25, Ct);

            Assert.True(await bridge.CaptureStateAsync(device.Id, Ct));
            var stored = session.Project.FindDevice(device.Id)!.Value.Device;
            Assert.NotNull(stored.State);
            Assert.Equal(0.25, stored.ValueOf(GainParameter)!.Value.ToFraction(), 6);
            saved = ProjectSerializer.Default.Serialize(new ProjectDocument(session.Project));
        }

        // Reopen in a new session: the device's instance comes back with the saved configuration.
        var reopened = new ProjectSession();
        Set(reopened, ProjectSerializer.Default.Deserialize(saved).Project);
        await using var again = new PluginDeviceHost(host.Manager, reopened, [Gain], maxBlockFrames: Frames);
        await again.SyncAsync(reopened.Project, Ct);
        var restored = again.InstanceOf(device.Id)!;
        host.Track(restored);
        Assert.All(Run(restored), s => Assert.Equal(0.5f, s, 4));

        // Crash it: the device says so, the project is untouched, and a restart restores the same state.
        var before = reopened.Project;
        KillProcess(restored.WorkerProcessId!.Value);
        await WaitForStateAsync(restored, PluginInstanceState.Unavailable);
        var crashed = again.StatusOf(reopened.Project.FindDevice(device.Id)!.Value.Device)!;
        Assert.Equal(("Crashed", true, true), (crashed.Text, crashed.NeedsAttention, crashed.CanRestart));
        Assert.Same(before, reopened.Project);

        Assert.True(await again.RestartAsync(device.Id, Ct));
        host.Track(restored);
        Assert.All(Run(restored), s => Assert.Equal(0.5f, s, 4));

        // Automation still names the device and a parameter the plugin reports, and reaches it as a parameter feed.
        var catalog = again.Extend(DeviceCatalog.BuiltIn);
        Assert.DoesNotContain(SignalRoutingValidator.Validate(reopened.Project, catalog.Lookup), i => i.Code is RoutingIssueCode.AutomationDeviceMissing or RoutingIssueCode.AutomationParameterUnknown);
        Assert.Equal(device.Id, Assert.Single(SignalGraph.Evaluate(reopened.Project, catalog).Parameters).Device);
    }

    [Fact]
    public async Task Scenarios11And13_OneWorkerCrashing_LeavesBluestoneTheProjectAndTheOtherPluginsRunning()
    {
        await using var host = new PluginTestHost();
        var (session, _, first) = ProjectWithGainOn("Vocals");
        var (_, _, second) = ProjectWithGainOn("Guitar", session);
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames, hosting: new PluginHostingSettings { Mode = HostingMode.Individually });
        await bridge.SyncAsync(session.Project, Ct);
        var a = bridge.InstanceOf(first.Id)!;
        var b = bridge.InstanceOf(second.Id)!;
        host.Track(a);
        host.Track(b);
        Assert.NotEqual(a.WorkerProcessId, b.WorkerProcessId);
        var project = session.Project;

        KillProcess(a.WorkerProcessId!.Value);
        await WaitForStateAsync(a, PluginInstanceState.Unavailable);

        Assert.Equal("Crashed", bridge.StatusOf(first)!.Text);
        Assert.Equal("Running", bridge.StatusOf(second)!.Text);
        Assert.All(Run(a), s => Assert.Equal(0.0f, s));
        Assert.All(Run(b), s => Assert.Equal(1.0f, s, 4));
        Assert.Same(project, session.Project);
        Assert.False(await bridge.CaptureStateAsync(first.Id, Ct));
    }

    [Fact]
    public async Task ARateChange_RecreatesTheInstanceAtTheNewRate_FromItsLiveState_WithoutTouchingTheProject()
    {
        await using var host = new PluginTestHost();
        var (session, _, device) = ProjectWithGainOn("Vocals");
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames);
        await bridge.SyncAsync(session.Project, Ct);
        var before = bridge.InstanceOf(device.Id)!;
        host.Track(before);
        await before.SetParameterAsync(GainParameter.Value, 0.25, Ct);
        var project = session.Project;

        await bridge.SetSampleRateAsync(44_100, Ct);

        var after = bridge.InstanceOf(device.Id)!;
        host.Track(after);
        Assert.NotSame(before, after);
        Assert.Equal((44_100.0, PluginInstanceState.Running), (after.Request.SampleRate, after.Status.State));
        Assert.Equal(44_100.0, bridge.SampleRate);
        Assert.All(Run(after), s => Assert.Equal(0.5f, s, 4));
        Assert.Same(project, session.Project);

        // The same rate again changes nothing.
        await bridge.SetSampleRateAsync(44_100, Ct);
        Assert.Same(after, bridge.InstanceOf(device.Id));
    }

    [Fact]
    public async Task AMissingPlugin_KeepsItsDevice_AndIsReported_WithoutStartingAWorker()
    {
        await using var host = new PluginTestHost();
        var session = new ProjectSession();
        var track = Track.Create("Lead");
        var missing = DeviceInstance.Create(new DeviceReference(new DeviceDefinitionId("plugin:vst3:gone/vital"), "Vital")) with
        {
            State = new PluginState(ByteBlock.Copy([1, 2, 3]), "vst3"),
        };
        Set(session, session.Project.WithChain(DeviceChain.Create(ChainOwner.ForTrack(track.Id)) with { Devices = [missing] }) with
        {
            Sequence = session.Project.Sequence.WithTrack(track),
        });
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames);

        await bridge.SyncAsync(session.Project, Ct);

        Assert.Equal(("Not installed", true), (bridge.StatusOf(missing)!.Text, bridge.StatusOf(missing)!.NeedsAttention));
        Assert.Null(bridge.InstanceOf(missing.Id));
        Assert.Equal(0, host.Manager.WorkerLaunchCount);
        Assert.Equal([1, 2, 3], session.Project.FindDevice(missing.Id)!.Value.Device.State!.Data.ToArray());
    }

    [Fact]
    public async Task RemovingADevice_StopsItsWorker()
    {
        await using var host = new PluginTestHost();
        var (session, track, device) = ProjectWithGainOn("Vocals");
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames);
        await bridge.SyncAsync(session.Project, Ct);
        var pid = bridge.InstanceOf(device.Id)!.WorkerProcessId!.Value;

        Set(session, session.Project.WithChain(session.Project.ChainOf(track.Id)!.Remove(device.Id)));
        await bridge.SyncAsync(session.Project, Ct);

        Assert.Null(bridge.InstanceOf(device.Id));
        await WaitUntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(10), "the removed device's worker to exit");
    }

    [Fact]
    public async Task ThePluginsOwnEdits_RecordedWhileItKeepsMoving_AreNotSentBackToIt()
    {
        await using var host = new PluginTestHost();
        var (session, _, device) = ProjectWithGainOn("Vocals");
        await using var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: Frames);
        await bridge.SyncAsync(session.Project, Ct);
        var instance = bridge.InstanceOf(device.Id)!;
        host.Track(instance);
        async Task<double> PluginGain() => (await instance.GetParametersAsync(Ct)).Single(p => p.Id == GainParameter.Value).Value;

        // The plugin moves its gain itself (a drag in its editor); each edit reaches the project later, on the UI thread.
        double[] moves = [0.2, 0.3, 0.4];
        foreach (var value in moves)
        {
            await instance.SetParameterAsync(GainParameter.Value, value, Ct);
        }

        foreach (var value in moves)
        {
            bridge.RecordEdit(new PluginParameterEdit(device.Id, GainParameter.Value, value));
            await bridge.SyncAsync(session.Project, Ct);
            Assert.Equal(0.4, await PluginGain(), 6);
        }

        // An edit in the project still reaches the plugin.
        session.Execute(DeviceCommands.SetParameter(device.Id, GainParameter, ControlValue.FromFraction(0.25)));
        await bridge.SyncAsync(session.Project, Ct);
        Assert.Equal(0.25, await PluginGain(), 6);
    }
}
