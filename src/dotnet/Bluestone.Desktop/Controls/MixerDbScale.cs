using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// The scale between a strip's gain bar and its meter. The labels and right-hand ticks follow the meter's display mode
/// (dBFS, VU, K, or LUFS); the left-hand ticks mark the gain bar's own dB steps, which do not change with the mode. In
/// Peak + RMS the two sets coincide, as both are drawn on the same segment grid.
/// </summary>
public sealed class MixerDbScale : Control
{
    public static readonly StyledProperty<MixerMeterMode> ModeProperty = AvaloniaProperty.Register<MixerDbScale, MixerMeterMode>(nameof(Mode));

    private static readonly IPen TickPen = new ImmutablePen(MixerRail.Brush(Palette.Border), 1);
    private static readonly IPen UnityPen = new ImmutablePen(MixerRail.Brush(Palette.TextSecondary), 1);
    private static readonly IBrush LabelBrush = MixerRail.Brush(Palette.TextSecondary);
    private static readonly IBrush ZeroBrush = MixerRail.Brush(Palette.Text);
    private readonly Dictionary<(MixerMeterMode, double), FormattedText> _labels = [];
    private double[] _placed = [];

    static MixerDbScale()
    {
        AffectsRender<MixerDbScale>(ModeProperty);
    }

    public MixerMeterMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var metrics = MixerRail.Metrics(Bounds.Height, 1);
        var tick = MixerRail.TickLength;
        var width = Bounds.Width;
        foreach (var mark in MixerRail.Marks(MixerMeterMode.PeakRms))
        {
            var y = Snap(MixerRail.EdgeY(metrics, MixerRail.Fraction(mark, MixerRail.GainFloor, MixerRail.GainCeiling)));
            context.DrawLine(mark == 0 ? UnityPen : TickPen, new Point(0, y), new Point(tick, y));
        }

        var (floor, ceiling) = MixerRail.Range(Mode);
        var zero = Mode switch
        {
            MixerMeterMode.LufsS => -23,
            _ => 0,
        };
        // Every tick is drawn; labels are thinned when the rail is short so they never overlap. The reference label
        // (0, or -23 LUFS) is placed first so it always survives, then the rest fill in where they fit.
        var marks = MixerRail.Marks(Mode);
        if (_placed.Length < marks.Length)
        {
            _placed = new double[marks.Length];
        }

        var placed = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var mark in marks)
            {
                if ((mark == zero) != (pass == 0))
                {
                    continue;
                }

                var y = Snap(MixerRail.EdgeY(metrics, MixerRail.Fraction(mark, floor, ceiling)));
                context.DrawLine(TickPen, new Point(width - tick, y), new Point(width, y));

                var text = Label(mark, mark == zero);
                var top = Math.Clamp(Math.Round(y - (text.Height / 2)), 0, Math.Max(0, Bounds.Height - text.Height));
                if (Overlaps(top, text.Height, placed))
                {
                    continue;
                }

                _placed[placed++] = top;
                context.DrawText(text, new Point(Math.Round((width - text.Width) / 2), top));
            }
        }

        bool Overlaps(double top, double height, int count)
        {
            for (var i = 0; i < count; i++)
            {
                if (top < _placed[i] + height && _placed[i] < top + height)
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
            text = MixerRail.Text(MixerRail.Label(mark), reference ? ZeroBrush : LabelBrush);
            _labels[(Mode, mark)] = text;
        }

        return text;
    }

    private static double Snap(double y) => Math.Floor(y) + 0.5;
}
