using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Bluestone.Application.Editing;
using Bluestone.Desktop.Theme;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Desktop.Controls;

/// <summary>
/// Shared drawing for musical time: grid lines (bars, beats, and grid steps when there is room) and
/// the bar ruler with its cycle strip. Positions are in pixels from the left of the content, so
/// <c>x = tick × pixelsPerQuarter / PPQN − scroll</c>.
/// </summary>
internal static class TimeGrid
{
    public const double CycleStripHeight = 11;

    private static readonly IPen BarPen = new Pen(new SolidColorBrush(Palette.GridBar), 1);
    private static readonly IPen BeatPen = new Pen(new SolidColorBrush(Palette.GridBeat), 1);
    private static readonly IPen StepPen = new Pen(new SolidColorBrush(Palette.GridStep), 1);
    private static readonly IBrush RulerBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#323235"), 0), new GradientStop(Color.Parse("#29292C"), 1) },
    };

    private static readonly IBrush CycleStrip = new SolidColorBrush(Color.Parse("#232325"));
    private static readonly IBrush CycleFill = new SolidColorBrush(Palette.Cycle, 0.85);
    private static readonly IPen CycleEdge = new Pen(new SolidColorBrush(Palette.Darken(Palette.Cycle, 0.35)), 1);
    private static readonly IBrush CycleShade = new SolidColorBrush(Palette.Cycle, 0.05);
    private static readonly IPen RulerTickPen = new Pen(new SolidColorBrush(Color.Parse("#6A6A70")), 1);
    private static readonly IPen RulerEdgePen = new Pen(new SolidColorBrush(Palette.Divider), 1);
    private static readonly IBrush RulerText = new SolidColorBrush(Color.Parse("#B4B4B9"));
    private static readonly IBrush PlayheadBrush = new SolidColorBrush(Palette.Playhead);
    private static readonly IBrush RecordBrush = new SolidColorBrush(Palette.Record);
    private static readonly StreamGeometry PlayheadHead = StreamGeometry.Parse("M -5,0 L 5,0 L 5,3 L 0,8 L -5,3 Z");
    private static readonly Typeface Face = new("Inter", FontStyle.Normal, FontWeight.Medium);

    public static double TickToX(long tick, Sequence sequence, double pixelsPerQuarter) =>
        tick * pixelsPerQuarter / sequence.Ppqn.TicksPerQuarterNote;

    public static long XToTick(double x, Sequence sequence, double pixelsPerQuarter) =>
        (long)Math.Max(0, x * sequence.Ppqn.TicksPerQuarterNote / pixelsPerQuarter);

    /// <summary>Draws vertical grid lines over <paramref name="area"/>. <paramref name="step"/> adds lines between beats when they are far enough apart.</summary>
    public static void DrawLines(DrawingContext context, Sequence sequence, double pixelsPerQuarter, double scroll, Rect area, GridDivision? step = null)
    {
        var meter = sequence.MeterMap;
        var firstTick = XToTick(Math.Max(0, scroll + area.Left - 2), sequence, pixelsPerQuarter);
        var lastTick = XToTick(scroll + area.Right + 2, sequence, pixelsPerQuarter);
        var bar = meter.BarStart(new Tick(firstTick));
        var guard = 0;
        while (bar.Value <= lastTick && guard++ < 20_000)
        {
            var signature = meter.SignatureAt(bar);
            var beat = signature.TryGetTicksPerBeat(sequence.Ppqn, out var span) ? span.Value : sequence.Ppqn.TicksPerQuarterNote;
            var next = meter.NextBarStart(bar);
            var beatPixels = TickToX(beat, sequence, pixelsPerQuarter);

            if (step is { } division && division != GridDivision.Bar)
            {
                var stepTicks = MusicalGrid.StepTicks(division, meter, bar);
                if (TickToX((long)stepTicks, sequence, pixelsPerQuarter) >= 7)
                {
                    for (var s = 1; bar.Value + (s * stepTicks) < next.Value; s++)
                    {
                        var t = bar.Value + (long)Math.Round(s * stepTicks);
                        if ((t - bar.Value) % beat != 0)
                        {
                            VLine(context, StepPen, TickToX(t, sequence, pixelsPerQuarter) - scroll, area);
                        }
                    }
                }
            }

            if (beatPixels >= 6)
            {
                for (var t = bar.Value + beat; t < next.Value; t += beat)
                {
                    VLine(context, BeatPen, TickToX(t, sequence, pixelsPerQuarter) - scroll, area);
                }
            }

            VLine(context, BarPen, TickToX(bar.Value, sequence, pixelsPerQuarter) - scroll, area);
            if (next <= bar)
            {
                break;
            }

            bar = next;
        }
    }

    /// <summary>
    /// Draws the bar ruler: a cycle strip on top (the loop, in yellow), bar numbers and beat ticks
    /// below, and the playhead marker.
    /// </summary>
    public static void DrawRuler(DrawingContext context, Sequence sequence, TickRange? loop, double pixelsPerQuarter, double scroll, Rect bounds, long playhead, bool recording)
    {
        context.FillRectangle(RulerBrush, bounds);
        context.FillRectangle(CycleStrip, new Rect(bounds.Left, bounds.Top, bounds.Width, CycleStripHeight));
        if (loop is { } cycle)
        {
            var x0 = TickToX(cycle.Start.Value, sequence, pixelsPerQuarter) - scroll;
            var x1 = TickToX(cycle.End.Value, sequence, pixelsPerQuarter) - scroll;
            var rect = new Rect(x0, bounds.Top + 1, Math.Max(2, x1 - x0), CycleStripHeight - 2);
            context.DrawRectangle(CycleFill, CycleEdge, rect, 2, 2);
        }

        var meter = sequence.MeterMap;
        var firstTick = XToTick(Math.Max(0, scroll - 40), sequence, pixelsPerQuarter);
        var lastTick = XToTick(scroll + bounds.Width + 2, sequence, pixelsPerQuarter);
        var bar = meter.BarStart(new Tick(firstTick));
        var firstBarLength = meter.NextBarStart(bar).Value - bar.Value;
        var barPixels = TickToX(firstBarLength > 0 ? firstBarLength : 4L * sequence.Ppqn.TicksPerQuarterNote, sequence, pixelsPerQuarter);
        var labelEvery = barPixels switch
        {
            >= 28 => 1,
            >= 14 => 2,
            >= 7 => 4,
            >= 3.5 => 8,
            _ => 16,
        };

        var baseline = bounds.Bottom;
        var guard = 0;
        while (bar.Value <= lastTick && guard++ < 20_000)
        {
            var number = meter.ToBarBeatTick(bar).Bar;
            var signature = meter.SignatureAt(bar);
            var beat = signature.TryGetTicksPerBeat(sequence.Ppqn, out var span) ? span.Value : sequence.Ppqn.TicksPerQuarterNote;
            var next = meter.NextBarStart(bar);
            var x = Snap(TickToX(bar.Value, sequence, pixelsPerQuarter) - scroll);
            var labelled = (number - 1) % labelEvery == 0;
            context.DrawLine(RulerTickPen, new Point(x, labelled ? bounds.Top + CycleStripHeight : baseline - 6), new Point(x, baseline));
            if (labelled)
            {
                var text = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, RulerText);
                context.DrawText(text, new Point(x + 3, bounds.Top + CycleStripHeight + 2));
            }

            if (TickToX(beat, sequence, pixelsPerQuarter) >= 8)
            {
                for (var t = bar.Value + beat; t < next.Value; t += beat)
                {
                    var bx = Snap(TickToX(t, sequence, pixelsPerQuarter) - scroll);
                    context.DrawLine(RulerTickPen, new Point(bx, baseline - 3), new Point(bx, baseline));
                }
            }

            if (next <= bar)
            {
                break;
            }

            bar = next;
        }

        context.DrawLine(RulerEdgePen, new Point(bounds.Left, baseline - 0.5), new Point(bounds.Right, baseline - 0.5));
        var px = Snap(TickToX(playhead, sequence, pixelsPerQuarter) - scroll);
        if (px >= bounds.Left - 6 && px <= bounds.Right + 6)
        {
            using (context.PushTransform(Matrix.CreateTranslation(px, baseline - 9)))
            {
                context.DrawGeometry(recording ? RecordBrush : PlayheadBrush, null, PlayheadHead);
            }
        }
    }

    /// <summary>Faintly shades the cycle range over a content area so the loop is visible below the ruler.</summary>
    public static void ShadeCycle(DrawingContext context, Sequence sequence, TickRange? loop, double pixelsPerQuarter, double scroll, Rect area)
    {
        if (loop is { } cycle)
        {
            var x0 = TickToX(cycle.Start.Value, sequence, pixelsPerQuarter) - scroll;
            var x1 = TickToX(cycle.End.Value, sequence, pixelsPerQuarter) - scroll;
            context.FillRectangle(CycleShade, new Rect(x0, area.Top, x1 - x0, area.Height));
        }
    }

    /// <summary>Aligns a vertical line to the pixel grid so it stays one pixel wide.</summary>
    public static double Snap(double x) => Math.Round(x) + 0.5;

    private static void VLine(DrawingContext context, IPen pen, double x, Rect area)
    {
        if (x >= area.Left - 1 && x <= area.Right + 1)
        {
            var sx = Snap(x);
            context.DrawLine(pen, new Point(sx, area.Top), new Point(sx, area.Bottom));
        }
    }
}
