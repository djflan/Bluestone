using System.Diagnostics;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>
/// Crash isolation and recovery with real worker processes (epic scenarios 11, 12, 13). Crashes are real:
/// <see cref="Process.Kill()"/> from outside, <c>Environment.FailFast</c> inside, or a heartbeat kill; nothing is mocked.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginCrashRecoveryTests
{
    private const int Frames = 64;
    private const uint GainParameter = 0;
    private const uint InvertParameter = 1;

    [Fact]
    public async Task KillingTheWorkerMidStream_MarksCrashedOnce_SilencesAtOnce_AndRestartRestoresStateAndParameters()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var unavailable = 0;
        gain.StatusChanged += (_, e) =>
        {
            if (e.Current.State == PluginInstanceState.Unavailable)
            {
                Interlocked.Increment(ref unavailable);
            }
        };
        var driver = new BlockDriver(gain, Frames);
        Array.Fill(driver.Input, 1.0f);
        gain.QueueParameterChange(GainParameter, 0.25, 0);
        driver.Process();
        driver.Process();
        Assert.All(driver.Output, s => Assert.Equal(0.5f, s));

        // State is captured with gain 0.25; invert is changed afterwards, so only the remembered values know it.
        var snapshot = await gain.CaptureStateAsync(Ct);
        gain.QueueParameterChange(InvertParameter, 1.0, 0);
        driver.Process();
        driver.Process();
        Assert.All(driver.Output, s => Assert.Equal(-0.5f, s));

        // The audio thread keeps running through the kill. Every block is the plugin's exact output or exact silence.
        var pid = gain.WorkerProcessId!.Value;
        using var stop = new CancellationTokenSource();
        var outcomes = new List<ProcessOutcome>();
        var badSamples = 0;
        var audioThread = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var outcome = driver.Process().Outcome;
                outcomes.Add(outcome);
                var expected = outcome == ProcessOutcome.Processed ? -0.5f : 0.0f;
                badSamples += driver.Output.Count(s => s != expected);
            }
        });
        audioThread.Start();
        await Task.Delay(100, Ct);
        KillProcess(pid);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        await Task.Delay(200, Ct);
        await stop.CancelAsync();
        Assert.True(audioThread.Join(TimeSpan.FromSeconds(10)));

        Assert.Equal(new PluginInstanceStatus(PluginInstanceState.Unavailable, UnavailableReason.Crashed, gain.Status.Message), gain.Status);
        Assert.Equal(1, Volatile.Read(ref unavailable));
        Assert.Equal(0, badSamples);
        Assert.Contains(ProcessOutcome.Processed, outcomes);
        Assert.Equal(ProcessOutcome.Unavailable, outcomes[^1]);
        Assert.False(IsRunning(pid));

        var watch = Stopwatch.StartNew();
        var after = driver.Process();
        watch.Stop();
        Assert.Equal(ProcessOutcome.Unavailable, after.Outcome);
        Assert.All(driver.Output, s => Assert.Equal(0.0f, s));
        Assert.True(watch.ElapsedMilliseconds < 50, $"ProcessBlock took {watch.ElapsedMilliseconds} ms after the crash.");
        Assert.Same(snapshot, gain.LastSnapshot);

        var status = await gain.RestartAsync(Ct);
        host.Track(gain);

        Assert.Equal(PluginInstanceState.Running, status.State);
        Assert.NotEqual(pid, gain.WorkerProcessId);
        Assert.Equal([new ParameterValue(GainParameter, 0.25), new ParameterValue(InvertParameter, 1.0)], await gain.GetParametersAsync(Ct));
        driver.Process();
        Assert.Equal(ProcessOutcome.Processed, driver.Process().Outcome);
        Assert.All(driver.Output, s => Assert.Equal(-0.5f, s));
    }

    [Fact]
    public async Task AWorkerThatStopsAnswering_IsKilledByTheHeartbeat_AndTheInstanceIsHung()
    {
        await using var host = new PluginTestHost(o => o with { HeartbeatInterval = TimeSpan.FromMilliseconds(100), HeartbeatTimeout = TimeSpan.FromSeconds(1) });
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var pid = gain.WorkerProcessId!.Value;

        Assert.True(await gain.InduceTestFaultAsync(TestFault.Hang));
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);

        Assert.Equal(UnavailableReason.Hung, gain.Status.Reason);
        await WaitUntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(10), "the hung worker to be killed");
    }

    [Fact]
    public async Task AWorkerThatSendsGarbageFrames_IsKilled_AndTheInstanceHasAProtocolError()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var pid = gain.WorkerProcessId!.Value;

        Assert.True(await gain.InduceTestFaultAsync(TestFault.GarbageFrames));
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);

        Assert.Equal(UnavailableReason.ProtocolError, gain.Status.Reason);
        await WaitUntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(10), "the misbehaving worker to be killed");
    }

    [Fact]
    public async Task AWorkerThatFailsFast_IsCrashed_AndTheHostSurvives()
    {
        await using var host = new PluginTestHost();
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, 48_000, Frames, 0, 1));

        Assert.True(await sine.InduceTestFaultAsync(TestFault.FailFast));
        await WaitForStateAsync(sine, PluginInstanceState.Unavailable);

        Assert.Equal(UnavailableReason.Crashed, sine.Status.Reason);
        Assert.Equal(PluginInstanceState.Running, (await sine.RestartAsync(Ct)).State);
        host.Track(sine);
    }

    [Fact]
    public async Task ARestartStartedByTheUnavailableEvent_KeepsItsNewWorker_AndTheNewWorkerIsSupervised()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        Task<PluginInstanceStatus>? restart = null;
        gain.StatusChanged += (_, e) =>
        {
            if (e.Current.State == PluginInstanceState.Unavailable && restart is null)
            {
                restart = gain.RestartAsync(Ct);
            }
        };

        KillProcess(gain.WorkerProcessId!.Value);
        await WaitUntilAsync(() => restart is not null, TimeSpan.FromSeconds(10), "the restart to begin");

        Assert.Equal(PluginInstanceState.Running, (await restart!).State);
        host.Track(gain);
        var driver = new BlockDriver(gain, Frames);
        Array.Fill(driver.Input, 1.0f);
        await WaitUntilAsync(() => driver.Process().Outcome == ProcessOutcome.Processed, TimeSpan.FromSeconds(10), "the restarted worker to process");

        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        Assert.Equal(UnavailableReason.Crashed, gain.Status.Reason);
    }

    [Fact]
    public async Task AMissingWorker_FailsToStart_AndNothingRunsInTheHostProcess()
    {
        await using var host = new PluginTestHost(o => o with { WorkerPath = Path.Combine(AppContext.BaseDirectory, "no-such-worker", "Bluestone.PluginWorker") });
        var gain = await host.CreateAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var driver = new BlockDriver(gain, Frames);
        Array.Fill(driver.Input, 1.0f);

        var outcome = driver.Process();
        driver.Process();

        Assert.Equal(new PluginInstanceStatus(PluginInstanceState.Unavailable, UnavailableReason.FailedToStart, gain.Status.Message), gain.Status);
        Assert.Equal(ProcessOutcome.Unavailable, outcome.Outcome);
        Assert.All(driver.Output, s => Assert.Equal(0.0f, s));
        Assert.Null(gain.WorkerProcessId);
        Assert.Equal(1, host.Manager.WorkerLaunchCount);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "Bluestone.PluginWorker");
    }

    [Fact]
    public async Task AnUnknownPlugin_IsMissing()
    {
        await using var host = new PluginTestHost();

        var missing = await host.CreateAsync(new PluginInstanceRequest(Reference("reference.nothing", "Nothing", PluginKind.AudioEffect), 48_000, Frames, 1, 1));

        Assert.Equal(UnavailableReason.Missing, missing.Status.Reason);
    }

    [Fact]
    public async Task KillingOneOfSeveralWorkers_LeavesTheOthersProducingCorrectAudio()
    {
        await using var host = new PluginTestHost();
        double[] gains = [0.25, 0.5, 0.75];
        var instances = new List<PluginInstance>();
        var drivers = new List<BlockDriver>();
        foreach (var value in gains)
        {
            var instance = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
            await instance.SetParameterAsync(GainParameter, value, Ct);
            var driver = new BlockDriver(instance, Frames);
            Array.Fill(driver.Input, 1.0f);
            driver.Process();
            instances.Add(instance);
            drivers.Add(driver);
        }

        Assert.Equal(3, instances.Select(i => i.WorkerProcessId).Distinct().Count());
        var changes = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            instances[i].StatusChanged += (_, _) => Interlocked.Increment(ref changes[index]);
        }

        KillProcess(instances[1].WorkerProcessId!.Value);
        await WaitForStateAsync(instances[1], PluginInstanceState.Unavailable);

        for (var block = 0; block < 50; block++)
        {
            for (var i = 0; i < 3; i++)
            {
                var outcome = drivers[i].Process().Outcome;
                var expected = i == 1 ? 0.0f : (float)(gains[i] * 2);
                Assert.Equal(i == 1 ? ProcessOutcome.Unavailable : ProcessOutcome.Processed, outcome);
                Assert.All(drivers[i].Output, s => Assert.Equal(expected, s));
            }
        }

        Assert.Equal([0, 1, 0], changes);
        Assert.Equal(PluginInstanceState.Running, instances[0].Status.State);
        Assert.Equal(PluginInstanceState.Running, instances[2].Status.State);
    }

    [Fact]
    public async Task AWorkerThatCrashesAtStartup_WithAutomaticRestart_EndsQuarantined_NotInALoop()
    {
        var policy = new RestartPolicy { AutoRestart = true, MaxRestarts = 2, Window = TimeSpan.FromMinutes(5), InitialBackoff = TimeSpan.FromMilliseconds(20), MaxBackoff = TimeSpan.FromMilliseconds(100) };
        await using var host = new PluginTestHost(o => o with { WorkerEnvironment = new Dictionary<string, string> { ["BLUESTONE_PLUGINWORKER_TEST_CRASH_AT_STARTUP"] = "1" } });
        var states = new List<PluginInstanceState>();
        var gain = await host.CreateAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1) { RestartPolicy = policy });
        gain.StatusChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.Current.State);
            }
        };

        await WaitForStateAsync(gain, PluginInstanceState.Quarantined);
        await Task.Delay(500, Ct);

        Assert.Equal(PluginInstanceState.Quarantined, gain.Status.State);
        Assert.Equal(3, host.Manager.WorkerLaunchCount);

        // A manual restart resets the counters: the same budget is spent once more, then quarantine again.
        lock (states)
        {
            states.Clear();
        }

        await gain.RestartAsync(Ct);
        await WaitForStateAsync(gain, PluginInstanceState.Quarantined);
        await Task.Delay(500, Ct);
        Assert.Equal(6, host.Manager.WorkerLaunchCount);
        lock (states)
        {
            Assert.Equal(1, states.Count(s => s == PluginInstanceState.Quarantined));
            Assert.Equal(PluginInstanceState.Restarting, states[0]);
        }
    }

    [Fact]
    public async Task InstancesOfOneModule_ShareAWorker_AndAllBecomeUnavailableWhenItDies()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1) { Hosting = HostingMode.ByPlugin });
        var sine = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, 48_000, Frames, 0, 1) { Hosting = HostingMode.ByPlugin });
        var separate = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));

        Assert.Equal(gain.WorkerProcessId, sine.WorkerProcessId);
        Assert.NotEqual(gain.WorkerProcessId, separate.WorkerProcessId);
        Assert.Equal(2, host.Manager.WorkerLaunchCount);

        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);
        await WaitForStateAsync(sine, PluginInstanceState.Unavailable);

        Assert.Equal(PluginInstanceState.Running, separate.Status.State);
        Assert.Equal(PluginInstanceState.Running, (await gain.RestartAsync(Ct)).State);
        Assert.Equal(PluginInstanceState.Running, (await sine.RestartAsync(Ct)).State);
        Assert.Equal(gain.WorkerProcessId, sine.WorkerProcessId);
        host.Track(gain);
    }

    [Fact]
    public async Task InstancesHostedTogether_ShareOneWorker()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1) { Hosting = HostingMode.Together });
        var transpose = await host.CreateRunningAsync(new PluginInstanceRequest(Transpose, 48_000, Frames, 0, 0) { Hosting = HostingMode.Together });

        Assert.Equal(gain.WorkerProcessId, transpose.WorkerProcessId);
        Assert.Equal(1, host.Manager.WorkerLaunchCount);
    }

    [Fact]
    public async Task BypassInput_AfterACrash_PassesTheDryInputDelayedByThePipeline_NeverStaleAudio()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 2, 2) { FailurePolicy = FailurePolicy.BypassInput });
        await gain.SetParameterAsync(GainParameter, 0.25, Ct);
        var driver = new BlockDriver(gain, Frames);
        driver.Process();
        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);

        for (var block = 1; block <= 3; block++)
        {
            var previous = driver.Input.ToArray();
            Array.Fill(driver.Input, block);
            var outcome = driver.Process();

            Assert.True(outcome.Bypassed);
            Assert.Equal(block == 1 ? new float[2 * Frames] : previous, driver.Output);
        }
    }

    [Fact]
    public async Task CorruptState_IsReported_AndTheInstanceRunsWithDefaults()
    {
        await using var host = new PluginTestHost();
        var corrupt = new PluginStateSnapshot(Gain, new PluginStateData("bluestone.reference-state", [0x43, 0x52, 0x50, 0x53, 0x09, 0x00, 0x00, 0x00]), [], DateTimeOffset.UnixEpoch);

        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1) { RestoreFrom = corrupt });

        Assert.Contains("version 9", gain.LastStateRestoreError, StringComparison.Ordinal);
        Assert.Equal([new ParameterValue(GainParameter, 0.5), new ParameterValue(InvertParameter, 0.0)], await gain.GetParametersAsync(Ct));
    }

    [Fact]
    public async Task ASnapshot_RestoresIntoANewInstance()
    {
        await using var host = new PluginTestHost();
        var original = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        await original.SetParameterAsync(GainParameter, 0.125, Ct);
        var snapshot = await original.CaptureStateAsync(Ct);

        var copy = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1) { RestoreFrom = snapshot });

        Assert.Null(copy.LastStateRestoreError);
        Assert.Equal([new ParameterValue(GainParameter, 0.125), new ParameterValue(InvertParameter, 0.0)], await copy.GetParametersAsync(Ct));
    }

    [Fact]
    public async Task PeriodicSnapshots_CaptureStateWithoutBeingAsked()
    {
        await using var host = new PluginTestHost(o => o with { PeriodicSnapshotInterval = TimeSpan.FromMilliseconds(100) });
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        await gain.SetParameterAsync(GainParameter, 0.75, Ct);

        await WaitUntilAsync(() => gain.LastSnapshot?.ParameterValues.Contains(new ParameterValue(GainParameter, 0.75)) == true, StatusTimeout, "a periodic snapshot");

        Assert.Equal("bluestone.reference-state", gain.LastSnapshot!.State.Format);
    }

    [Fact]
    public async Task CaptureState_FromACrashedInstance_IsRefused_AndTheLastSnapshotIsKept()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var snapshot = await gain.CaptureStateAsync(Ct);
        KillProcess(gain.WorkerProcessId!.Value);
        await WaitForStateAsync(gain, PluginInstanceState.Unavailable);

        await Assert.ThrowsAsync<PluginUnavailableException>(() => gain.CaptureStateAsync(Ct));
        Assert.Same(snapshot, gain.LastSnapshot);
    }

    [Fact]
    public async Task DisposingTheManager_LeavesNoWorkerProcessBehind()
    {
        int[] pids;
        await using (var host = new PluginTestHost())
        {
            var a = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
            var b = await host.CreateRunningAsync(new PluginInstanceRequest(Sine, 48_000, Frames, 0, 2) { Hosting = HostingMode.ByPlugin });
            var c = await host.CreateRunningAsync(new PluginInstanceRequest(Transpose, 48_000, Frames, 0, 0) { Hosting = HostingMode.Together });
            pids = [a.WorkerProcessId!.Value, b.WorkerProcessId!.Value, c.WorkerProcessId!.Value];
            Assert.All(pids, pid => Assert.True(IsRunning(pid)));
        }

        Assert.All(pids, pid => Assert.False(IsRunning(pid)));
    }

    [Fact]
    public async Task DestroyingAnInstance_UnloadsIt_AndStopsItsWorker()
    {
        await using var host = new PluginTestHost();
        var gain = await host.CreateRunningAsync(new PluginInstanceRequest(Gain, 48_000, Frames, 1, 1));
        var pid = gain.WorkerProcessId!.Value;

        await host.Manager.DestroyInstanceAsync(gain, Ct);

        Assert.Equal(PluginInstanceState.Unloaded, gain.Status.State);
        Assert.Empty(host.Manager.Instances);
        await WaitUntilAsync(() => !IsRunning(pid), TimeSpan.FromSeconds(10), "the worker to exit");
        Assert.Empty(Directory.GetFiles(host.Manager.DataDirectory));
    }
}
