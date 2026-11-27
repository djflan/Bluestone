using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The colour picker's brightness, as a small console fader: a gutter graded from black up to the full colour and a cap
/// with a lit line in the colour chosen. <see cref="MixerValueControl.Value"/> is the brightness in percent (0 to 100). It
/// takes the fader's gestures: drag (Shift for fine), the wheel, Up and Down (Shift for fine), Page Up and Down, Home and
/// End, double-click for full; a click on the travel away from the cap moves the cap there first.
/// </summary>
public sealed class BrightnessFader : MixerValueControl
{
    public static readonly StyledProperty<double> HueProperty = AvaloniaProperty.Register<BrightnessFader, double>(nameof(Hue));

    public static readonly StyledProperty<double> SaturationProperty = AvaloniaProperty.Register<BrightnessFader, double>(nameof(Saturation));

    private const double TrackWidth = 10;
    private const double TrackInset = 4;
    private const double CapHeight = 12;

    private static readonly ImmutablePen TickPen = new(new ImmutableSolidColorBrush(Palette.BorderStrong), 1);
    private static readonly ImmutablePen CapEdge = new(new ImmutableSolidColorBrush(Palette.Edge), 1);
    private static readonly IImmutableSolidColorBrush CapShadow = new ImmutableSolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IImmutableBrush CapFace = ConsolePaint.Vertical(Palette.Overlay, Palette.Raised);
    private static readonly ImmutablePen FocusPen = new(new ImmutableSolidColorBrush(Palette.Focus), 1.5);

    static BrightnessFader()
    {
        AffectsRender<BrightnessFader>(HueProperty, SaturationProperty);
        MaximumProperty.OverrideDefaultValue<BrightnessFader>(100);
        DefaultValueProperty.OverrideDefaultValue<BrightnessFader>(100);
        SmallChangeProperty.OverrideDefaultValue<BrightnessFader>(1);
        LargeChangeProperty.OverrideDefaultValue<BrightnessFader>(10);
        CursorProperty.OverrideDefaultValue<BrightnessFader>(new Cursor(StandardCursorType.SizeNorthSouth));
        ClipToBoundsProperty.OverrideDefaultValue<BrightnessFader>(false);
    }

    public BrightnessFader() => AutomationProperties.SetName(this, "Brightness");

    /// <summary>The hue the travel is graded in (degrees).</summary>
    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }

    /// <summary>The saturation the travel is graded in (0 to 1).</summary>
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }

    protected override string ValueText => string.Create(CultureInfo.InvariantCulture, $"{Value:0}%");

    private Rect Track => new((Bounds.Width / 2) - (TrackWidth / 2), TrackInset, TrackWidth, Math.Max(1, Bounds.Height - (2 * TrackInset)));

    /// <summary>The cap's centre line for <paramref name="value"/> (percent), in the fader's coordinates.</summary>
    public double YFor(double value) => Track.Top + (Track.Height * (1 - (Math.Clamp(value, Minimum, Maximum) / Math.Max(1e-9, Maximum - Minimum))));

    protected override double DragDelta(Vector delta) => -delta.Y / Track.Height * (Maximum - Minimum);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // A press on the travel away from the cap moves the cap there, then drags from there.
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 1 && Math.Abs(point.Position.Y - YFor(Value)) > CapHeight / 2)
        {
            SetFromUser(Minimum + ((1 - ((point.Position.Y - Track.Top) / Track.Height)) * (Maximum - Minimum)));
        }

        base.OnPointerPressed(e);
    }

    private (Color Colour, IImmutableBrush? Brush) _track;
    private (Color Colour, IPen[]? Glow, IPen? Pen, int GlowVersion) _mark;

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var track = Track;
        ConsolePaint.Gutter(context, track.Inflate(2), 4);
        var full = new ColourHsv(Hue, Saturation, 1).ToColor();
        // The colours here change with every drag, so their brushes are this fader's own, rebuilt only when they change,
        // never the shared caches (which would keep every colour tried for the life of the app).
        if (_track.Colour != full || _track.Brush is null)
        {
            _track = (full, ConsolePaint.CreateVertical(full, Colors.Black));
        }

        context.DrawRectangle(_track.Brush, null, new RoundedRect(track, 2.5));
        for (var i = 0; i <= 4; i++)
        {
            var y = Math.Round(track.Top + (track.Height * i / 4)) + 0.5;
            context.DrawLine(TickPen, new Point(0, y), new Point(3, y));
            context.DrawLine(TickPen, new Point(w - 3, y), new Point(w, y));
        }

        var capY = YFor(Value);
        var cap = new Rect(1, capY - (CapHeight / 2), w - 2, CapHeight);
        context.DrawRectangle(CapShadow, null, new RoundedRect(cap.Translate(new Vector(0, 1.5)), 2.5));
        context.DrawRectangle(CapFace, CapEdge, new RoundedRect(cap, 2.5));
        var mark = new ColourHsv(Hue, Saturation, Value / Math.Max(1e-9, Maximum)).ToColor();
        var from = new Point(cap.Left + 4, capY);
        var to = new Point(cap.Right - 4, capY);
        // The glow pens hold the glow strength they were built at, so a glow change rebuilds them too.
        if (_mark.Colour != mark || _mark.Glow is null || _mark.Pen is null || _mark.GlowVersion != Glow.Version)
        {
            _mark = (mark, Glow.CreatePens(mark, 1.4, 2), new ImmutablePen(new ImmutableSolidColorBrush(mark), 2), Glow.Version);
        }

        Glow.Line(context, from, to, _mark.Glow);
        context.DrawLine(_mark.Pen, from, to);

        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            context.DrawRectangle(null, FocusPen, new RoundedRect(new Rect(Bounds.Size).Inflate(2.5), 5));
        }
    }
}
