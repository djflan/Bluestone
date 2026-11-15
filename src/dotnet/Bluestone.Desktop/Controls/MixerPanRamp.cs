using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A strip's pan as a small horizontal bar with an inlaid ramp that widens toward each side; a fill in the track's colour
/// grows from the centre along the ramp to the pan position, thicker the further it goes. A Slider has a thumb on a line
/// and cannot show a bipolar amount this way. −1 is hard left, 0 centre, +1 hard right. Drag sideways, use the keys or
/// wheel, or double-click (or Home) to centre.
/// </summary>
public sealed class MixerPanRamp : MixerValueControl
{
    /// <summary>The track's colour index for the fill, or −1 for the neutral accent (Master).</summary>
    public static readonly StyledProperty<int> ColorIndexProperty = AvaloniaProperty.Register<MixerPanRamp, int>(nameof(ColorIndex), -1);

    private static readonly IBrush FieldBrush = MixerRail.Brush(Palette.Field);
    private static readonly IPen BorderPen = new Avalonia.Media.Immutable.ImmutablePen(MixerRail.Brush(Palette.Border), 1);
    private static readonly IPen HoverPen = new Avalonia.Media.Immutable.ImmutablePen(MixerRail.Brush(Palette.TextMuted), 1);
    private static readonly IPen FocusPen = new Avalonia.Media.Immutable.ImmutablePen(MixerRail.Brush(Palette.Focus), 1);
    private static readonly IBrush InlayBrush = MixerRail.Brush(Palette.Mix(Palette.Raised, Palette.TextMuted, 0.34));
    private static readonly IPen EndPen = new Avalonia.Media.Immutable.ImmutablePen(MixerRail.Brush(Palette.Text), 1);
    private static readonly IPen CentrePen = new Avalonia.Media.Immutable.ImmutablePen(MixerRail.Brush(Palette.TextSecondary), 1);
    private static readonly IBrush DisabledBrush = MixerRail.Brush(Palette.TextMuted);
    private IBrush? _fill;

    static MixerPanRamp()
    {
        AffectsRender<MixerPanRamp>(ColorIndexProperty);
        CursorProperty.OverrideDefaultValue<MixerPanRamp>(new Cursor(StandardCursorType.SizeWestEast));
        MinimumProperty.OverrideDefaultValue<MixerPanRamp>(-1);
        MaximumProperty.OverrideDefaultValue<MixerPanRamp>(1);
        SmallChangeProperty.OverrideDefaultValue<MixerPanRamp>(0.02);
        LargeChangeProperty.OverrideDefaultValue<MixerPanRamp>(0.25);
    }

    public int ColorIndex
    {
        get => GetValue(ColorIndexProperty);
        set => SetValue(ColorIndexProperty, value);
    }

    protected override string ValueText
    {
        get
        {
            var amount = (int)Math.Round(Math.Abs(Value) * 100);
            return amount == 0 ? "Centre" : Value < 0 ? Invariant($"{amount}% left") : Invariant($"{amount}% right");
        }
    }

    protected override double DragDelta(Vector delta) => delta.X / Math.Max(1, (Bounds.Width / 2) - Inset);

    private static double Inset => MixerRail.Number("Space.XXS", 2);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColorIndexProperty)
        {
            _fill = null;
        }
    }

    public override void Render(DrawingContext context)
    {
        var inset = Inset;
        var radius = MixerRail.Radius("Radius.Small", 2);
        var box = new Rect(0.5, inset + 0.5, Math.Max(0, Bounds.Width - 1), Math.Max(0, Bounds.Height - (2 * inset) - 1));
        context.DrawRectangle(FieldBrush, IsFocused ? FocusPen : IsPointerOver || IsDragging ? HoverPen : BorderPen, box, radius, radius);

        var centre = box.Center;
        var half = (box.Width / 2) - 3;
        var edge = (box.Height / 2) - 1.5;
        if (half <= 0 || edge <= 0)
        {
            return;
        }

        context.DrawGeometry(InlayBrush, null, Wedge(centre, centre.X - half, edge));
        context.DrawGeometry(InlayBrush, null, Wedge(centre, centre.X + half, edge));

        var amount = Math.Clamp(Value, -1, 1);
        if (Math.Abs(amount) > 0.004)
        {
            var end = Math.Round(centre.X + (half * amount));
            var thickness = 0.5 + ((edge - 0.5) * Math.Abs(amount));
            _fill ??= MixerRail.Brush(Palette.Lighten(ColorIndex >= 0 ? Palette.Track(ColorIndex) : Palette.Accent, 0.28));
            context.DrawGeometry(IsEnabled ? _fill : DisabledBrush, null, Wedge(centre, end, thickness));
            context.DrawLine(EndPen, new Point(end + 0.5, centre.Y - thickness), new Point(end + 0.5, centre.Y + thickness));
        }

        var x = Math.Floor(centre.X) + 0.5;
        context.DrawLine(CentrePen, new Point(x, centre.Y - 2), new Point(x, centre.Y + 2));
    }

    /// <summary>A ramp from a point at the centre to a vertical edge of half-height <paramref name="edge"/> at <paramref name="end"/>.</summary>
    private static StreamGeometry Wedge(Point centre, double end, double edge)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new Point(centre.X, centre.Y - 0.5), true);
        path.LineTo(new Point(end, centre.Y - edge));
        path.LineTo(new Point(end, centre.Y + edge));
        path.LineTo(new Point(centre.X, centre.Y + 0.5));
        path.EndFigure(true);
        return geometry;
    }
}
