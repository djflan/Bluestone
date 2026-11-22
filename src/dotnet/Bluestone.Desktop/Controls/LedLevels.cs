using Avalonia;
using Avalonia.Controls;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// How lit each of a control's LEDs is, from 0 (dark) to 1 (lit), moving toward its target on <see cref="LedMotion"/>'s
/// clock: going dark it fades, eased (fast at first, then a short tail), over <see cref="FadeMs"/>; going lit it warms up
/// in a linear ramp over <see cref="WarmupMs"/> (0 lights at once). Levels are quantized to <see cref="Steps"/> steps
/// so a control can prebuild one brush per step and a frame allocates nothing (ADR 0043). The arrays are reallocated only
/// when the LED count changes. Changes are instant while the control is in no window, while the matching switch on
/// <see cref="LedMotion"/> is off, or under Reduce motion. UI thread only.
/// </summary>
internal sealed class LedLevels : ILedAnimator
{
    /// <summary>Quantization steps between dark (0) and lit (<see cref="Steps"/>).</summary>
    public const int Steps = 8;

    private readonly Visual _owner;
    private bool[] _target = [];
    private double[] _level = [];
    private double[] _from = [];
    private double[] _since = [];
    private int[] _step = [];
    private bool _running;

    public LedLevels(Visual owner, double fadeMs, double warmupMs = 0, int count = 0)
    {
        _owner = owner;
        FadeMs = fadeMs;
        WarmupMs = warmupMs;
        Resize(count);
    }

    /// <summary>How long a fully lit LED takes to go dark, in milliseconds.</summary>
    public double FadeMs { get; set; }

    /// <summary>How long a dark LED takes to light, in milliseconds (0: at once).</summary>
    public double WarmupMs { get; set; }

    public int Count => _level.Length;

    /// <summary>Whether this is on the clock now (for tests).</summary>
    internal bool IsRunning => _running;

    /// <summary>Makes room for <paramref name="count"/> LEDs, all dark. True when it reallocated (the count changed).</summary>
    public bool Resize(int count)
    {
        if (count == _level.Length)
        {
            return false;
        }

        _target = new bool[count];
        _level = new double[count];
        _from = new double[count];
        _since = new double[count];
        _step = new int[count];
        return true;
    }

    /// <summary>How lit LED <paramref name="index"/> is, 0 to 1.</summary>
    public double Level(int index) => _level[index];

    /// <summary>LED <paramref name="index"/>'s level quantized: 0 (dark) to <see cref="Steps"/> (lit).</summary>
    public int Step(int index) => _step[index];

    /// <summary>Whether LED <paramref name="index"/> is heading for lit.</summary>
    public bool Target(int index) => _target[index];

    /// <summary>
    /// Sends LED <paramref name="index"/> toward lit or dark from <paramref name="now"/> (<see cref="LedMotion.Now"/>);
    /// <paramref name="instant"/> skips the motion (a control's first frame at a new size).
    /// </summary>
    public void Set(int index, bool lit, double now, bool instant = false)
    {
        if (_target[index] == lit)
        {
            return;
        }

        _target[index] = lit;
        var moves = !instant && (lit ? WarmupMs > 0 && LedMotion.WarmsUp : FadeMs > 0 && LedMotion.Fades);
        if (moves && (_running || (_running = LedMotion.Start(_owner, this))))
        {
            _from[index] = _level[index];
            _since[index] = now;
        }
        else
        {
            _level[index] = lit ? 1 : 0;
            _step[index] = lit ? Steps : 0;
        }
    }

    public bool Advance(double now)
    {
        var attached = TopLevel.GetTopLevel(_owner) is not null;
        var fades = attached && LedMotion.Fades;
        var warms = attached && LedMotion.WarmsUp;
        var moving = false;
        var changed = false;
        for (var i = 0; i < _level.Length; i++)
        {
            var goal = _target[i] ? 1.0 : 0.0;
            if (_level[i] == goal)
            {
                continue;
            }

            var elapsed = Math.Max(0, now - _since[i]);
            double level;
            if (_target[i])
            {
                level = warms && WarmupMs > 0 ? Math.Min(1, _from[i] + (elapsed / WarmupMs)) : 1;
            }
            else
            {
                var progress = fades && FadeMs > 0 ? elapsed / FadeMs : 1;
                var remaining = 1 - progress;
                level = progress >= 1 ? 0 : _from[i] * remaining * remaining;
            }

            _level[i] = level;
            moving |= level != goal;
            var step = Quantize(level);
            if (step != _step[i])
            {
                _step[i] = step;
                changed = true;
            }
        }

        if (changed)
        {
            _owner.InvalidateVisual();
        }

        _running = moving;
        return moving;
    }

    private static int Quantize(double level) => (int)Math.Round(level * Steps, MidpointRounding.AwayFromZero);
}
