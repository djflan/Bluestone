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
    private readonly Action<Avalonia.Controls.TopLevel, Action<TimeSpan>> _request = LedMotion.RequestFrame;
    private readonly bool _fade = LedMotion.Fade;
    private readonly bool _warmup = LedMotion.Warmup;
    private readonly bool _reduce = LedMotion.ReduceMotion;
    private List<Action<TimeSpan>> _due = [];
    private List<Action<TimeSpan>> _running = [];

    public ManualLedClock()
    {
        LedMotion.Clock = () => Time;
        LedMotion.RequestFrame = (_, tick) => _due.Add(tick);
        LedMotion.Fade = true;
        LedMotion.Warmup = true;
        LedMotion.ReduceMotion = false;
    }

    public double Time { get; set; }

    /// <summary>How many frames have been asked for and not yet run.</summary>
    public int Pending => _due.Count;

    /// <summary>Moves the time to <paramref name="at"/> and runs the frames asked for until now.</summary>
    public void Frame(double at)
    {
        Time = at;
        (_due, _running) = (_running, _due);
        foreach (var tick in _running)
        {
            tick(TimeSpan.Zero);
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
