using System.Collections.Concurrent;

namespace Bluestone.Audio.Devices;

internal static class AsioThread
{
    private static readonly BlockingCollection<Action> Queue = new();
    private static readonly Thread? Worker;

    static AsioThread()
    {
        if (OperatingSystem.IsWindows())
        {
            Worker = new Thread(Run) { IsBackground = true, Name = "Bluestone ASIO control" };
            Worker.SetApartmentState(ApartmentState.STA);
            Worker.Start();
        }
    }

    public static bool IsCurrent => ReferenceEquals(Thread.CurrentThread, Worker);

    public static T Invoke<T>(Func<T> action)
    {
        if (!OperatingSystem.IsWindows() || IsCurrent)
        {
            return action();
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(() =>
        {
            try
            {
                result.SetResult(action());
            }
            catch (Exception exception)
            {
                result.SetException(exception);
            }
        });
        return result.Task.GetAwaiter().GetResult();
    }

    public static void Invoke(Action action) => Invoke(() =>
    {
        action();
        return true;
    });

    private static void Run()
    {
        foreach (var action in Queue.GetConsumingEnumerable())
        {
            action();
        }
    }
}
