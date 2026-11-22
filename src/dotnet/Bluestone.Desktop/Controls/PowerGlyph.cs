using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The power (enable/bypass) glyph as a toggle: an open ring with a stem, lit accent when on and muted when off. There is
/// no lamp beside it; the glyph itself is the indicator. <see cref="Bloom"/> adds the soft lit halo a device header uses.
/// </summary>
public sealed class PowerGlyph : ToggleButton
{
    public static readonly StyledProperty<double> GlyphSizeProperty = AvaloniaProperty.Register<PowerGlyph, double>(nameof(GlyphSize), 8);

    public static readonly StyledProperty<bool> BloomProperty = AvaloniaProperty.Register<PowerGlyph, bool>(nameof(Bloom));

    private StreamGeometry? _ring;
    private (Point, double) _ringKey;
    private IBrush? _bloom;

    static PowerGlyph()
    {
        AffectsRender<PowerGlyph>(GlyphSizeProperty, BloomProperty, IsCheckedProperty, IsPointerOverProperty, IsEnabledProperty);
        CursorProperty.OverrideDefaultValue<PowerGlyph>(new Cursor(StandardCursorType.Hand));
    }

    public double GlyphSize { get => GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }

    public bool Bloom { get => GetValue(BloomProperty); set => SetValue(BloomProperty, value); }

    protected override Type StyleKeyOverride => typeof(PowerGlyph);

    protected override Size MeasureOverride(Size availableSize) => new(GlyphSize + 2, GlyphSize + 2);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // Transparent hit area across the whole bounds.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var size = GlyphSize;
        var centre = new Point(Math.Round(Bounds.Width / 2), Math.Round(Bounds.Height / 2));
        var on = IsChecked == true;
        var color = on ? Palette.Accent : IsPointerOver ? Palette.TextSecondary : Palette.TextMuted;
        if (!IsEnabled)
        {
            color = ConsolePaint.Alpha(color, 0.5);
        }

        if (on && Bloom)
        {
            _bloom ??= new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(ConsolePaint.Alpha(Palette.Accent, Palette.GlowAlpha), 0),
                    new GradientStop(ConsolePaint.Alpha(Palette.Accent, Palette.GlowAlpha * 0.35), 0.45),
                    new GradientStop(ConsolePaint.Alpha(Palette.Accent, 0), 1),
                },
            }.ToImmutable();
            context.DrawEllipse(_bloom, null, centre, size * 0.92, size * 0.92);
        }

        if (_ring is null || _ringKey != (centre, size))
        {
            _ring = ConsolePaint.Arc(centre, size * 0.40, -60, 300);
            _ringKey = (centre, size);
        }

        ConsolePaint.Power(context, centre, size, color, _ring);
        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            context.DrawEllipse(null, ConsolePaint.Pen(Palette.Focus), centre, (size / 2) + 2, (size / 2) + 2);
        }
    }
}
