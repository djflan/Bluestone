using System.Diagnostics;

namespace Bluestone.Playback;

/// <summary>
/// Drives a <see cref="PlaybackEngine"/> on a dedicated high-priority thread: pump, then wait until
/// the next event or until a command arrives. Waits longer than <see cref="SpinThreshold"/> block on
/// the engine's signal; shorter ones spin so immediate-delivery endpoints are served on time.
/// </summary>
public sealed class PlaybackThread : IDisposable
{
    public static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(1.5);

    private readonly PlaybackEngine _engine;
    private readonly Action? _onThreadStart;
    private readonly Thread _thread;
    private volatile bool _stopping;

    /// <param name="engine">The engine to drive.</param>
    /// <param name="onThreadStart">
    /// Optional platform setup run on the playback thread before pumping, such as raising its
    /// scheduling class. Kept outside this project so playback stays platform-neutral.
    /// </param>
    public PlaybackThread(PlaybackEngine engine, Action? onThreadStart = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _onThreadStart = onThreadStart;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Bluestone playback",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Set if the engine threw; the thread stops pumping after a fault.</summary>
    /// <remarks>Disposing pumps once more on the playback thread, so a <see cref="PlaybackEngine.Stop"/> issued just before takes effect.</remarks>
    public Exception? Fault { get; private set; }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            ((EventWaitHandle)_engine.WorkSignal).Set();
        }
        catch (ObjectDisposedException)
        {
            // The engine was disposed first; the thread will notice _stopping on its next pass.
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        try
        {
            _onThreadStart?.Invoke();
            while (!_stopping)
            {
                var wait = _engine.Pump();
                if (wait == Timeout.InfiniteTimeSpan)
                {
                    _engine.WorkSignal.WaitOne();
                }
                else if (wait > SpinThreshold)
                {
                    _engine.WorkSignal.WaitOne(wait - TimeSpan.FromMilliseconds(1));
                }
                else if (wait > TimeSpan.Zero)
                {
                    var until = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
                    var spinner = default(SpinWait);
                    while (Stopwatch.GetTimestamp() < until && !_stopping)
                    {
                        spinner.SpinOnce(sleep1Threshold: -1);
                    }
                }
            }

            // Apply anything queued before shutdown (typically Stop), so sounding notes are released
            // by the thread that owns the engine rather than by a concurrent caller.
            _engine.Pump();
        }
        catch (ObjectDisposedException) when (_stopping)
        {
        }
#pragma warning disable CA1031 // A fault on the playback thread must be captured, not crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Fault = ex;
        }
    }
}
