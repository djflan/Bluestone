using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A flat text key (mode switches such as Auto or Limiter, and segmented choices): a dark rounded face with a light
/// label; on takes a little of the accent, a soft bloom behind the label, an accent rim, and an accent-white label. No lamp beside it.
/// With <see cref="IsAction"/> it is a plain action key instead (Add, Reset): pressing it never checks it, and a screen
/// reader hears a button it can invoke, not a toggle.
/// </summary>
public sealed class TextKey : ToggleButton
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<TextKey, string>(nameof(Text), string.Empty);

    /// <summary>An action key: it runs its command and never toggles, and is announced as a button.</summary>
    public static readonly StyledProperty<bool> IsActionProperty = AvaloniaProperty.Register<TextKey, bool>(nameof(IsAction));

    private const double Radius = 3;
    private const double FontPx = 10;
    private const double PadX = 8;
    private FormattedText? _label;
    private IImmutableBrush? _bloom;
    private (string, bool) _labelKey;

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public bool IsAction { get => GetValue(IsActionProperty); set => SetValue(IsActionProperty, value); }

    static TextKey()
    {
        AffectsRender<TextKey>(TextProperty, IsCheckedProperty, IsPointerOverProperty, IsPressedProperty, IsEffectivelyEnabledProperty);
        CursorProperty.OverrideDefaultValue<TextKey>(new Cursor(StandardCursorType.Hand));
        AffectsMeasure<TextKey>(TextProperty);
    }

    public override void Render(DrawingContext context)
    {
        var r = ConsolePaint.Snap(new Rect(Bounds.Size));
        var face = new RoundedRect(r, Radius);
        var on = IsChecked == true;
        if (on)
        {
            // A lit mode key: the face takes a little of the accent and a soft bloom from behind the label.
            var fill = ConsolePaint.Mix(Palette.Key, Palette.Accent, IsPointerOver ? 0.22 : 0.16);
            context.DrawRectangle(ConsolePaint.Brush(IsPressed ? ConsolePaint.Darker(fill, 0.15) : fill), null, face);
            using (context.PushClip(face))
            {
                _bloom ??= new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(ConsolePaint.Alpha(Palette.Accent, Palette.GlowAlpha * 0.5), 0),
                        new GradientStop(ConsolePaint.Alpha(Palette.Accent, Palette.GlowAlpha * 0.5 * 0.35), 0.45),
                        new GradientStop(ConsolePaint.Alpha(Palette.Accent, 0), 1),
                    },
                }.ToImmutable();
                var radius = r.Width * 0.5;
                context.DrawEllipse(_bloom, null, r.Center, radius, radius);
            }
        }
        else
        {
            var key = IsPressed ? ConsolePaint.Darker(Palette.Key, 0.15) : IsPointerOver ? ConsolePaint.Lighter(Palette.Key, 0.07) : Palette.Key;
            context.DrawRectangle(ConsolePaint.Brush(key), null, face);
        }

        ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, on ? 0.10 : 0.06), r.X + 2, r.Right - 2, r.Y + 0.5);
        context.DrawRectangle(null, ConsolePaint.Pen(on ? ConsolePaint.Alpha(Palette.Accent, 0.45) : ConsolePaint.Alpha(ConsolePaint.Black, 0.55)), new RoundedRect(r.Deflate(0.5), Radius));
        var label = Label(IsChecked == true);
        using (context.PushOpacity(IsEffectivelyEnabled ? 1 : 0.45))
        {
            context.DrawText(label, new Point(r.X + Math.Round((r.Width - label.Width) / 2), r.Y + ((r.Height - label.Height) / 2)));
        }

        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            context.DrawRectangle(null, ConsolePaint.Pen(Palette.Focus), new RoundedRect(r.Inflate(2), Radius + 2));
        }
    }

    protected override Type StyleKeyOverride => typeof(TextKey);

    protected override void Toggle()
    {
        if (!IsAction)
        {
            base.Toggle();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => IsAction ? new ButtonAutomationPeer(this) : base.OnCreateAutomationPeer();

    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Label(false);
        return new Size(Math.Ceiling(label.Width) + (2 * PadX), 20);
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    private FormattedText Label(bool on)
    {
        if (_label is null || _labelKey != (Text, on))
        {
            _label = new FormattedText(Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                ConsolePaint.Typeface("Font.Ui", FontWeight.SemiBold), FontPx, ConsolePaint.Brush(on ? ConsolePaint.Lighter(Palette.Accent, 0.15) : Palette.TextSecondary));
            _labelKey = (Text, on);
        }

        return _label;
    }
}
