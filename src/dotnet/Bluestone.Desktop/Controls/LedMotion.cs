using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace Bluestone.Desktop.Controls;

/// <summary>Something that moves LEDs on <see cref="LedMotion"/>'s clock.</summary>
internal interface ILedAnimator
{
    /// <summary>
    /// Moves to <paramref name="now"/> (milliseconds on <see cref="LedMotion.Clock"/>) on a frame of <paramref name="top"/>;
    /// false once settled, or when it no longer runs in <paramref name="top"/>, which takes it off that window's clock.
    /// </summary>
    bool Advance(TopLevel top, double now);
}

/// <summary>
/// The shared clock for LED fade and warm-up (<see cref="LedLevels"/>): one loop per window on
/// <see cref="TopLevel.RequestAnimationFrame"/>, which runs only while some LED in that window is moving and stops once
/// all have settled, so an idle app does no work. <see cref="Fade"/> and <see cref="Warmup"/> follow Settings ▸ Display;
/// <see cref="ReduceMotion"/> follows the system's Reduce motion, and with it on every change is instant. UI thread only.
/// </summary>
internal static class LedMotion
{
    private static readonly ConditionalWeakTable<TopLevel, Loop> Loops = [];

    /// <summary>LEDs fade to dark rather than going out at once (Settings ▸ Display).</summary>
    public static bool Fade { get; set; } = true;

    /// <summary>Signal LEDs take a few milliseconds to light rather than lighting at once (Settings ▸ Display).</summary>
    public static bool Warmup { get; set; } = true;

    /// <summary>The system asks for less motion: no fade and no warm-up, whatever the settings.</summary>
    public static bool ReduceMotion { get; set; }

    /// <summary>Whether a fade runs now.</summary>
    public static bool Fades => Fade && !ReduceMotion;

    /// <summary>Whether a warm-up runs now.</summary>
    public static bool WarmsUp => Warmup && !ReduceMotion;

    /// <summary>The time in milliseconds; tests replace it.</summary>
    public static Func<double> Clock { get; set; } = SystemMilliseconds;

    /// <summary>Asks a window for one frame; tests replace it to run frames by hand.</summary>
    public static Action<TopLevel, Action<TimeSpan>> RequestFrame { get; set; } = Request;

    public static double Now => Clock();

    /// <summary>
    /// Runs <paramref name="animator"/> on the frames of <paramref name="owner"/>'s window until it settles, and returns that
    /// window; starting one already running there adds nothing. Null (and nothing runs) when the owner is in no window; the
    /// caller then shows the end state at once.
    /// </summary>
    public static TopLevel? Start(Visual owner, ILedAnimator animator)
    {
        if (TopLevel.GetTopLevel(owner) is not { } top)
        {
            return null;
        }

        if (!Loops.TryGetValue(top, out var loop))
        {
            loop = new Loop(top);
            Loops.Add(top, loop);
        }

        loop.Add(animator);
        return top;
    }

    /// <summary>How many animators are moving in <paramref name="top"/>, and whether it has a frame requested (for tests).</summary>
    internal static (int Moving, bool Scheduled) StateOf(TopLevel top) =>
        Loops.TryGetValue(top, out var loop) ? (loop.Count, loop.Scheduled) : (0, false);

    private static double SystemMilliseconds() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private static void Request(TopLevel top, Action<TimeSpan> tick) => top.RequestAnimationFrame(tick);

    private sealed class Loop
    {
        private readonly List<ILedAnimator> _animators = [];
        private readonly WeakReference<TopLevel> _top;
        private readonly Action<TimeSpan> _tick;

        public Loop(TopLevel top)
        {
            _top = new WeakReference<TopLevel>(top);
            _tick = _ => Run();
        }

        public int Count => _animators.Count;

        public bool Scheduled { get; private set; }

        public void Add(ILedAnimator animator)
        {
            if (!_animators.Contains(animator))
            {
                _animators.Add(animator);
            }

            Schedule();
        }

        private void Run()
        {
            Scheduled = false;
            if (!_top.TryGetTarget(out var top))
            {
                _animators.Clear();
                return;
            }

            var now = Now;
            // Backwards, so a settled animator can take the last one's place.
            for (var i = _animators.Count - 1; i >= 0; i--)
            {
                if (!_animators[i].Advance(top, now))
                {
                    _animators[i] = _animators[^1];
                    _animators.RemoveAt(_animators.Count - 1);
                }
            }

            if (_animators.Count > 0)
            {
                Schedule();
            }
        }

        private void Schedule()
        {
            if (!Scheduled && _top.TryGetTarget(out var top))
            {
                Scheduled = true;
                RequestFrame(top, _tick);
            }
        }
    }
}
