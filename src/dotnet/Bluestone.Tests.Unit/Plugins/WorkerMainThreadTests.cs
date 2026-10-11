using Bluestone.PluginWorker;

namespace Bluestone.Tests.Unit.Plugins;

/// <summary>The worker's main-thread loop keeps serving its other idle handlers when one of them fails.</summary>
public sealed class WorkerMainThreadTests
{
    [Fact]
    public void Run_AnIdleHandlerThrows_ReportsAndRemovesIt_AndTheLoopGoesOn()
    {
        var errors = new StringWriter();
        using var main = new WorkerMainThread(errors);
        var failing = 0;
        var healthy = 0;
        var done = new TaskCompletionSource();
        main.AddIdleHandler(() =>
        {
            failing++;
            throw new InvalidOperationException("broken plugin");
        });
        main.AddIdleHandler(() =>
        {
            if (++healthy == 5)
            {
                done.SetResult();
            }

            return true;
        });

        main.Run(done.Task);

        Assert.Equal(1, failing);
        Assert.Equal(5, healthy);
        Assert.Contains("broken plugin", errors.ToString(), StringComparison.Ordinal);
    }
}
