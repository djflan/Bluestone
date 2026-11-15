using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Bluestone.Audio;
using Bluestone.Desktop.Theme;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// Draws the selected Mixer meter value as paired bars or round arcs. Peak/RMS remains a separate, unchanged
/// <see cref="LevelMeter"/> so switching presentation never changes DSP, peak hold, or readiness.
/// </summary>
internal sealed class MixerMeterFace : Control
{
    public static readonly StyledProperty<MixerMeterMode> ModeProperty =
        AvaloniaProperty.Register<MixerMeterFace, MixerMeterMode>(nameof(Mode));

    public static readonly StyledProperty<MixerMeterPresentation> PresentationProperty =
        AvaloniaProperty.Register<MixerMeterFace, MixerMeterPresentation>(nameof(Presentation));

    public static readonly StyledProperty<double> LeftValueProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(LeftValue));

    public static readonly StyledProperty<double> RightValueProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(RightValue));

    public static readonly StyledProperty<double> LeftPeakHoldProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(LeftPeakHold));

    public static readonly StyledProperty<double> RightPeakHoldProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(RightPeakHold));

    public static readonly StyledProperty<bool> IsReadyProperty =
        AvaloniaProperty.Register<MixerMeterFace, bool>(nameof(IsReady));

    public static readonly StyledProperty<double> PeakFloorProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(PeakFloor));

    public static readonly StyledProperty<double> PeakCeilingProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(PeakCeiling));

    public static readonly StyledProperty<double> VuFloorProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(VuFloor));

    public static readonly StyledProperty<double> VuCeilingProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(VuCeiling));

    public static readonly StyledProperty<double> KFloorProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(KFloor));

    public static readonly StyledProperty<double> KCeilingProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(KCeiling));

    public static readonly StyledProperty<double> LufsFloorProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(LufsFloor));

    public static readonly StyledProperty<double> LufsCeilingProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(LufsCeiling));

    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(BarGap));

    public static readonly StyledProperty<double> BarCornerRadiusProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(BarCornerRadius));

    public static readonly StyledProperty<double> RoundStrokeProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(RoundStroke));

    public static readonly StyledProperty<double> PeakHoldStrokeProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(PeakHoldStroke));

    public static readonly StyledProperty<double> RoundStartAngleProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(RoundStartAngle));

    public static readonly StyledProperty<double> RoundSweepAngleProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(RoundSweepAngle));

    public static readonly StyledProperty<double> HotFractionProperty =
        AvaloniaProperty.Register<MixerMeterFace, double>(nameof(HotFraction));

    static MixerMeterFace()
    {
        AffectsRender<MixerMeterFace>(
            ModeProperty,
            PresentationProperty,
            LeftValueProperty,
            RightValueProperty,
            LeftPeakHoldProperty,
            RightPeakHoldProperty,
            IsReadyProperty,
            PeakFloorProperty,
            PeakCeilingProperty,
            VuFloorProperty,
            VuCeilingProperty,
            KFloorProperty,
            KCeilingProperty,
            LufsFloorProperty,
            LufsCeilingProperty,
            BarGapProperty,
            BarCornerRadiusProperty,
            RoundStrokeProperty,
            PeakHoldStrokeProperty,
            RoundStartAngleProperty,
            RoundSweepAngleProperty,
            HotFractionProperty);
    }

    public MixerMeterMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public MixerMeterPresentation Presentation
    {
        get => GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public double LeftValue
    {
        get => GetValue(LeftValueProperty);
        set => SetValue(LeftValueProperty, value);
    }

    public double RightValue
    {
        get => GetValue(RightValueProperty);
        set => SetValue(RightValueProperty, value);
    }

    public double LeftPeakHold
    {
        get => GetValue(LeftPeakHoldProperty);
        set => SetValue(LeftPeakHoldProperty, value);
    }

    public double RightPeakHold
    {
        get => GetValue(RightPeakHoldProperty);
        set => SetValue(RightPeakHoldProperty, value);
    }

    public bool IsReady
    {
        get => GetValue(IsReadyProperty);
        set => SetValue(IsReadyProperty, value);
    }

    public double PeakFloor
    {
        get => GetValue(PeakFloorProperty);
        set => SetValue(PeakFloorProperty, value);
    }

    public double PeakCeiling
    {
        get => GetValue(PeakCeilingProperty);
        set => SetValue(PeakCeilingProperty, value);
    }

    public double VuFloor
    {
        get => GetValue(VuFloorProperty);
        set => SetValue(VuFloorProperty, value);
    }

    public double VuCeiling
    {
        get => GetValue(VuCeilingProperty);
        set => SetValue(VuCeilingProperty, value);
    }

    public double KFloor
    {
        get => GetValue(KFloorProperty);
        set => SetValue(KFloorProperty, value);
    }

    public double KCeiling
    {
        get => GetValue(KCeilingProperty);
        set => SetValue(KCeilingProperty, value);
    }

    public double LufsFloor
    {
        get => GetValue(LufsFloorProperty);
        set => SetValue(LufsFloorProperty, value);
    }

    public double LufsCeiling
    {
        get => GetValue(LufsCeilingProperty);
        set => SetValue(LufsCeilingProperty, value);
    }

    public double BarGap
    {
        get => GetValue(BarGapProperty);
        set => SetValue(BarGapProperty, value);
    }

    public double BarCornerRadius
    {
        get => GetValue(BarCornerRadiusProperty);
        set => SetValue(BarCornerRadiusProperty, value);
    }

    public double RoundStroke
    {
        get => GetValue(RoundStrokeProperty);
        set => SetValue(RoundStrokeProperty, value);
    }

    public double PeakHoldStroke
    {
        get => GetValue(PeakHoldStrokeProperty);
        set => SetValue(PeakHoldStrokeProperty, value);
    }

    public double RoundStartAngle
    {
        get => GetValue(RoundStartAngleProperty);
        set => SetValue(RoundStartAngleProperty, value);
    }

    public double RoundSweepAngle
    {
        get => GetValue(RoundSweepAngleProperty);
        set => SetValue(RoundSweepAngleProperty, value);
    }

    public double HotFraction
    {
        get => GetValue(HotFractionProperty);
        set => SetValue(HotFractionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var (floor, ceiling) = Scale();
        if (Presentation == MixerMeterPresentation.Round)
        {
            DrawRound(context, floor, ceiling);
        }
        else
        {
            DrawBars(context, floor, ceiling);
        }
    }

    private (double Floor, double Ceiling) Scale() => Mode switch
    {
        MixerMeterMode.Vu => (VuFloor, VuCeiling),
        MixerMeterMode.K20 or MixerMeterMode.K14 or MixerMeterMode.K12 => (KFloor, KCeiling),
        MixerMeterMode.LufsS => (LufsFloor, LufsCeiling),
        _ => (PeakFloor, PeakCeiling),
    };

    private void DrawBars(DrawingContext context, double floor, double ceiling)
    {
        var gap = Math.Max(0, BarGap);
        var width = (Bounds.Width - gap) / 2;
        if (width <= 0)
        {
            return;
        }

        DrawBar(context, new Rect(0, 0, width, Bounds.Height), LeftValue, LeftPeakHold, floor, ceiling);
        DrawBar(context, new Rect(width + gap, 0, width, Bounds.Height), RightValue, RightPeakHold, floor, ceiling);
    }

    private void DrawBar(DrawingContext context, Rect bounds, double value, double peakHold, double floor, double ceiling)
    {
        var track = new SolidColorBrush(Palette.MixerMeterTrack);
        context.DrawRectangle(track, null, bounds, BarCornerRadius, BarCornerRadius);
        if (!IsReady || ceiling <= floor)
        {
            return;
        }

        if (double.IsFinite(value))
        {
            var fraction = Math.Clamp((value - floor) / (ceiling - floor), 0, 1);
            var height = bounds.Height * fraction;
            if (height > 0)
            {
                var color = fraction >= HotFraction ? Palette.MixerMeterHot : Palette.MixerMeterFill;
                var fill = new Rect(bounds.X, bounds.Bottom - height, bounds.Width, height);
                context.DrawRectangle(new SolidColorBrush(color), null, fill, BarCornerRadius, BarCornerRadius);
            }

        }

        DrawBarPeakHold(context, bounds, peakHold, floor, ceiling);
    }

    private void DrawRound(DrawingContext context, double floor, double ceiling)
    {
        var gap = Math.Max(0, BarGap);
        var diameter = Math.Min(Bounds.Height, (Bounds.Width - gap) / 2);
        var radius = (diameter - RoundStroke) / 2;
        if (radius <= 0)
        {
            return;
        }

        var firstCenterX = diameter / 2;
        var centerY = Bounds.Height / 2;
        DrawArc(context, new Point(firstCenterX, centerY), radius, LeftValue, LeftPeakHold, floor, ceiling);
        DrawArc(context, new Point(firstCenterX + diameter + gap, centerY), radius, RightValue, RightPeakHold, floor, ceiling);
    }

    private void DrawArc(DrawingContext context, Point center, double radius, double value, double peakHold, double floor, double ceiling)
    {
        var track = new Pen(new SolidColorBrush(Palette.MixerMeterTrack), RoundStroke, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, track, Arc(center, radius, 1));
        if (!IsReady || ceiling <= floor)
        {
            return;
        }

        if (double.IsFinite(value))
        {
            var fraction = Math.Clamp((value - floor) / (ceiling - floor), 0, 1);
            if (fraction > 0)
            {
                var color = fraction >= HotFraction ? Palette.MixerMeterHot : Palette.MixerMeterFill;
                var fill = new Pen(new SolidColorBrush(color), RoundStroke, lineCap: PenLineCap.Round);
                context.DrawGeometry(null, fill, Arc(center, radius, fraction));
            }
        }

        DrawRoundPeakHold(context, center, radius, peakHold, floor, ceiling);
    }

    private void DrawBarPeakHold(DrawingContext context, Rect bounds, double peakHold, double floor, double ceiling)
    {
        if (!double.IsFinite(peakHold) || peakHold <= floor || ceiling <= floor)
        {
            return;
        }

        var fraction = Math.Clamp((peakHold - floor) / (ceiling - floor), 0, 1);
        var y = bounds.Bottom - (bounds.Height * fraction);
        context.DrawLine(new Pen(new SolidColorBrush(Palette.MixerMeterPeakHold), PeakHoldStroke),
            new Point(bounds.X, y), new Point(bounds.Right, y));
    }

    private void DrawRoundPeakHold(DrawingContext context, Point center, double radius, double peakHold, double floor, double ceiling)
    {
        if (!double.IsFinite(peakHold) || peakHold <= floor || ceiling <= floor)
        {
            return;
        }

        var fraction = Math.Clamp((peakHold - floor) / (ceiling - floor), 0, 1);
        var angle = Radians(RoundStartAngle + (RoundSweepAngle * fraction));
        var halfStroke = RoundStroke / 2;
        context.DrawLine(new Pen(new SolidColorBrush(Palette.MixerMeterPeakHold), PeakHoldStroke),
            PointAt(center, radius - halfStroke, angle),
            PointAt(center, radius + halfStroke, angle));
    }

    private StreamGeometry Arc(Point center, double radius, double fraction)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var startAngle = Radians(RoundStartAngle);
        var sweep = RoundSweepAngle * fraction;
        var endAngle = Radians(RoundStartAngle + sweep);
        context.BeginFigure(PointAt(center, radius, startAngle), false);
        context.ArcTo(PointAt(center, radius, endAngle), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
        context.EndFigure(false);
        return geometry;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle)));
}
