using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>What a <see cref="SquareKey"/> shows on its face.</summary>
public enum SquareKeyKind
{
    /// <summary>A letter (M, S).</summary>
    Letter,

    /// <summary>A record dot: red on the dark face when off, white on the red fill when on.</summary>
    Record,

    /// <summary>A small speaker (input monitoring).</summary>
    Monitor,
}

/// <summary>
/// A compact square track key (mute, solo, record, monitor), as on a console's channel strip: off is a dark grey face
/// with a light letter and a faint top lip; on is a solid fill in the key's colour throwing its light past the face (<see cref="LedLight"/>) while the LED glow is on. Solo's yellow takes
/// dark ink; every other lit key takes white. Turned off, the lit face fades out over <see cref="FadeMs"/>
/// (<see cref="LedMotion"/>). A toggle button, so it keeps its accessible name and checked state.
/// </summary>
public sealed class SquareKey : ToggleButton
{
    public static readonly StyledProperty<SquareKeyKind> KindProperty = AvaloniaProperty.Register<SquareKey, SquareKeyKind>(nameof(Kind));

    /// <summary>The letter on a <see cref="SquareKeyKind.Letter"/> key.</summary>
    public static readonly StyledProperty<string> GlyphProperty = AvaloniaProperty.Register<SquareKey, string>(nameof(Glyph), string.Empty);

    /// <summary>The fill when on.</summary>
    public static readonly StyledProperty<Color> OnColorProperty = AvaloniaProperty.Register<SquareKey, Color>(nameof(OnColor), Palette.Accent);

    /// <summary>The face size; the key is square.</summary>
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<SquareKey, double>(nameof(Size), 15);

    private const double Radius = 2;
    /// <summary>How long the lit face takes to fade out, in milliseconds.</summary>
    internal const double FadeMs = 60;

    private const double LitLight = 0.9;
    private readonly FormattedText?[] _letters = new FormattedText?[2];
    private readonly (string, uint, double)[] _letterKeys = new (string, uint, double)[2];
    private readonly LedLevels _lamp;

    static SquareKey()
    {
        AffectsRender<SquareKey>(KindProperty, GlyphProperty, OnColorProperty, SizeProperty, IsCheckedProperty, IsPointerOverProperty, IsPressedProperty, IsEnabledProperty);
        AffectsMeasure<SquareKey>(SizeProperty);
        CursorProperty.OverrideDefaultValue<SquareKey>(new Cursor(StandardCursorType.Hand));
    }

    public SquareKey()
    {
        _lamp = new LedLevels(this, FadeMs, count: 1);

        // A lit key's light reaches past its face onto the strip; clipped to the key, it would never show.
        ClipToBounds = false;
    }

    /// <summary>How lit the face is (for tests).</summary>
    internal LedLevels Lamp => _lamp;

