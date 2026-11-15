using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The interaction a mixer strip's drawn value controls share (<see cref="MixerGainBar"/>, <see cref="MixerPanRamp"/>):
/// relative pointer drag (Shift for fine), the wheel, arrow keys (Shift for fine), Page Up/Down for large steps, Home and End
/// for the minimum and maximum, and double-click, Delete, or Backspace to return to the default. A drag previews live and writes the value as it goes,
/// as the device-panel <see cref="Knob"/> does, so the change is heard while it is made. The value is only clamped when the
/// user changes it; a bound value outside the range is shown at the nearest end and is never written back.
/// </summary>
public abstract class MixerValueControl : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<MixerValueControl, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<MixerValueControl, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<MixerValueControl, double>(nameof(Maximum), 1);

    public static readonly StyledProperty<double> DefaultValueProperty = AvaloniaProperty.Register<MixerValueControl, double>(nameof(DefaultValue));

    public static readonly StyledProperty<double> SmallChangeProperty = AvaloniaProperty.Register<MixerValueControl, double>(nameof(SmallChange), 1);

    public static readonly StyledProperty<double> LargeChangeProperty = AvaloniaProperty.Register<MixerValueControl, double>(nameof(LargeChange), 6);

    private MixerValueAutomationPeer? _peer;
    private Point _dragStart;
    private double _dragStartValue;
    private bool _dragging;

    static MixerValueControl()
    {
        AffectsRender<MixerValueControl>(ValueProperty, MinimumProperty, MaximumProperty, IsFocusedProperty, IsEnabledProperty, IsPointerOverProperty);
        FocusableProperty.OverrideDefaultValue<MixerValueControl>(true);
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

    public double SmallChange
    {
        get => GetValue(SmallChangeProperty);
        set => SetValue(SmallChangeProperty, value);
    }

    public double LargeChange
    {
        get => GetValue(LargeChangeProperty);
        set => SetValue(LargeChangeProperty, value);
    }

    /// <summary>Whether a pointer drag is in progress, for drawing the pressed state.</summary>
    protected bool IsDragging => _dragging;

    /// <summary>The value the user asked for, clamped to the range.</summary>
    public void SetFromUser(double value)
    {
        var clamped = Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));
        if (clamped != Value)
        {
            Value = clamped;
        }
    }

    /// <summary>Moves the value by <paramref name="steps"/> small changes (a tenth of one with <paramref name="fine"/>).</summary>
    public void Step(int steps, bool fine) => SetFromUser(Start + (steps * SmallChange * (fine ? 0.1 : 1)));

    /// <summary>The value a gesture moves from: the bound value, brought into the shown range.</summary>
    protected virtual double Start => Math.Clamp(Value, Minimum, Math.Max(Minimum, Maximum));

    /// <summary>How far the value moves for a pointer movement of <paramref name="delta"/> from the press point.</summary>
    protected abstract double DragDelta(Vector delta);

    /// <summary>What a screen reader says the value is.</summary>
    protected abstract string ValueText { get; }

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new MixerValueAutomationPeer(this);

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
            _dragging = false;
            SetFromUser(DefaultValue);
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragStartValue = Start;
        _dragging = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || !Equals(e.Pointer.Captured, this))
        {
            return;
        }

        var scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1;
        SetFromUser(_dragStartValue + (DragDelta(e.GetPosition(this) - _dragStart) * scale));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta != 0)
        {
            Step(Math.Sign(delta), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Up or Key.Right:
                Step(1, fine);
                e.Handled = true;
                break;
            case Key.Down or Key.Left:
                Step(-1, fine);
                e.Handled = true;
                break;
            case Key.PageUp:
                SetFromUser(Start + LargeChange);
                e.Handled = true;
                break;
            case Key.PageDown:
                SetFromUser(Start - LargeChange);
                e.Handled = true;
                break;
            case Key.Home:
                SetFromUser(Minimum);
                e.Handled = true;
                break;
            case Key.End:
                SetFromUser(Maximum);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back:
                SetFromUser(DefaultValue);
                e.Handled = true;
                break;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            AutomationProperties.SetHelpText(this, ValueText);
            _peer?.RaiseValueChanged(change.OldValue, change.NewValue);
        }
    }

    private void EndDrag()
    {
        if (_dragging)
        {
            _dragging = false;
            InvalidateVisual();
        }
    }

    /// <summary>Reports the control as a slider with a settable range value.</summary>
    private sealed class MixerValueAutomationPeer(MixerValueControl owner) : ControlAutomationPeer(owner), IRangeValueProvider
    {
        private new MixerValueControl Owner => (MixerValueControl)base.Owner;

        public bool IsReadOnly => !Owner.IsEnabled;

        public double Minimum => Owner.Minimum;

        public double Maximum => Owner.Maximum;

        public double Value => Owner.Value;

        public double LargeChange => Owner.LargeChange;

        public double SmallChange => Owner.SmallChange;

        public void SetValue(double value) => Owner.SetFromUser(value);

        public void RaiseValueChanged(object? oldValue, object? newValue) =>
            RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override string? GetHelpTextCore() => Owner.ValueText;
    }

    /// <summary>Formats a number for <see cref="ValueText"/> without the current culture's separators.</summary>
    protected static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
