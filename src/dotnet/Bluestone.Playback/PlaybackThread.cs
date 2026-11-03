using System.Diagnostics;

namespace Bluestone.Playback;

/// <summary>
/// Drives a <see cref="PlaybackEngine"/> on a dedicated high-priority thread: pump, then wait until
/// the next event or until a command arrives. Waits longer than <see cref="SpinThreshold"/> (2 ms with a
/// precise sleep) block on the engine's signal, whose timeout has whole-millisecond resolution, so it ends early; the rest is
/// spun, or, when the platform supplies a precise sleep, slept in slices of at most
/// <see cref="FinalSlice"/> with a pump between them, so immediate-delivery endpoints are served on
/// time without spinning and commands and <see cref="PlaybackEngine.SendNow"/> are not held up (ADR 0043).
/// </summary>
public sealed class PlaybackThread : IDisposable
{
    public static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(1.5);

    /// <summary>The longest precise sleep before the thread pumps again; bounds the delay of commands and live thru.</summary>
    public static readonly TimeSpan FinalSlice = TimeSpan.FromMilliseconds(0.25);

    // With a precise sleep, block only when the 1 ms-early wait is at least a whole millisecond (shorter timeouts round to zero).
    private static readonly TimeSpan BlockThreshold = TimeSpan.FromMilliseconds(2);

    private readonly PlaybackEngine _engine;
    private readonly Action? _onThreadStart;
    private readonly Action<TimeSpan>? _preciseSleep;
    private readonly Thread _thread;
    private volatile bool _stopping;

    /// <param name="engine">The engine to drive.</param>
    /// <param name="onThreadStart">
    /// Optional platform setup run on the playback thread before pumping, such as raising its
    /// scheduling class. Kept outside this project so playback stays platform-neutral.
    /// </param>
    /// <param name="preciseSleep">
    /// Optional platform sleep accurate to well under a millisecond on the configured thread, such as
    /// <c>mach_wait_until</c> on a real-time thread. Without it the final approach to a due time is spun.
    /// </param>
    public PlaybackThread(PlaybackEngine engine, Action? onThreadStart = null, Action<TimeSpan>? preciseSleep = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _onThreadStart = onThreadStart;
        _preciseSleep = preciseSleep;
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
                else if (wait > (_preciseSleep is null ? SpinThreshold : BlockThreshold))
                {
                    _engine.WorkSignal.WaitOne(wait - TimeSpan.FromMilliseconds(1));
                }
                else if (wait > TimeSpan.Zero && _preciseSleep is { } sleep)
                {
                    sleep(wait < FinalSlice ? wait : FinalSlice);
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
