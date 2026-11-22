using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The power (enable/bypass) glyph as a toggle: an open ring with a stem, lit accent when on and muted when off. There is
/// no lamp beside it; the glyph itself is the indicator. <see cref="Bloom"/> adds the soft lit halo a device header uses.
/// Turned off, the lit glyph fades out over <see cref="FadeMs"/> (<see cref="LedMotion"/>).
/// </summary>
public sealed class PowerGlyph : ToggleButton
{
    public static readonly StyledProperty<double> GlyphSizeProperty = AvaloniaProperty.Register<PowerGlyph, double>(nameof(GlyphSize), 8);

    public static readonly StyledProperty<bool> BloomProperty = AvaloniaProperty.Register<PowerGlyph, bool>(nameof(Bloom));

    private StreamGeometry? _ring;
    private (Point, double) _ringKey;
    private IBrush? _bloom;
    private readonly LedLevels _lamp;

    public PowerGlyph() => _lamp = new LedLevels(this, FadeMs, count: 1);

    /// <summary>How long the lit glyph takes to fade out, in milliseconds.</summary>
    internal const double FadeMs = 60;

    /// <summary>How lit the glyph is (for tests).</summary>
    internal LedLevels Lamp => _lamp;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty)
        {
            _lamp.Set(0, IsChecked == true, LedMotion.Now);
        }
    }

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
        if (_ring is null || _ringKey != (centre, size))
        {
            _ring = ConsolePaint.Arc(centre, size * 0.40, -60, 300);
            _ringKey = (centre, size);
        }

        Glyph(context, centre, size, on);
        var fading = on ? 0 : _lamp.Step(0);
        if (fading > 0)
        {
            // Turned off a moment ago: the lit glyph over the dark one, fading out.
            using (context.PushOpacity(fading / (double)LedLevels.Steps))
            {
                Glyph(context, centre, size, true);
            }
        }

        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            context.DrawEllipse(null, ConsolePaint.Pen(Palette.Focus), centre, (size / 2) + 2, (size / 2) + 2);
        }
    }

    private void Glyph(DrawingContext context, Point centre, double size, bool on)
    {
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

        ConsolePaint.Power(context, centre, size, color, _ring!);
    }
}
