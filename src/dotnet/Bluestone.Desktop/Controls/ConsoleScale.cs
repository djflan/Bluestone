using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The dB scale to the right of a strip's meter: a short tick off the meter's edge and a small mono label per mark, with
/// 0 a step brighter. It shares the fader zone's vertical geometry, so in Peak + RMS a mark sits level with the fader cap
/// and gain LEDs at the same dB. The other display modes (VU, K, LUFS) label their own linear range.
/// </summary>
public sealed class ConsoleScale : Control
{
    public static readonly StyledProperty<MixerMeterMode> ModeProperty = AvaloniaProperty.Register<ConsoleScale, MixerMeterMode>(nameof(Mode));

    private static readonly double[] PeakMarks = [6, 0, -6, -12, -24, -36, -48, -60];
    private readonly Dictionary<(MixerMeterMode, double), FormattedText> _labels = [];
    private double[] _placed = new double[16];

    static ConsoleScale() => AffectsRender<ConsoleScale>(ModeProperty);

    public MixerMeterMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    /// <summary>The marks drawn for a mode, top to bottom.</summary>
    internal static double[] Marks(MixerMeterMode mode) => mode == MixerMeterMode.PeakRms ? PeakMarks : MixerRail.Marks(mode);

    public override void Render(DrawingContext context)
    {
        var top = ConsoleRail.TravelTop;
        var bottom = Bounds.Height - ConsoleRail.TravelBottomInset;
        if (bottom <= top)
        {
            return;
        }

        var reference = Mode == MixerMeterMode.LufsS ? -23 : 0;
        var marks = Marks(Mode);
        if (_placed.Length < marks.Length)
        {
            _placed = new double[marks.Length];
        }

        var placed = 0;

        // The reference label is placed first so it always survives; the rest fill in where they fit on a short rail.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var mark in marks)
            {
                var isReference = mark == reference;
                if (isReference != (pass == 0))
                {
                    continue;
                }

                var sy = ConsoleRail.Y(top, bottom, ConsoleRail.Fraction(Mode, mark));
                ConsolePaint.HLine(context, ConsolePaint.Alpha(ConsolePaint.White, isReference ? 0.35 : 0.14), 1, 4, Math.Round(sy) + 0.5);
                var text = Label(mark, isReference);
                var y = Math.Round(sy - 6 + ((12 - text.Height) / 2));
                if (Overlaps(y, text.Height, placed))
                {
                    continue;
                }

                _placed[placed++] = y;
                context.DrawText(text, new Point(5, y));
            }
        }

        bool Overlaps(double y, double height, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (y < _placed[i] + height - 1 && _placed[i] < y + height - 1)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private FormattedText Label(double mark, bool reference)
    {
        if (!_labels.TryGetValue((Mode, mark), out var text))
        {
            var value = mark > 0
                ? string.Create(CultureInfo.InvariantCulture, $"+{mark:0}")
                : mark.ToString("0", CultureInfo.InvariantCulture).Replace("-", "\u2212", StringComparison.Ordinal);
            text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, ConsolePaint.Typeface("Font.Mono"), 8,
                ConsolePaint.Brush(reference ? Palette.TextSecondary : Palette.TextMuted));
            _labels[(Mode, mark)] = text;
        }

        return text;
    }
}
