using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A compact rotary parameter control for device panels. Drag vertically to change it (Shift for fine), scroll, or use the
/// arrow keys; double-click returns it to its default. A stepped parameter (<see cref="Step"/> above zero) moves one step per
/// key press or wheel notch and lands only on its steps when dragged; a continuous one moves 2% of its range (0.2% with Shift). Dragging writes the value live, so the change is heard as it is
/// made; the project's history coalesces consecutive edits of one parameter into a single undo step. A reset (double-click,
/// Delete, Backspace) runs <see cref="ResetCommand"/> when bound, so it is an undo step of its own. Keys with Ctrl, Alt, or
/// Meta, and a horizontal or Shift wheel, pass through to the window and any scroller around the knob.
/// </summary>
public sealed class Knob : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<Knob, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Knob, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<Knob, double>(nameof(DefaultValue));

    /// <summary>The distance between a stepped parameter's positions, or 0 (the default) for a continuous parameter.</summary>
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<Knob, double>(nameof(Step));

    /// <summary>The value arc's colour: the device's signal accent.</summary>
    public static readonly StyledProperty<Color> AccentProperty = AvaloniaProperty.Register<Knob, Color>(nameof(Accent), Palette.Accent);

    /// <summary>
    /// What a screen reader says the value is, with its unit (for example "-21.0 dB"); when unset, the plain number.
    /// </summary>
    public static readonly StyledProperty<string?> ValueTextProperty = AvaloniaProperty.Register<Knob, string?>(nameof(ValueText));

    /// <summary>
    /// How strongly the lit ticks glow (0-1): a tight halo with an exponential falloff. 0 (the default) keeps the knob calm;
    /// the selected device panel raises it, so the device you are working on is lit and the rest stay quiet.
    /// </summary>
    public static readonly StyledProperty<double> GlowProperty = AvaloniaProperty.Register<Knob, double>(nameof(Glow));

    /// <summary>What a reset runs; when unset, the value returns to <see cref="DefaultValue"/>.</summary>
    public static readonly StyledProperty<ICommand?> ResetCommandProperty = AvaloniaProperty.Register<Knob, ICommand?>(nameof(ResetCommand));

    private const double SweepDegrees = 270;
    private const double StartDegrees = 135;
    private const double PixelsForFullRange = 160;
    private double? _preview;
    private KnobAutomationPeer? _peer;
    private Point _dragStart;
    private double _dragStartValue;

    static Knob()
    {
        AffectsRender<Knob>(ValueProperty, MinimumProperty, MaximumProperty, AccentProperty, GlowProperty, IsFocusedProperty, IsEnabledProperty);
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

    public double Step
    {
        get => GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double Glow
    {
        get => GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    public string? ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public ICommand? ResetCommand
    {
        get => GetValue(ResetCommandProperty);
        set => SetValue(ResetCommandProperty, value);
    }

    private double Range => Math.Max(Maximum - Minimum, double.Epsilon);

    private string SpokenValue => ValueText ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Value:0.###}");

    private double Shown => _preview ?? Value;

    /// <summary>Sets the value from a fraction of the range, clamped; used by keys, the wheel, and tests.</summary>
    public void Nudge(double fraction) => Value = Math.Clamp(Value + (fraction * Range), Minimum, Maximum);

    /// <summary>Moves the value one key press or wheel notch up (positive <paramref name="direction"/>) or down.</summary>
    public void NudgeBy(int direction, bool fine)
    {
        if (Step > 0)
        {
            // A stepped parameter has nothing between its positions, so Shift moves a whole step too.
            var steps = Math.Round((Value - Minimum) / Step) + Math.Sign(direction);
            Value = Math.Clamp(Minimum + (steps * Step), Minimum, Maximum);
        }
        else
        {
            Nudge(Math.Sign(direction) * (fine ? 0.002 : 0.02));
        }
    }

    /// <summary>Returns to the default through <see cref="ResetCommand"/> when one is bound, else through the value.</summary>
    public void Reset()
    {
        _preview = null;
        if (ResetCommand is { } command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return;
        }

        Value = Math.Clamp(DefaultValue, Minimum, Maximum);
    }

    /// <summary>Clamps a value to the range and, for a stepped parameter, rounds it to the nearest step.</summary>
    private double Snap(double value) => Step > 0
        ? Math.Clamp(Minimum + (Math.Round((value - Minimum) / Step) * Step), Minimum, Maximum)
        : Math.Clamp(value, Minimum, Maximum);

    /// <summary>How many radial ticks a knob of this size draws over its 270° sweep: 21, or 15 below 30 DIP.</summary>
    internal static int TickCount(double size) => size >= 30 ? 21 : 15;

    /// <summary>
    /// Whether tick <paramref name="index"/> of <paramref name="count"/> is lit: every tick from the origin (the start, or the
    /// centre of a bipolar range) up to the value. The origin tick is always lit, so a value at its origin still shows a mark.
    /// </summary>
    internal static bool IsTickLit(int index, int count, double fraction, double origin)
    {
        var at = index / (double)(count - 1);
        var half = 0.5 / (count - 1);
        var lo = Math.Min(origin, fraction) - half;
        var hi = Math.Max(origin, fraction) + half;
        return at >= lo && at < hi;
    }

    /// <summary>The fraction of the range a value sits at, and the fill's origin (the centre of a bipolar range).</summary>
    internal (double Fraction, double Origin) Positions() =>
        (Math.Clamp((Shown - Minimum) / Range, 0, 1), Minimum < 0 && Maximum > 0 ? -Minimum / Range : 0);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 4)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size / 2) - 1.5;
        var (fraction, origin) = Positions();
        var enabled = IsEnabled;
        var accent = enabled ? Accent : Palette.TextMuted;
        var count = TickCount(size);
        var tickLength = size >= 30 ? 4 : 3;
        var inner = radius - tickLength;

        // The body sits inside the tick ring with a 1.5 DIP gap, so every tick reads on the panel.
        context.DrawEllipse(new SolidColorBrush(Palette.KnobBody), new Pen(new SolidColorBrush(Palette.Border), 1), center, inner - 1.5, inner - 1.5);

        // Thin radial ticks over the sweep: lit (accent) from the origin to the value, unlit ticks visible (≥3:1).
        var unlit = new Pen(new SolidColorBrush(enabled ? Palette.KnobTick : Palette.TextMuted, enabled ? 1 : 0.5), 1.2, lineCap: PenLineCap.Round);
        var lit = new Pen(new SolidColorBrush(accent), 1.6, lineCap: PenLineCap.Round);
        var glow = enabled ? Math.Clamp(Glow, 0, 1) : 0;

        // A tight halo on lit ticks only: two wider strokes whose opacity falls off as e^(−r/τ), τ ≈ reach/3 (reach 3 DIP).
        IPen? near = null, far = null;
        if (glow > 0)
        {
            near = new Pen(new SolidColorBrush(accent, 0.34 * glow * Math.Exp(-1.0)), 3.6, lineCap: PenLineCap.Round);
            far = new Pen(new SolidColorBrush(accent, 0.34 * glow * Math.Exp(-2.0)), 6.0, lineCap: PenLineCap.Round);
        }

        for (var i = 0; i < count; i++)
        {
            var angle = Radians(StartDegrees + (SweepDegrees * i / (count - 1)));
            var from = PointAt(center, inner, angle);
            var to = PointAt(center, radius, angle);
            if (IsTickLit(i, count, fraction, origin))
            {
                if (far is not null && near is not null)
                {
                    context.DrawLine(far, from, to);
                    context.DrawLine(near, from, to);
                }

                context.DrawLine(lit, from, to);
            }
            else
            {
                context.DrawLine(unlit, from, to);
            }
        }

        var pointerAngle = Radians(StartDegrees + (SweepDegrees * fraction));
        var pointerFrom = PointAt(center, (inner - 1.5) * 0.3, pointerAngle);
        var tip = PointAt(center, inner - 3, pointerAngle);
        context.DrawLine(new Pen(new SolidColorBrush(enabled ? Palette.Text : Palette.TextMuted), 1.6, lineCap: PenLineCap.Round), pointerFrom, tip);

        if (IsFocused)
        {
            // Focus is a solid 2 DIP outline just outside the ticks, never a glow.
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Palette.Focus), 2), center, radius + 1.5, radius + 1.5);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(34, 34);

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new KnobAutomationPeer(this);

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
            Reset();
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
        _preview = Snap(_dragStartValue + delta);
        SetCurrentValue(ValueProperty, _preview.Value);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

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
        // A horizontal or Shift wheel scrolls whatever holds the knob instead.
        if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.Y == 0)
        {
            return;
        }

        NudgeBy(Math.Sign(e.Delta.Y), fine: false);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None)
        {
            return;
        }

        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Up or Key.Right:
                NudgeBy(1, fine);
                e.Handled = true;
                break;
            case Key.Down or Key.Left:
                NudgeBy(-1, fine);
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
                Reset();
                e.Handled = true;
                break;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            AutomationProperties.SetHelpText(this, SpokenValue);
            _peer?.RaiseValueChanged(change.OldValue, change.NewValue);
        }
        else if (change.Property == ValueTextProperty)
        {
            AutomationProperties.SetHelpText(this, SpokenValue);
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

    /// <summary>Reports the knob as a slider with a settable range value, as the mixer's drawn value controls do.</summary>
    private sealed class KnobAutomationPeer(Knob owner) : ControlAutomationPeer(owner), IRangeValueProvider
    {
        private new Knob Owner => (Knob)base.Owner;

        public bool IsReadOnly => !Owner.IsEffectivelyEnabled;

        public double Minimum => Owner.Minimum;

        public double Maximum => Owner.Maximum;

        public double Value => Owner.Value;

        public double LargeChange => Owner.Step > 0 ? Owner.Step : Owner.Range * 0.1;

        public double SmallChange => Owner.Step > 0 ? Owner.Step : Owner.Range * 0.02;

        public void SetValue(double value)
        {
            if (!Owner.IsEffectivelyEnabled)
            {
                throw new ElementNotEnabledException();
            }

            Owner.Value = Owner.Snap(value);
        }

        public void RaiseValueChanged(object? oldValue, object? newValue) =>
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string? GetHelpTextCore() => Owner.SpokenValue;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static Point PointAt(Point center, double radius, double angle) => new(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
}
