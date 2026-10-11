using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A compact rotary parameter control for device panels. Drag vertically to change it (Shift for fine), scroll, or use the
/// arrow keys; double-click returns it to its default. While dragging, the knob previews the value and commits it once on
/// release, so one gesture is one undo step.
/// </summary>
public sealed class Knob : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Knob, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<Knob, double>(nameof(DefaultValue));

    /// <summary>The value arc's colour: the device's signal accent.</summary>
    public static readonly StyledProperty<Color> AccentProperty = AvaloniaProperty.Register<Knob, Color>(nameof(Accent), Palette.Accent);

    private const double SweepDegrees = 270;
    private const double StartDegrees = 135;
    private const double PixelsForFullRange = 160;
    private double? _preview;
    private Point _dragStart;
    private double _dragStartValue;

    static Knob()
    {
        AffectsRender<Knob>(ValueProperty, MinimumProperty, MaximumProperty, AccentProperty, IsFocusedProperty, IsEnabledProperty);
        FocusableProperty.OverrideDefaultValue<Knob>(true);
        CursorProperty.OverrideDefaultValue<Knob>(new Cursor(StandardCursorType.SizeNorthSouth));
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double DefaultValue
    {
        get => GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private double Range => Math.Max(Maximum - Minimum, double.Epsilon);

    private double Shown => _preview ?? Value;

    /// <summary>Sets the value from a fraction of the range, clamped; used by keys, the wheel, and tests.</summary>
    public void Nudge(double fraction) => Value = Math.Clamp(Value + (fraction * Range), Minimum, Maximum);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size / 2) - 2.5;
        var fraction = Math.Clamp((Shown - Minimum) / Range, 0, 1);
        var accent = IsEnabled ? Accent : Palette.TextMuted;

        context.DrawEllipse(new SolidColorBrush(Palette.Field), null, center, radius - 3, radius - 3);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Palette.Border), 2.5, lineCap: PenLineCap.Round), Arc(center, radius, 0, 1));

        // A bipolar range (−x to +x) fills from the centre; a unipolar one from the start.
        var origin = Minimum < 0 && Maximum > 0 ? -Minimum / Range : 0;
        if (Math.Abs(fraction - origin) > 0.001)
        {
            context.DrawGeometry(null, new Pen(new SolidColorBrush(accent), 2.5, lineCap: PenLineCap.Round), Arc(center, radius, Math.Min(origin, fraction), Math.Max(origin, fraction)));
        }

        var angle = Radians(StartDegrees + (SweepDegrees * fraction));
        var inner = PointAt(center, radius * 0.25, angle);
        var tip = PointAt(center, radius - 4.5, angle);
        context.DrawLine(new Pen(new SolidColorBrush(IsEnabled ? Palette.Text : Palette.TextMuted), 1.6, lineCap: PenLineCap.Round), inner, tip);

        if (IsFocused)
        {
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.Focus, 0.7), 1), center, radius + 1.5, radius + 1.5);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(34, 34);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus();
        if (e.ClickCount == 2)
        {
            _preview = null;
            Value = Math.Clamp(DefaultValue, Minimum, Maximum);
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragStartValue = Value;
        _preview = Value;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_preview is null || !Equals(e.Pointer.Captured, this))
        {
            return;
        }

        var scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1;
        var delta = (_dragStart.Y - e.GetPosition(this).Y) / PixelsForFullRange * Range * scale;
        _preview = Math.Clamp(_dragStartValue + delta, Minimum, Maximum);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Commit();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Commit();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y != 0)
        {
            Nudge(Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.002 : 0.02));
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.002 : 0.02;
        switch (e.Key)
        {
            case Key.Up or Key.Right:
                Nudge(step);
                e.Handled = true;
                break;
            case Key.Down or Key.Left:
                Nudge(-step);
                e.Handled = true;
                break;
            case Key.Home:
                Value = Minimum;
                e.Handled = true;
                break;
            case Key.End:
                Value = Maximum;
                e.Handled = true;
                break;
            case Key.Back or Key.Delete:
                Value = Math.Clamp(DefaultValue, Minimum, Maximum);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            AutomationProperties.SetHelpText(this, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Value:0.###}"));
        }
    }

    private void Commit()
    {
        if (_preview is { } value)
        {
            _preview = null;
            if (value != Value)
            {
                Value = value;
            }

            InvalidateVisual();
        }
    }

    private static StreamGeometry Arc(Point center, double radius, double from, double to)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var start = Radians(StartDegrees + (SweepDegrees * from));
        var end = Radians(StartDegrees + (SweepDegrees * to));
        context.BeginFigure(PointAt(center, radius, start), false);
        context.ArcTo(PointAt(center, radius, end), new Size(radius, radius), 0, (to - from) * SweepDegrees > 180, SweepDirection.Clockwise);
        context.EndFigure(false);
        return geometry;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static Point PointAt(Point center, double radius, double angle) => new(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
}
