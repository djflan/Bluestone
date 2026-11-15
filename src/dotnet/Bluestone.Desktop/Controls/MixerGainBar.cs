using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A strip's gain as a tall, slim bar that fills from the bottom to the setting, on the same segment grid as the meter
/// and scale beside it (−60 to +6 dB, <see cref="MixerRail"/>). A Slider's thumb cannot express a filled level; this is a
/// gain setting in dB, not a level reading. Drag vertically, use the keys or wheel, or double-click for unity (0 dB).
/// </summary>
public sealed class MixerGainBar : MixerValueControl
{
    /// <summary>The width of the bar itself; the control is wider so its hit target and focus ring are comfortable.</summary>
    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<MixerGainBar, double>(nameof(BarWidth), 8);

    private static readonly IBrush TrackBrush = MixerRail.Brush(Palette.Raised);
    private static readonly IBrush FillBrush = MixerRail.Brush(Palette.Mix(Palette.Accent, Palette.Panel, 0.35));
    private static readonly IBrush FillHoverBrush = MixerRail.Brush(Palette.Mix(Palette.Accent, Palette.Panel, 0.15));
    private static readonly IBrush EdgeBrush = MixerRail.Brush(Palette.Lighten(Palette.Mix(Palette.Accent, Palette.Panel, 0.35), 0.18));
    private static readonly IBrush CapBrush = MixerRail.Brush(Palette.Text);
    private static readonly IBrush DisabledBrush = MixerRail.Brush(Palette.TextMuted);
    private static readonly IBrush OverBrush = MixerRail.Brush(Palette.MixerMeterHot);
    private static readonly IPen FocusPen = new Avalonia.Media.Immutable.ImmutablePen(MixerRail.Brush(Palette.Focus), 1);

    static MixerGainBar()
    {
        AffectsRender<MixerGainBar>(BarWidthProperty);
        AffectsMeasure<MixerGainBar>(BarWidthProperty);
        CursorProperty.OverrideDefaultValue<MixerGainBar>(new Cursor(StandardCursorType.SizeNorthSouth));
        SmallChangeProperty.OverrideDefaultValue<MixerGainBar>(1);
        LargeChangeProperty.OverrideDefaultValue<MixerGainBar>(6);
    }

    public double BarWidth
    {
        get => GetValue(BarWidthProperty);
        set => SetValue(BarWidthProperty, value);
    }

    protected override string ValueText => Value <= Minimum ? "−∞ dB" : Invariant($"{Value:+0.0;-0.0;0.0} dB");

    /// <summary>A gain below the bar's floor starts a gesture from the floor, so the first movement is visible.</summary>
    protected override double Start => Math.Max(base.Start, Math.Min(MixerRail.GainFloor, Maximum));

    protected override double DragDelta(Vector delta)
    {
        var metrics = MixerRail.Metrics(Bounds.Height, BarWidth);
        return -delta.Y / Math.Max(1, metrics.Height) * (MixerRail.GainCeiling - MixerRail.GainFloor);
    }

    /// <summary>At least as wide as the bar; the view stretches it so its whole column is a drag target.</summary>
    protected override Size MeasureOverride(Size availableSize) => new(BarWidth, 0);

    public override void Render(DrawingContext context)
    {
        // An invisible surface across the full bounds, so the column around the slim bar takes the pointer too.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var metrics = MixerRail.Metrics(Bounds.Height, BarWidth);
        var left = Math.Round((Bounds.Width - BarWidth) / 2);
        var top = MixerRail.Inset;
        var bottom = MixerRail.Bottom(metrics);
        var radius = MixerRail.Radius("Radius.Small", 2);
        var whole = new Rect(left, top, BarWidth, metrics.Height);
        context.DrawRectangle(TrackBrush, null, whole, radius, radius);

        var fillTop = Math.Floor(MixerRail.EdgeY(metrics, MixerRail.Fraction(Value, MixerRail.GainFloor, MixerRail.GainCeiling)));
        if (Value > MixerRail.GainFloor && fillTop < bottom)
        {
            using (context.PushClip(new RoundedRect(whole, radius)))
            {
                var fill = !IsEnabled ? DisabledBrush : IsDragging || IsPointerOver ? FillHoverBrush : FillBrush;
                context.DrawRectangle(fill, null, new Rect(left, fillTop, BarWidth, bottom - fillTop));
                context.DrawRectangle(EdgeBrush, null, new Rect(left, fillTop + 1, 1, Math.Max(0, bottom - fillTop - 1)));
                context.DrawRectangle(CapBrush, null, new Rect(left, fillTop, BarWidth, 1));
            }
        }

        // Gain above the scale's ceiling (the strip allows more boost than the bar shows): a small caret above the bar, so a
        // full bar at +6 dB and one at +18 dB do not look the same. The readout carries the exact value.
        if (Value > MixerRail.GainCeiling)
        {
            var caret = new StreamGeometry();
            using (var shape = caret.Open())
            {
                var apex = Math.Max(0, top - MixerRail.TickLength - 2);
                shape.BeginFigure(new Point(left, top - 2), true);
                shape.LineTo(new Point(left + (BarWidth / 2), apex));
                shape.LineTo(new Point(left + BarWidth, top - 2));
                shape.EndFigure(true);
            }

            context.DrawGeometry(OverBrush, null, caret);
        }

        if (IsFocused)
        {
            var ring = whole.Inflate(2);
            context.DrawRectangle(null, FocusPen, new Rect(ring.X + 0.5, ring.Y + 0.5, ring.Width - 1, ring.Height - 1), radius + 1, radius + 1);
        }
    }
}
