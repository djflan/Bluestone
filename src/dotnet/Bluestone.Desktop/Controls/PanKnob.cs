using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A strip's pan as a small Machined arc-ring knob (<see cref="ArcKnobPaint"/>): the value arc grows from the top centre
/// toward the side the signal is panned to. −1 is hard left, 0 centre, +1 hard right. Drag sideways (Shift for fine), use
/// the keys or wheel, or double-click (or Home) to centre. The knob draws at the left of its bounds, so the view can set
/// a caption and value beside it inside the same hit target.
/// </summary>
public sealed class PanKnob : MixerValueControl
{
    /// <summary>The knob's outer radius, including the track ring.</summary>
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<PanKnob, double>(nameof(Radius), 11);

    private readonly ArcKnobCache _cache = new();

    static PanKnob()
    {
        AffectsRender<PanKnob>(RadiusProperty);
        AffectsMeasure<PanKnob>(RadiusProperty);
        CursorProperty.OverrideDefaultValue<PanKnob>(new Cursor(StandardCursorType.SizeWestEast));
        MinimumProperty.OverrideDefaultValue<PanKnob>(-1);
        MaximumProperty.OverrideDefaultValue<PanKnob>(1);
        SmallChangeProperty.OverrideDefaultValue<PanKnob>(0.02);
        LargeChangeProperty.OverrideDefaultValue<PanKnob>(0.25);
    }

    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected override string ValueText
    {
        get
        {
            var amount = (int)Math.Round(Math.Abs(Value) * 100);
            return amount == 0 ? "Centre" : Value < 0 ? Invariant($"{amount}% left") : Invariant($"{amount}% right");
        }
    }

    /// <summary>A sideways drag across about the knob's width and a half sweeps from centre to one side.</summary>
    protected override double DragDelta(Vector delta) => delta.X / Math.Max(1, (Bounds.Width / 2) - 2);

    protected override Size MeasureOverride(Size availableSize) => new(2 * (Radius + 1), 2 * (Radius + 1));

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var radius = Radius;
        var centre = new Point(radius + 1, radius + 1);
        var fraction = (Math.Clamp(Value, -1, 1) + 1) / 2;
        ArcKnobPaint.Draw(context, centre, radius, fraction, bipolar: true, Palette.Accent, dim: !IsEnabled, _cache);
        if (IsFocused)
        {
            context.DrawEllipse(null, ConsolePaint.Pen(Palette.Focus), centre, radius + 1.5, radius + 1.5);
        }
    }
}
