using Avalonia.Controls;
using Bluestone.Desktop.Controls;

namespace Bluestone.Tests.Ui;

/// <summary>
/// Runs <see cref="LedMotion"/> by hand: its time is <see cref="Time"/> (milliseconds), frames wait until <see cref="Frame"/>,
/// and fade and warm-up are on with Reduce motion off. Dispose to put everything back. A frame allocates nothing, so cost
/// tests can run through it.
/// </summary>
internal sealed class ManualLedClock : IDisposable
{
    private readonly Func<double> _clock = LedMotion.Clock;
    private readonly Action<TopLevel, Action<TimeSpan>> _request = LedMotion.RequestFrame;
    private readonly bool _fade = LedMotion.Fade;
    private readonly bool _warmup = LedMotion.Warmup;
    private readonly bool _reduce = LedMotion.ReduceMotion;
    private List<(TopLevel Top, Action<TimeSpan> Tick)> _due = [];
    private List<(TopLevel Top, Action<TimeSpan> Tick)> _running = [];

    public ManualLedClock()
    {
        LedMotion.Clock = () => Time;
        LedMotion.RequestFrame = (top, tick) => _due.Add((top, tick));
        LedMotion.Fade = true;
        LedMotion.Warmup = true;
        LedMotion.ReduceMotion = false;
    }

    public double Time { get; set; }

    /// <summary>How many frames have been asked for and not yet run.</summary>
    public int Pending => _due.Count;

    /// <summary>How many frames <paramref name="top"/> has asked for and not yet run.</summary>
    public int PendingFor(TopLevel top)
    {
        var count = 0;
        foreach (var (asker, _) in _due)
        {
            count += asker == top ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// Moves the time to <paramref name="at"/> and runs the frames asked for until now: every window's, or only
    /// <paramref name="only"/>'s (the others stay pending, as a window that has closed or is not drawing gets no frames).
    /// </summary>
    public void Frame(double at, TopLevel? only = null)
    {
        Time = at;
        (_due, _running) = (_running, _due);
        foreach (var frame in _running)
        {
            if (only is null || frame.Top == only)
            {
                frame.Tick(TimeSpan.Zero);
            }
            else
            {
                _due.Add(frame);
            }
        }

        _running.Clear();
    }

    public void Dispose()
    {
        LedMotion.Clock = _clock;
        LedMotion.RequestFrame = _request;
        LedMotion.Fade = _fade;
        LedMotion.Warmup = _warmup;
        LedMotion.ReduceMotion = _reduce;
    }
}
