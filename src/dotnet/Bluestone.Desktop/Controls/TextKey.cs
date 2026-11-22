using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A flat text key (mode switches such as Auto or Limiter, and segmented choices): a dark rounded face with a light
/// label; on fills with the accent and the label turns dark. No lamp beside it.
/// </summary>
public sealed class TextKey : ToggleButton
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<TextKey, string>(nameof(Text), string.Empty);

    private const double Radius = 3;
    private const double FontPx = 10;
    private const double PadX = 8;
    private FormattedText? _label;
    private (string, bool) _labelKey;

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    static TextKey()
    {
        AffectsRender<TextKey>(TextProperty, IsCheckedProperty, IsPointerOverProperty, IsPressedProperty, IsEnabledProperty);
        CursorProperty.OverrideDefaultValue<TextKey>(new Cursor(StandardCursorType.Hand));
        AffectsMeasure<TextKey>(TextProperty);
    }

    public override void Render(DrawingContext context)
    {
        var r = ConsolePaint.Snap(new Rect(Bounds.Size));
        var face = new RoundedRect(r, Radius);
        if (IsChecked == true)
        {
            var c = IsPointerOver ? ConsolePaint.Lighter(Palette.Accent, 0.1) : Palette.Accent;
            context.DrawRectangle(ConsolePaint.Vertical(ConsolePaint.Lighter(c, 0.08), ConsolePaint.Darker(c, 0.08)), null, face);
            Glow.Rect(context, r, Radius, Palette.Accent, 0.45);
            context.DrawRectangle(ConsolePaint.Vertical(ConsolePaint.Lighter(c, 0.08), ConsolePaint.Darker(c, 0.08)), null, face);
        }
        else
        {
            var key = IsPressed ? ConsolePaint.Darker(Palette.Key, 0.15) : IsPointerOver ? ConsolePaint.Lighter(Palette.Key, 0.07) : Palette.Key;
            context.DrawRectangle(ConsolePaint.Vertical(ConsolePaint.Lighter(key, 0.04), key), null, face);
            ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, 0.07), r.X + 2, r.Right - 2, r.Y + 0.5);
        }

        context.DrawRectangle(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.55)), new RoundedRect(r.Inflate(0.5), Radius + 0.5));
        var label = Label(IsChecked == true);
        using (context.PushOpacity(IsEnabled ? 1 : 0.45))
        {
            context.DrawText(label, new Point(r.X + Math.Round((r.Width - label.Width) / 2), r.Y + ((r.Height - label.Height) / 2)));
        }

        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            context.DrawRectangle(null, ConsolePaint.Pen(Palette.Focus), new RoundedRect(r.Inflate(2), Radius + 2));
        }
    }

    protected override Type StyleKeyOverride => typeof(TextKey);

    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Label(false);
        return new Size(Math.Ceiling(label.Width) + (2 * PadX), 18);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    private FormattedText Label(bool on)
    {
        if (_label is null || _labelKey != (Text, on))
        {
            _label = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                ConsolePaint.Typeface("Font.Ui", FontWeight.SemiBold), FontPx, ConsolePaint.Brush(on ? Palette.SoloInk : Palette.Text));
            _labelKey = (Text, on);
        }

        return _label;
    }
}
