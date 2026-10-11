using System.Collections.Concurrent;
using Bluestone.PluginWorker.Native;

namespace Bluestone.PluginWorker;

/// <summary>
/// The worker process's main thread. Plugins and their editors expect one "UI thread" that runs the operating system's
/// event loop (on macOS it must be the process's first thread, running NSApplication's loop), so the worker keeps its
/// main thread for that and runs the control plane elsewhere:
/// <list type="bullet">
/// <item>The main thread runs <see cref="Run"/>: queued work, then the native event loop (window events, plugin timers),
/// then each registered idle handler (plugin requests, edits, editor close), round and round.</item>
/// <item>The control loop runs on a thread-pool thread and hands every request to the main thread with
/// <see cref="Invoke{T}"/>, which waits for the result.</item>
/// <item>Each instance's processing thread calls the plugin directly (the audio role); the plugin host serializes it
/// with control calls.</item>
/// </list>
/// The native library is loaded only when a third-party plugin is first used, so a worker hosting only the reference
/// plugins never loads it.
/// </summary>
internal sealed class WorkerMainThread : IDisposable
{
    private const uint PumpMilliseconds = 10;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly List<Func<bool>> _idle = [];
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly TextWriter _errors;
    private volatile bool _nativeReady;
    private string? _nativeError;

    /// <summary>Creates the loop for the calling thread; failures of idle handlers are written to <paramref name="errors"/>.</summary>
    internal WorkerMainThread(TextWriter errors)
    {
        _errors = errors;
    }

    /// <summary>The main thread of this process, once <see cref="Program"/> has created it; null in unit tests.</summary>
    public static WorkerMainThread? Current { get; private set; }

    public void Dispose() => _signal.Dispose();

    public bool IsCurrent => Environment.CurrentManagedThreadId == _threadId;

    public static WorkerMainThread CreateForThisThread() => Current = new WorkerMainThread(Console.Error);

    /// <summary>Runs <paramref name="work"/> on the main thread and returns its result (or rethrows its exception).</summary>
    public T Invoke<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsCurrent)
        {
            return work();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
#pragma warning disable CA1031 // Rethrown on the calling thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                done.SetException(ex);
            }
        });
        Wake();
        return done.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action work) => Invoke(() =>
    {
        work();
        return true;
    });

    /// <summary>Adds a handler called on the main thread after every turn of the loop; it returns false to be removed. Main thread.</summary>
    public void AddIdleHandler(Func<bool> handler) => _idle.Add(handler);

    /// <summary>Loads and initializes the native plugin host on first use. Main thread. Throws <see cref="NotSupportedException"/> when it cannot be loaded.</summary>
    public void EnsureNative()
    {
        if (!IsCurrent)
        {
            throw new InvalidOperationException("The native plugin host is initialized on the main thread.");
        }

        if (_nativeReady)
        {
            return;
        }

        if (_nativeError is not null)
        {
            throw new NotSupportedException(_nativeError);
        }

        try
        {
            var version = NativeMethods.AbiVersionOf();
            if (version != NativeMethods.AbiVersion)
            {
                throw new NotSupportedException($"The native plugin host speaks ABI {version}; this worker needs {NativeMethods.AbiVersion}.");
            }

            if (NativeMethods.MainInit(out var error) != 0)
            {
                throw new NotSupportedException(NativeMethods.TakeString(error) ?? "The native plugin host failed to start.");
            }

            _nativeReady = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException)
        {
            _nativeError = $"The native plugin host is not available: {ex.Message}";
            throw new NotSupportedException(_nativeError, ex);
        }
    }

    /// <summary>Serves queued work, the event loop, and idle handlers until <paramref name="until"/> completes. Main thread.</summary>
    public void Run(Task until)
    {
        ArgumentNullException.ThrowIfNull(until);
        while (!until.IsCompleted)
        {
            RunQueued();
            if (_nativeReady)
            {
                NativeMethods.MainPump(_work.IsEmpty ? PumpMilliseconds : 0);
            }
            else
            {
                _signal.WaitOne(TimeSpan.FromMilliseconds(PumpMilliseconds));
            }

            for (var i = _idle.Count - 1; i >= 0; i--)
            {
                if (!RunIdleHandler(_idle[i]))
                {
                    _idle.RemoveAt(i);
                }
            }
        }

        RunQueued();
    }

    /// <summary>Runs one idle handler; one that throws is reported and removed, so it cannot stop the loop (and with it every other instance).</summary>
    private bool RunIdleHandler(Func<bool> handler)
    {
        try
        {
            return handler();
        }
#pragma warning disable CA1031 // Any failure of one plugin's main-thread work is contained and reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _errors.WriteLine($"An idle handler failed and was removed: {ex}");
            return false;
        }
    }

    private void RunQueued()
    {
        while (_work.TryDequeue(out var item))
        {
            item();
        }
    }

    private void Wake()
    {
        _signal.Set();
        if (_nativeReady)
        {
            NativeMethods.MainWake();
        }
    }
}
