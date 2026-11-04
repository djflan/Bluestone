using Bluestone.Application.Engine;
using Bluestone.Application.Sessions;
using Bluestone.Application.Status;
using Bluestone.Engine.Protocol;
using Bluestone.Midi.Endpoints;
using Bluestone.Playback;
using Bluestone.Profiles;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Engine;

/// <summary>
/// The engine process and its supervisor (ADR 0044) with real engine processes: starting, answering, reporting its
/// figures, shutting down, and each way an engine can stop (killed, hung, faulted, missing), restarted with a warning or
/// left failed after too many stops. Nothing is mocked.
/// </summary>
[Collection(EngineProcessTests.Name)]
public sealed class EngineSupervisorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TheEngine_StartsReady_Answers_ReportsItsFigures_AndShutsDownWithoutCountingAsAStop()
    {
        var activities = new ActivityLog();
        var ended = Ended(activities);
        var engine = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true }, activities);

        await engine.StartAsync();

        Assert.Equal(EngineState.Ready, engine.State);
        var pid = engine.ProcessId!.Value;
        Assert.True(IsRunning(pid));
        Assert.True(await engine.PingAsync(TimeSpan.FromSeconds(5)) < TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => engine.Telemetry is not null, Patience, "a heartbeat");
        Assert.True(engine.Telemetry!.Value.WorkingSetBytes > 0);
        Assert.Contains(Snapshot(ended), e => e.Activity.Text == "Starting the engine…" && e.Outcome == ActivityOutcome.Completed);

        await engine.DisposeAsync();

        Assert.False(IsRunning(pid));
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.Empty(engine.Stops);
    }

    [Fact]
    public async Task AKilledEngine_IsReplaced_AndTheRestartIsAWarning()
    {
        var activities = new ActivityLog();
        var ended = Ended(activities);
        await using var engine = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true }, activities);
        await engine.StartAsync();
        var first = engine.ProcessId!.Value;

        KillProcess(first);
        await WaitUntilAsync(() => engine.State == EngineState.Ready && engine.ProcessId is { } next && next != first, Patience, "a new engine");

        var stop = Assert.Single(engine.Stops);
        Assert.Equal(first, stop.ProcessId);
        Assert.Contains(stop.Reason, new[] { EngineStopReason.Exited, EngineStopReason.ChannelBroken });
        await WaitUntilAsync(() => Snapshot(ended).Any(e => e.Outcome == ActivityOutcome.Warning), Patience, "the restart's warning");
        var restart = Assert.Single(Snapshot(ended), e => e.Outcome == ActivityOutcome.Warning);
        Assert.StartsWith("The engine stopped (", restart.Text, StringComparison.Ordinal);
        Assert.EndsWith("was restarted; playback is stopped.", restart.Text, StringComparison.Ordinal);
        Assert.True(await engine.PingAsync(TimeSpan.FromSeconds(5)) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AHungEngine_IsKilled_AndReplaced()
    {
        await using var engine = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true, HeartbeatTimeout = TimeSpan.FromMilliseconds(600) }, new ActivityLog());
        await engine.StartAsync();
        var hung = engine.ProcessId!.Value;

        await engine.SendTestAsync(TestAction.Hang);
        await WaitUntilAsync(() => engine.State == EngineState.Ready && engine.ProcessId is { } next && next != hung, Patience, "a new engine");

        Assert.Equal(EngineStopReason.MissedHeartbeats, Assert.Single(engine.Stops).Reason);
        await WaitUntilAsync(() => !IsRunning(hung), Patience, "the hung engine to be killed");
    }

    [Fact]
    public async Task AFaultOnARealTimeThread_IsAStop()
    {
        await using var engine = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true }, new ActivityLog());
        await engine.StartAsync();
        var faulted = engine.ProcessId!.Value;

        await engine.SendTestAsync(TestAction.Fault);
        await WaitUntilAsync(() => engine.State == EngineState.Ready && engine.ProcessId is { } next && next != faulted, Patience, "a new engine");

        var stop = Assert.Single(engine.Stops);
        Assert.Equal((EngineStopReason.Fault, "A test fault on the playback thread."), (stop.Reason, stop.Detail));
        await WaitUntilAsync(() => !IsRunning(faulted), Patience, "the faulted engine to be killed");
    }

    [Fact]
    public async Task TooManyStops_LeaveTheEngineFailed_UntilItIsRestarted()
    {
        var activities = new ActivityLog();
        var ended = Ended(activities);
        await using var engine = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true, RestartLimit = 2 }, activities);
        await engine.StartAsync();

        var first = engine.ProcessId!.Value;
        await engine.SendTestAsync(TestAction.Crash);
        await WaitUntilAsync(() => engine.State == EngineState.Ready && engine.ProcessId is { } next && next != first, Patience, "the first replacement");
        await engine.SendTestAsync(TestAction.Crash);
        await WaitUntilAsync(() => engine.State == EngineState.Failed, Patience, "the engine to be left failed");

        Assert.Equal(2, engine.Stops.Length);
        Assert.Null(engine.ProcessId);
        Assert.Contains(Snapshot(ended), e => e.Outcome == ActivityOutcome.Failed && e.Text.Contains("stopped 2 times within 60 seconds", StringComparison.Ordinal));

        await engine.RestartAsync();

        Assert.Equal(EngineState.Ready, engine.State);
        Assert.True(await engine.PingAsync(TimeSpan.FromSeconds(5)) < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Play_RestartsAnEngineThatWasLeftFailed()
    {
        await using var engine = new EngineSupervisor(new EngineSupervisorOptions { AllowTestCommands = true, RestartLimit = 1 }, new ActivityLog());
        using var playback = new EnginePlayback(engine);
        using var directory = new EndpointDirectory([]);
        await using var controller = new PlaybackController(new ProjectSession(), directory, ProfileCatalog.Empty, playback, startLiveThread: false);
        await engine.StartAsync();
        await engine.SendTestAsync(TestAction.Crash);
        await WaitUntilAsync(() => engine.State == EngineState.Failed, Patience, "the engine to be left failed");

        await controller.PlayAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(EngineState.Ready, engine.State);
        await WaitUntilAsync(() => playback.State == TransportState.Playing, Patience, "the restarted engine to play");
    }

    [Fact]
    public async Task AMissingEngine_FailsToStart_WithAReason_AndEndsFailed()
    {
        var activities = new ActivityLog();
        var ended = Ended(activities);
        await using var engine = new EngineSupervisor(new EngineSupervisorOptions { EnginePath = Path.Combine(Path.GetTempPath(), "no-such-folder", "Bluestone.Engine") }, activities);

        await engine.StartAsync();
        await WaitUntilAsync(() => engine.State == EngineState.Failed, Patience, "the engine to be left failed");

        Assert.Equal(3, engine.Stops.Length);
        Assert.All(engine.Stops, s => Assert.Equal(EngineStopReason.FailedToStart, s.Reason));
        Assert.Contains(Snapshot(ended), e => e.Outcome == ActivityOutcome.Failed && e.Activity.Text == "Starting the engine…" && e.Text.Contains("Could not start the engine", StringComparison.Ordinal));
    }

    private static List<ActivityEndedEventArgs> Ended(ActivityLog activities)
    {
        var ended = new List<ActivityEndedEventArgs>();
        activities.Ended += (_, e) =>
        {
            lock (ended)
            {
                ended.Add(e);
            }
        };
        return ended;
    }

    private static List<ActivityEndedEventArgs> Snapshot(List<ActivityEndedEventArgs> ended)
    {
        lock (ended)
        {
            return [.. ended];
        }
    }
}

/// <summary>Tests that start engine processes run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EngineProcessTests
{
    public const string Name = "Engine processes";
}