    public SquareKeyKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public string Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public Color OnColor { get => GetValue(OnColorProperty); set => SetValue(OnColorProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override Type StyleKeyOverride => typeof(SquareKey);

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty)
        {
            _lamp.Set(0, IsChecked == true, LedMotion.Now);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Glow.Changed += OnGlowChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Glow.Changed -= OnGlowChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnGlowChanged(object? sender, EventArgs e) => InvalidateVisual();

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
        var size = Size;
        var r = ConsolePaint.Snap(new Rect(Math.Round((Bounds.Width - size) / 2), Math.Round((Bounds.Height - size) / 2), size, size));
        var face = new RoundedRect(r, Radius);
        var on = IsChecked == true;
        var fading = on ? 0 : _lamp.Step(0);
        using (context.PushOpacity(IsEnabled ? 1 : 0.45))
        {
            Face(context, r, face, on);
            if (fading > 0)
            {
                // Turned off a moment ago: the lit face over the dark one, fading out.
                using (context.PushOpacity(fading / (double)LedLevels.Steps))
                {
                    Face(context, r, face, true);
                }
            }
        }

        if (IsFocused && Classes.Contains(":focus-visible"))
        {
            context.DrawRectangle(null, ConsolePaint.Pen(Palette.Focus), new RoundedRect(r.Inflate(2), Radius + 2));
        }
    }

    private void Face(DrawingContext context, Rect r, RoundedRect face, bool on)
    {
        var size = Size;
        if (on)
        {
            var fill = ConsolePaint.Vertical(ConsolePaint.Lighter(OnColor, IsPointerOver ? 0.16 : 0.08), ConsolePaint.Darker(OnColor, IsPressed ? 0.2 : 0.08));
            LedLight.For(OnColor, r.Size, Radius, ellipse: false, LitLight)?.Draw(context, r);
            context.DrawRectangle(fill, null, face);
        }
        else
        {
            var key = IsPressed ? ConsolePaint.Darker(Palette.Key, 0.15) : IsPointerOver ? ConsolePaint.Lighter(Palette.Key, 0.07) : Palette.Key;
            context.DrawRectangle(ConsolePaint.Vertical(ConsolePaint.Lighter(key, 0.04), key), null, face);
            ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, 0.07), r.X + 1, r.Right - 1, r.Y + 0.5);
        }

        context.DrawRectangle(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.55)), new RoundedRect(r.Inflate(0.5), Radius + 0.5));
        // Solo's yellow and mute's blue are light faces: dark ink keeps the letter at 4.5:1 or better.
        var ink = on ? (OnColor == Palette.Solo ? Palette.SoloInk : OnColor == Palette.Mute ? Palette.Base : ConsolePaint.White) : Palette.Text;
        switch (Kind)
        {
            case SquareKeyKind.Record:
                context.DrawEllipse(ConsolePaint.Brush(on ? ConsolePaint.White : Palette.Record), null, r.Center, r.Width * 0.2, r.Width * 0.2);
                break;
            case SquareKeyKind.Monitor:
                Speaker(context, r, ink);
                break;
            default:
                var text = Letter(on ? 1 : 0, ink, size < 15 ? 9 : 10);
                context.DrawText(text, new Point(r.X + Math.Round((r.Width - text.Width) / 2), r.Y + ((r.Height - text.Height) / 2)));
                break;
        }
    }

    // One cached letter for each face, so a fade draws both without rebuilding either.
    private FormattedText Letter(int slot, Color ink, double fontSize)
    {
        var key = (Glyph, ink.ToUInt32(), fontSize);
        if (_letters[slot] is not { } letter || _letterKeys[slot] != key)
        {
            letter = new FormattedText(Glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                ConsolePaint.Typeface("Font.Ui", FontWeight.Bold), fontSize, ConsolePaint.Brush(ink));
            _letters[slot] = letter;
            _letterKeys[slot] = key;
        }

        return letter;
    }

    private static void Speaker(DrawingContext context, Rect r, Color ink)
    {
        var c = r.Center;
        var k = r.Width / 16;
        var body = new StreamGeometry();
        using (var shape = body.Open())
        {
            shape.BeginFigure(new Point(c.X - (4 * k), c.Y - (1.6 * k)), true);
            shape.LineTo(new Point(c.X - (1.6 * k), c.Y - (1.6 * k)));
            shape.LineTo(new Point(c.X + (1.4 * k), c.Y - (4.2 * k)));
            shape.LineTo(new Point(c.X + (1.4 * k), c.Y + (4.2 * k)));
            shape.LineTo(new Point(c.X - (1.6 * k), c.Y + (1.6 * k)));
            shape.LineTo(new Point(c.X - (4 * k), c.Y + (1.6 * k)));
            shape.EndFigure(true);
        }

        context.DrawGeometry(ConsolePaint.Brush(ink), null, body);
        context.DrawGeometry(null, ConsolePaint.Pen(ink, 1, PenLineCap.Round), ConsolePaint.Arc(new Point(c.X + (1.4 * k), c.Y), 3 * k, -45, 90));
    }
}
