using Avalonia;
using Avalonia.Controls;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A lit glyph laid over its dark twin, for styled keys and lamps that cannot draw their own fade (the transport's glyph
/// toggles, the input activity lamps). Its first child is the dark face and its second the lit one, both styled as they
/// are at rest: lit shows only the lit child; dark shows the dark child with the lit one over it, fading out over
/// <see cref="FadeDuration"/> on <see cref="LedMotion"/>'s clock (<see cref="LedLevels"/>), as a <see cref="SquareKey"/> does.
/// Going lit is instant. With Settings ▸ Display ▸ LED fade off or under Reduce motion, going dark is instant too. The
/// lit child's opacity changes only on a level step, so a fade sets it at most <see cref="LedLevels.Steps"/> times.
/// </summary>
public sealed class LedFade : Panel, ILedAnimator
{
    /// <summary>Whether the lit child shows.</summary>
    public static readonly StyledProperty<bool> IsLitProperty = AvaloniaProperty.Register<LedFade, bool>(nameof(IsLit));

    /// <summary>How long the lit child takes to fade out: a key's by default (<see cref="SquareKey.FadeMs"/>); a lamp takes Motion.LampRelease.</summary>
    public static readonly StyledProperty<TimeSpan> FadeDurationProperty =
        AvaloniaProperty.Register<LedFade, TimeSpan>(nameof(FadeDuration), TimeSpan.FromMilliseconds(SquareKey.FadeMs));

    private readonly LedLevels _lamp;
    private int _shownStep = -1;
    private bool _shownTarget;

    public LedFade()
    {
        _lamp = new LedLevels(this, SquareKey.FadeMs, count: 1);
        // After the lamp's own handler, which settles it on leaving the window.
        DetachedFromVisualTree += (_, _) => Sync();
    }

    public bool IsLit { get => GetValue(IsLitProperty); set => SetValue(IsLitProperty, value); }

    public TimeSpan FadeDuration { get => GetValue(FadeDurationProperty); set => SetValue(FadeDurationProperty, value); }

    /// <summary>How lit the lit child is (for tests).</summary>
    internal LedLevels Lamp => _lamp;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FadeDurationProperty)
        {
            _lamp.FadeMs = FadeDuration.TotalMilliseconds;
        }
        else if (change.Property == IsLitProperty)
        {
            _lamp.Set(0, IsLit, LedMotion.Now);
            if (_lamp.IsRunning)
            {
                // The lamp runs on the clock itself; this rides along to show each step it reaches.
                LedMotion.Start(this, this);
            }

            Sync();
        }
    }

    protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        _shownStep = -1;
        Sync();
    }

    bool ILedAnimator.Advance(TopLevel top, double now)
    {
        // Advancing twice at one time is harmless: a level depends only on the time.
        var moving = _lamp.Advance(top, now);
        Sync();
        return moving;
    }

    private void Sync()
    {
        var step = _lamp.Step(0);
        var target = _lamp.Target(0);
        if (step == _shownStep && target == _shownTarget)
        {
            return;
        }

        _shownStep = step;
        _shownTarget = target;
        if (Children.Count > 0)
        {
            Children[0].IsVisible = !target;
        }

        if (Children.Count > 1)
        {
            var lit = Children[1];
            lit.Opacity = step / (double)LedLevels.Steps;
            lit.IsVisible = step > 0;
        }
    }
}
