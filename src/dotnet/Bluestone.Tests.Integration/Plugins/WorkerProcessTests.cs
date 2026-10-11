using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Plugins.Workers;
using static Bluestone.Tests.Integration.Plugins.PluginTestHost;

namespace Bluestone.Tests.Integration.Plugins;

/// <summary>The worker process on its own: launch, handshake, and its exit when the host goes away.</summary>
[Collection(PluginProcessTests.Name)]
public sealed class WorkerProcessTests
{
    [Fact]
    public async Task AWorker_ExitsByItself_WhenItsControlPipeCloses_EvenWithAnInstanceRunning()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "plugin-test-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var worker = await WorkerProcess.StartAsync(WorkerMode.InstanceHost, new WorkerProcessOptions { HeartbeatInterval = null }, null, Ct);
        try
        {
            var created = await worker.RequestAsync(
                new CreateInstance(PluginInstanceId.New(), Sine, 48_000, new ExchangeOptions(64, 0, 1), 1, directory, null),
                TimeSpan.FromSeconds(15),
                Ct);
            Assert.IsType<InstanceCreated>(created);

            worker.ClosePipeWithoutShutdown();
            await WaitUntilAsync(() => worker.ExitCode is not null, TimeSpan.FromSeconds(10), "the worker to exit");

            Assert.Equal(0, worker.ExitCode);
        }
        finally
        {
            await worker.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }

        Assert.False(IsRunning(worker.ProcessId));
    }

    [Fact]
    public async Task ARequestTheWorkerDoesNotServe_GetsAnErrorReply_NotACrash()
    {
        var worker = await WorkerProcess.StartAsync(WorkerMode.InstanceHost, new WorkerProcessOptions(), null, Ct);
        try
        {
            var reply = await worker.RequestAsync(new ScanModule("/nothing"), TimeSpan.FromSeconds(15), Ct);
            var missing = await worker.RequestAsync(new CaptureState(PluginInstanceId.New()), TimeSpan.FromSeconds(15), Ct);

            Assert.Equal(ErrorCode.InvalidRequest, Assert.IsType<ErrorReply>(reply).Code);
            Assert.Equal(ErrorCode.InstanceNotFound, Assert.IsType<ErrorReply>(missing).Code);
            Assert.True(worker.IsAlive);
        }
        finally
        {
            await worker.DisposeAsync();
        }

        Assert.False(IsRunning(worker.ProcessId));
    }
}
