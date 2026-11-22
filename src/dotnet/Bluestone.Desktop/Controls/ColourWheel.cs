using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The colour picker's hue and saturation wheel: a continuous disc in a round console well, hue round the rim (red at the
/// top, running clockwise) and saturation out from the white centre. The whole disc darkens with <see cref="Brightness"/>,
/// so it always shows the colours it offers. A click or drag puts the thumb there; with focus, Left and Right turn the hue
/// (5°, Shift for 1°) and Up and Down change the saturation (5%, Shift for 1%).
/// </summary>
public sealed class ColourWheel : Control
{
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<ColourWheel, double>(nameof(Hue), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<ColourWheel, double>(nameof(Saturation), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>How bright the disc is drawn (0 to 1): the brightness fader's value.</summary>
    public static readonly StyledProperty<double> BrightnessProperty = AvaloniaProperty.Register<ColourWheel, double>(nameof(Brightness), 1);

    private const int Wedges = 180;
    private const double Rim = 5;
    private const double ThumbRadius = 6;

    private static readonly IImmutableSolidColorBrush WellBrush = new ImmutableSolidColorBrush(Palette.Well);
    private static readonly ImmutablePen EdgePen = new(new ImmutableSolidColorBrush(Palette.Edge), 1);
    private static readonly ImmutablePen LipPen = new(new ImmutableSolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
    private static readonly ImmutablePen TickPen = new(new ImmutableSolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
    private static readonly ImmutablePen ThumbKeyline = new(new ImmutableSolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1);
    private static readonly ImmutablePen ThumbRing = new(Brushes.White.ToImmutable(), 2);
    private static readonly ImmutablePen FocusPen = new(new ImmutableSolidColorBrush(Palette.Focus), 1.5);

    private (Point Centre, double Radius, (Geometry Wedge, IImmutableBrush Fill)[] Disc)? _disc;
    private bool _dragging;
    private ColourWheelPeer? _peer;

    static ColourWheel()
    {
        AffectsRender<ColourWheel>(HueProperty, SaturationProperty, BrightnessProperty, IsFocusedProperty);
        FocusableProperty.OverrideDefaultValue<ColourWheel>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ColourWheel>(false);
        CursorProperty.OverrideDefaultValue<ColourWheel>(new Cursor(StandardCursorType.Cross));
    }

    public ColourWheel()
    {
        AutomationProperties.SetName(this, "Hue and saturation");
        UpdateHelpText();
    }

    /// <summary>Degrees, 0 (red, at the top) to 360, clockwise.</summary>
    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }

    /// <summary>0 (white, at the centre) to 1 (the rim).</summary>
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }

    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    /// <summary>What a screen reader hears: "Hue 292°, saturation 60%".</summary>
    public string ValueText => string.Create(CultureInfo.InvariantCulture, $"Hue {Math.Round(Hue) % 360:0}°, saturation {Saturation * 100:0}%");

    private Point Centre => new(Bounds.Width / 2, Bounds.Height / 2);

    private double Outer => (Math.Min(Bounds.Width, Bounds.Height) / 2) - 1;

    private double DiscRadius => Math.Max(1, Outer - Rim);

    /// <summary>Where the thumb sits for a hue and saturation, in the wheel's coordinates.</summary>
    public Point PointFor(double hue, double saturation) => Polar(Centre, Math.Clamp(saturation, 0, 1) * DiscRadius, hue);

    /// <summary>Sets the hue and saturation under <paramref name="point"/>; outside the disc it takes the rim's.</summary>
    public void SetFromPoint(Point point)
    {
        var d = point - Centre;
        var distance = Math.Sqrt((d.X * d.X) + (d.Y * d.Y));
        if (distance > 0.5)
        {
            // Angle from the top, clockwise.
            SetCurrentValue(HueProperty, ((Math.Atan2(d.X, -d.Y) * 180 / Math.PI) + 360) % 360);
        }

        SetCurrentValue(SaturationProperty, Math.Clamp(distance / DiscRadius, 0, 1));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HueProperty || change.Property == SaturationProperty)
        {
            UpdateHelpText();
            _peer?.Changed(ValueText);
        }
        else if (change.Property == BoundsProperty)
        {
            _disc = null;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new ColourWheelPeer(this);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Focus(NavigationMethod.Pointer);
        _dragging = true;
        e.Pointer.Capture(this);
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && Equals(e.Pointer.Captured, this))
        {
            SetFromPoint(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if ((e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None)
        {
            return;
        }

        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var hueStep = fine ? 1.0 : 5.0;
        var saturationStep = fine ? 0.01 : 0.05;
        switch (e.Key)
        {
            case Key.Right:
                SetCurrentValue(HueProperty, (Hue + hueStep) % 360);
                break;
            case Key.Left:
                SetCurrentValue(HueProperty, ((Hue - hueStep) % 360 + 360) % 360);
                break;
            case Key.Up:
                SetCurrentValue(SaturationProperty, Math.Clamp(Math.Round((Saturation + saturationStep) * 100) / 100, 0, 1));
                break;
            case Key.Down:
                SetCurrentValue(SaturationProperty, Math.Clamp(Math.Round((Saturation - saturationStep) * 100) / 100, 0, 1));
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var c = Centre;
        var outer = Outer;
        if (outer <= Rim)
        {
            return;
        }

        // The well the disc sits in, with a faint lower lip, as the console's wells.
        context.DrawEllipse(WellBrush, EdgePen, c, outer, outer);
        context.DrawEllipse(null, LipPen, c + new Point(0, 0.75), outer - 0.5, outer - 0.5);

        var r = DiscRadius;
        var disc = _disc is { } cached && cached.Centre == c && cached.Radius == r ? cached.Disc : BuildDisc(c, r);
        _disc = (c, r, disc);

        // Under the wedges, white, so no hairline seam shows between them.
        context.DrawEllipse(Brushes.White, null, c, r * 0.6, r * 0.6);
        foreach (var (wedge, shade) in disc)
        {
            context.DrawGeometry(shade, null, wedge);
        }

        // Brightness darkens the whole disc: value v is the full colour at v, which over sRGB is black at 1 - v on top.
        var dark = 1 - Math.Clamp(Brightness, 0, 1);
        if (dark > 0.002)
        {
            context.DrawEllipse(new ImmutableSolidColorBrush(Colors.Black, dark), null, c, r + 0.5, r + 0.5);
        }

        // Faint 30° ticks round the rim: the wheel reads as an instrument, not a texture.
        for (var a = 0; a < 360; a += 30)
        {
            context.DrawLine(TickPen, Polar(c, outer - 0.5, a), Polar(c, outer - 3, a));
        }

        var colour = new ColourHsv(Hue, Saturation, Brightness).ToColor();
        var p = PointFor(Hue, Saturation);
        Glow.Halo(context, new Rect(p.X - ThumbRadius, p.Y - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2), ThumbRadius, colour, 0.45, 8);
        var fill = new ImmutableSolidColorBrush(colour);
        context.DrawEllipse(fill, ThumbKeyline, p, ThumbRadius + 2.5, ThumbRadius + 2.5);
        context.DrawEllipse(null, ThumbRing, p, ThumbRadius + 1, ThumbRadius + 1);
        context.DrawEllipse(fill, null, p, ThumbRadius, ThumbRadius);

        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            // Keyboard focus: a solid ring clear of the rim, never a glow.
            context.DrawEllipse(null, FocusPen, c, outer + 3, outer + 3);
        }
    }

    // The disc as wedges, each a radial white-to-hue gradient, so hue 0 is up and runs clockwise whatever a conic brush does.
    private static (Geometry, IImmutableBrush)[] BuildDisc(Point c, double r)
    {
        var disc = new (Geometry, IImmutableBrush)[Wedges];
        var step = 360.0 / Wedges;
        for (var i = 0; i < Wedges; i++)
        {
            var a0 = (i * step) - 0.35;
            var a1 = ((i + 1) * step) + 0.35;
            var wedge = new StreamGeometry();
            using (var g = wedge.Open())
            {
                g.BeginFigure(c, true);
                g.LineTo(Polar(c, r, a0));
                g.ArcTo(Polar(c, r, a1), new Size(r, r), 0, false, SweepDirection.Clockwise);
                g.EndFigure(true);
            }

            var fill = new RadialGradientBrush
            {
                Center = new RelativePoint(c, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(c, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(r, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(r, RelativeUnit.Absolute),
                GradientStops = [new GradientStop(Colors.White, 0), new GradientStop(new ColourHsv((i + 0.5) * step, 1, 1).ToColor(), 1)],
            }.ToImmutable();
            disc[i] = (wedge, fill);
        }

        return disc;
    }

    private static Point Polar(Point c, double r, double degreesFromTop) =>
        new(c.X + (r * Math.Sin(degreesFromTop * Math.PI / 180)), c.Y - (r * Math.Cos(degreesFromTop * Math.PI / 180)));

    private void UpdateHelpText() =>
        AutomationProperties.SetHelpText(this, ValueText + ". Left or Right turns the hue, Up or Down changes the saturation; Shift for finer steps.");

    /// <summary>Announced as a slider-like control whose value reads as the hue and saturation.</summary>
    private sealed class ColourWheelPeer(ColourWheel owner) : ControlAutomationPeer(owner), IValueProvider
    {
        private string _last = owner.ValueText;

        public bool IsReadOnly => true;

        public string? Value => ((ColourWheel)Owner).ValueText;

        public void SetValue(string? value) => throw new InvalidOperationException("Set the hue and saturation with the wheel or its keys.");

        public void Changed(string now)
        {
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, _last, now);
            _last = now;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
    }
}
