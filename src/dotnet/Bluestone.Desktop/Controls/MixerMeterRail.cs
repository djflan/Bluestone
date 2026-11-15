using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A mixer strip's one meter: a single column of LED segments drawn by <see cref="SignalIndicatorRenderer"/>, so it has the
/// same gradient and segment rhythm as every other signal indicator in Bluestone. The column fills to the display mode's
/// value on that mode's scale (<see cref="MixerRail.Range"/>), and a hairline marks the held peak where the mode is a dBFS
/// one (Peak + RMS and the K-system). Nothing is lit while the reading is unavailable or still warming up.
/// </summary>
public sealed class MixerMeterRail : Control
{
    public static readonly StyledProperty<MixerMeterMode> ModeProperty = AvaloniaProperty.Register<MixerMeterRail, MixerMeterMode>(nameof(Mode));

    /// <summary>The display mode's value in the mode's own unit (dBFS, dBVU, K dB, or LUFS); NaN for none.</summary>
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<MixerMeterRail, double>(nameof(Value), double.NaN);

    /// <summary>The held peak, always in dBFS.</summary>
    public static readonly StyledProperty<double> PeakHoldProperty = AvaloniaProperty.Register<MixerMeterRail, double>(nameof(PeakHold), double.NaN);

    public static readonly StyledProperty<bool> IsReadyProperty = AvaloniaProperty.Register<MixerMeterRail, bool>(nameof(IsReady));

    /// <summary>The width of the LED column.</summary>
    public static readonly StyledProperty<double> RailWidthProperty = AvaloniaProperty.Register<MixerMeterRail, double>(nameof(RailWidth), 14);

    private static readonly SignalIndicatorColors MeterColors = new(Palette.MeterLow, Palette.MeterMid, Palette.MeterHot, Palette.MeterClip, Palette.JunctionLamp, Palette.TextSecondary);
    private static readonly IBrush HoldBrush = MixerRail.Brush(Palette.MixerMeterPeakHold);
    private readonly SignalIndicatorRenderer _renderer = new() { Colors = MeterColors };

    static MixerMeterRail()
    {
        AffectsRender<MixerMeterRail>(ModeProperty, ValueProperty, PeakHoldProperty, IsReadyProperty, RailWidthProperty);
        AffectsMeasure<MixerMeterRail>(RailWidthProperty);
    }

    /// <summary>The rail is as wide as its segments plus the one-pixel halo on each side.</summary>
    protected override Size MeasureOverride(Size availableSize) => new(RailWidth + 2, 0);

    public MixerMeterMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double PeakHold
    {
        get => GetValue(PeakHoldProperty);
        set => SetValue(PeakHoldProperty, value);
    }

    public bool IsReady
    {
        get => GetValue(IsReadyProperty);
        set => SetValue(IsReadyProperty, value);
    }

    public double RailWidth
    {
        get => GetValue(RailWidthProperty);
        set => SetValue(RailWidthProperty, value);
    }

    /// <summary>
    /// How many of <paramref name="count"/> segments are lit, and which one holds the peak (−1 for none). Both round the
    /// same way as the gain bar's fill (<see cref="MixerRail.EdgeY"/>), so a level and a held peak at the same value meet
    /// on the same segment edge.
    /// </summary>
    internal (int Lit, int Held) Segments(int count)
    {
        if (!IsReady || double.IsNaN(Value))
        {
            return (0, -1);
        }

        var (floor, ceiling) = MixerRail.Range(Mode);
        var lit = Value <= floor ? 0 : Math.Clamp((int)Math.Round(MixerRail.Fraction(Value, floor, ceiling) * count), 0, count);
        var held = -1;
        if (Mode is MixerMeterMode.PeakRms or MixerMeterMode.K20 or MixerMeterMode.K14 or MixerMeterMode.K12 && !double.IsNaN(PeakHold))
        {
            var hold = PeakHold + MixerRail.KOffset(Mode);
            if (hold > floor)
            {
                held = Math.Clamp((int)Math.Round(MixerRail.Fraction(hold, floor, ceiling) * count) - 1, -1, count - 1);
            }
        }

        return (lit, held);
    }

    public override void Render(DrawingContext context)
    {
        var metrics = MixerRail.Metrics(Bounds.Height, RailWidth);
        var (lit, held) = Segments(metrics.Count);
        var silent = lit == 0 && held < 0;
        var state = new SignalDisplayState(lit, 0, held < lit ? -1 : held, -1, false, false, silent, !silent, 0, false, SignalIndicatorRenderer.NoReadout);
        var left = Math.Round((Bounds.Width - RailWidth) / 2);
        var top = MixerRail.Inset;

        // Cached columns at the screen's scale, and at least twice the size so a scaled render (a 2x capture) stays sharp;
        // the same expression SignalIndicator uses.
        var scale = Math.Max(2, Math.Ceiling(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));

        // The renderer draws a stereo pair; the clip keeps the first column and its halo, and none of the second.
        using (context.PushClip(new Rect(left - 1, top - 1, RailWidth + 2, metrics.Height + 2)))
        {
            _renderer.DrawLevel(context, new Point(left, top), metrics, LedShape.Bar, state, scale);
            if (held >= 0)
            {
                // Snapped to the pixel grid so the hairline stays crisp instead of blurring across two rows.
                var y = Math.Floor(metrics.SegmentTop(top + metrics.Height, held));
                context.DrawRectangle(HoldBrush, null, new Rect(left, y, RailWidth, 1));
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _renderer.ReleaseLevel();
        base.OnDetachedFromVisualTree(e);
    }
}
