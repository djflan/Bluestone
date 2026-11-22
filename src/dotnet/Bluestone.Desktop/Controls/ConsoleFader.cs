using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// A console fader: a dark slot down the travel and a 14×24 dark-satin cap with brushed grain, fine grip ridges, a soft
/// shadow onto the track and a lit cyan index line at the gain. The travel follows the console taper
/// (<see cref="ConsoleRail.Position"/>, −60 to +6 dB), the same curve as the meter and scale beside it, so the index
/// at unity sits level with 0 on the scale. Drag vertically (Shift for fine), use the keys or wheel, or double-click for
/// unity. The whole column takes the pointer, not only the cap.
/// </summary>
public sealed class ConsoleFader : MixerValueControl
{
    public const double CapWidth = 14;
    public const double CapHeight = 24;
    private const double CapRadius = 2.5;
    private const double IndexTier = 0.6;

    private static readonly Color SlotColor = Palette.Slot;
    private static readonly Color BodyTop = Color.Parse("#5C6068");
    private static readonly Color BodyBottom = Color.Parse("#2E3036");
    private static readonly Color UpperTop = Color.Parse("#4A4D54");
    private static readonly Color UpperBottom = Color.Parse("#3A3C42");
    private static readonly Color LowerTop = Color.Parse("#35373D");
    private static readonly Color LowerBottom = Color.Parse("#2B2D32");

    static ConsoleFader()
    {
        CursorProperty.OverrideDefaultValue<ConsoleFader>(new Cursor(StandardCursorType.SizeNorthSouth));
        SmallChangeProperty.OverrideDefaultValue<ConsoleFader>(1);
        LargeChangeProperty.OverrideDefaultValue<ConsoleFader>(6);
    }

    protected override string ValueText => Value <= Minimum ? "−∞ dB" : Invariant($"{Value:+0.0;-0.0;0.0} dB");

    /// <summary>A drag from below the travel's floor starts at the floor, so the first movement is visible.</summary>
    protected override double DragOrigin => Math.Max(Start, Math.Min(MixerRail.GainFloor, Maximum));

    /// <summary>End goes to the top of the travel (+6 dB), not the strip's full boost.</summary>
    protected override double KeyMaximum => Math.Max(Minimum, Math.Min(Maximum, MixerRail.GainCeiling));

    private static double TravelTop => ConsoleRail.TravelTop;

    private double TravelBottom => Math.Max(TravelTop + 1, Bounds.Height - ConsoleRail.TravelBottomInset);

    /// <summary>Where the cap's index sits for the current value.</summary>
    internal double IndexY => ConsoleRail.Y(TravelTop, TravelBottom, ConsoleRail.Position(Value));

    protected override double DragDelta(Vector delta)
    {
        var origin = DragStartValue;
        var travel = Math.Max(1, TravelBottom - TravelTop);
        var fraction = Math.Clamp(ConsoleRail.Position(origin) - (delta.Y / travel), 0, 1);

        // The foot of the travel is −∞, as on a console: the strip's minimum, not the scale's lowest mark.
        return (fraction <= 0 ? Minimum : ConsoleRail.Db(fraction)) - origin;
    }

    protected override Size MeasureOverride(Size availableSize) => new(CapWidth + 6, 0);

    public override void Render(DrawingContext context)
    {
        // The whole column takes the pointer, not only the slim slot.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var centre = Math.Round(Bounds.Width / 2);
        var top = TravelTop;
        var bottom = TravelBottom;
        var slot = new Rect(centre - 2, top - 2, 4, bottom - top + 4);
        context.DrawRectangle(ConsolePaint.Brush(SlotColor), ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.White, 0.05)), new RoundedRect(slot, 2));

        if (Value > MixerRail.GainCeiling)
        {
            // Boost beyond the travel: a small caret above the slot, so +6 and +18 dB do not look the same.
            var caret = new StreamGeometry();
            using (var shape = caret.Open())
            {
                shape.BeginFigure(new Point(centre - 3, top - 5), true);
                shape.LineTo(new Point(centre, top - 9));
                shape.LineTo(new Point(centre + 3, top - 5));
                shape.EndFigure(true);
            }

            context.DrawGeometry(ConsolePaint.Brush(Palette.MixerMeterHot), null, caret);
        }

        using (context.PushOpacity(IsEnabled ? 1 : 0.5))
        {
            Cap(context, new Point(centre, IndexY));
        }
    }

    private void Cap(DrawingContext context, Point centre)
    {
        var r = ConsolePaint.Snap(new Rect(centre.X - (CapWidth / 2), centre.Y - (CapHeight / 2), CapWidth, CapHeight));
        var shadow = ConsolePaint.Brush(ConsolePaint.Alpha(ConsolePaint.Black, 0.12));
        for (var k = 4; k >= 1; k--)
        {
            context.DrawRectangle(shadow, null, new RoundedRect(r.Translate(new Vector(0, 1 + k)).Inflate(k * 0.7), CapRadius + (k * 0.5)));
        }

        var body = new RoundedRect(r, CapRadius);
        context.DrawRectangle(ConsolePaint.Vertical(BodyTop, BodyBottom), null, body);
        using (context.PushClip(body))
        {
            context.FillRectangle(ConsolePaint.Vertical(UpperTop, UpperBottom), new Rect(r.X + 1.5, r.Y + 2, CapWidth - 3, (CapHeight / 2) - 3));
            context.FillRectangle(ConsolePaint.Vertical(LowerTop, LowerBottom), new Rect(r.X + 1.5, r.Y + (CapHeight / 2) + 1, CapWidth - 3, (CapHeight / 2) - 3));
            for (var x = r.X + 2; x < r.Right - 2; x += 1)
            {
                var alpha = 0.015 + (0.02 * ConsolePaint.Noise((int)(x * 7), 3));
                ConsolePaint.VLine(context, ConsolePaint.Alpha(ConsolePaint.White, alpha), x + 0.5, r.Y + 1, r.Bottom - 1);
            }

            for (var k = 0; k < 4; k++)
            {
                Ridge(context, r, r.Y + 3.5 + (k * 2));
                Ridge(context, r, r.Bottom - 3.5 - (k * 2));
            }

            ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, 0.14), r.X + 2, r.Right - 2, r.Y + 0.5);
        }

        var iy = Math.Round(centre.Y) + 0.5;
        Glow.Line(context, new Point(r.X + 1.5, iy), new Point(r.Right - 1.5, iy), 1.2, Palette.Accent, IndexTier);
        ConsolePaint.HLine(context, ConsolePaint.Lighter(Palette.Accent, 0.2), r.X + 1.5, r.Right - 1.5, iy);
        context.DrawRectangle(null, ConsolePaint.Pen(ConsolePaint.Alpha(ConsolePaint.Black, 0.7)), new RoundedRect(r.Deflate(0.5), CapRadius));

        if (IsFocused)
        {
            context.DrawRectangle(null, ConsolePaint.Pen(Palette.Focus), new RoundedRect(r.Inflate(2.5), CapRadius + 2));
        }
    }

    private static void Ridge(DrawingContext context, Rect r, double y)
    {
        ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.Black, 0.35), r.X + 2, r.Right - 2, y);
        ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, 0.05), r.X + 2, r.Right - 2, y + 1);
    }
}
