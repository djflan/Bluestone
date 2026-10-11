using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Bluestone.Platform.CoreMidi;

/// <summary>
/// The one thread on which Bluestone creates CoreMIDI clients. CoreMIDI delivers setup notifications
/// for every client on the run loop that was current when the process first called
/// <c>MIDIClientCreate</c>, so that run loop must outlive every provider. This background thread is
/// started once and runs for the life of the process.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class CoreMidiRunLoop
{
    private static readonly BlockingCollection<Action> Work = [];
    private static readonly Lazy<Thread> Thread = new(Start);

    /// <summary>Runs <paramref name="action"/> on the CoreMIDI thread and waits for it.</summary>
    public static T Invoke<T>(Func<T> action)
    {
        _ = Thread.Value;
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        Work.Add(() =>
        {
            try
            {
                result = action();
            }
#pragma warning disable CA1031 // Rethrown on the calling thread below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        return error is null ? result : throw new InvalidOperationException("CoreMIDI setup failed.", error);
    }

    private static Thread Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "CoreMIDI run loop" };
        thread.Start();
        return thread;
    }

    private static void Run()
    {
        while (true)
        {
            while (Work.TryTake(out var action))
            {
                action();
            }

            // Service CoreMIDI notifications, returning periodically to pick up queued work.
            Native.CFRunLoopRunInMode(Native.RunLoopDefaultMode, 0.02, returnAfterSourceHandled: false);
        }
    }
}
