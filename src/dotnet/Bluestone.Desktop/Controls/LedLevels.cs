using Avalonia;
using Avalonia.Controls;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// How lit each of a control's LEDs is, from 0 (dark) to 1 (lit), moving toward its target on <see cref="LedMotion"/>'s
/// clock: falling brightness fades, eased (fast at first, then a short tail), over <see cref="FadeMs"/>; rising
/// brightness warms up in a linear ramp over <see cref="WarmupMs"/> (0 changes at once). Levels are quantized to
/// <see cref="Steps"/> steps
/// so a control can prebuild one brush per step: the clock's tick allocates nothing, and a draw part-way allocates nothing
/// of ours (ADR 0043; the drawing backend's own cost per draw is the control's, at rest or not). The arrays are
/// reallocated only when the LED count changes. Changes are instant while the control is in no window, while the matching switch on
/// <see cref="LedMotion"/> is off, or under Reduce motion. Leaving its window settles every LED on its target, so a control
/// moved to another window mid-fade is never left part-lit, and its next change runs on the new window's clock. Turning
/// a switch off or Reduce motion on mid-motion shows the end state on the next frame; a change of target mid-motion moves
/// on from the level shown. UI thread only.
/// </summary>
internal sealed class LedLevels : ILedAnimator
{
    /// <summary>Quantization steps between dark (0) and lit (<see cref="Steps"/>).</summary>
    public const int Steps = 8;

    private readonly Visual _owner;
    private double[] _target = [];
    private double[] _level = [];
    private double[] _from = [];
    private double[] _since = [];
    private int[] _step = [];
    private bool _running;
    private TopLevel? _top;

    public LedLevels(Visual owner, double fadeMs, double warmupMs = 0, int count = 0)
    {
        _owner = owner;
        FadeMs = fadeMs;
        WarmupMs = warmupMs;
        Resize(count);
        owner.DetachedFromVisualTree += (_, _) => Settle();
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

        _target = new double[count];
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
    public bool Target(int index) => _target[index] > 0;

    /// <summary>
    /// Sends LED <paramref name="index"/> toward lit or dark from <paramref name="now"/> (<see cref="LedMotion.Now"/>);
    /// <paramref name="instant"/> skips the motion (a control's first frame at a new size).
    /// </summary>
    public void Set(int index, bool lit, double now, bool instant = false) => Set(index, lit ? 1 : 0, now, instant);

    /// <summary>
    /// Sends LED <paramref name="index"/> toward a brightness from 0 (dark) to 1 (lit) from
    /// <paramref name="now"/> (<see cref="LedMotion.Now"/>); <paramref name="instant"/> skips the motion.
    /// </summary>
    public void Set(int index, double target, double now, bool instant = false)
    {
        target = Math.Clamp(target, 0, 1);
        if (_target[index] == target)
        {
            // An instant request still settles an LED caught mid-motion toward the same target.
            if (instant)
            {
                _level[index] = target;
                _step[index] = Quantize(target);
            }

            return;
        }

        _target[index] = target;
        var warming = target > _level[index];
        var moves = !instant && (warming ? WarmupMs > 0 && LedMotion.WarmsUp : FadeMs > 0 && LedMotion.Fades);
        if (moves && !_running && LedMotion.Start(_owner, this) is { } top)
        {
            _top = top;
            _running = true;
        }

        if (moves && _running)
        {
            _from[index] = _level[index];
            _since[index] = now;
        }
        else
        {
            _level[index] = target;
            _step[index] = Quantize(target);
        }
    }

    public bool Advance(TopLevel top, double now)
    {
        if (!_running || top != _top)
        {
            // Settled since, or now running in another window: nothing to do here.
            return false;
        }

        var attached = TopLevel.GetTopLevel(_owner) == top;
        var fades = attached && LedMotion.Fades;
        var warms = attached && LedMotion.WarmsUp;
        var moving = false;
        var changed = false;
        for (var i = 0; i < _level.Length; i++)
        {
            var goal = _target[i];
            if (_level[i] == goal)
            {
                continue;
            }

            var elapsed = Math.Max(0, now - _since[i]);
            double level;
            if (goal > _from[i])
            {
                level = warms && WarmupMs > 0 ? Math.Min(goal, _from[i] + (elapsed / WarmupMs)) : goal;
            }
            else
            {
                var progress = fades && FadeMs > 0 ? elapsed / FadeMs : 1;
                var remaining = 1 - progress;
                level = progress >= 1 ? goal : goal + ((_from[i] - goal) * remaining * remaining);
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

    /// <summary>Puts every LED on its target at once and leaves the clock (the owner left its window).</summary>
    private void Settle()
    {
        _running = false;
        _top = null;
        var changed = false;
        for (var i = 0; i < _level.Length; i++)
        {
            var goal = _target[i];
            changed |= _level[i] != goal;
            _level[i] = goal;
            _step[i] = Quantize(goal);
        }

        if (changed)
        {
            _owner.InvalidateVisual();
        }
    }

    private static int Quantize(double level) => (int)Math.Round(level * Steps, MidpointRounding.AwayFromZero);
}
