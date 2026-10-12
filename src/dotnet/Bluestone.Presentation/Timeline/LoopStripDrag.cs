using Bluestone.Domain.Projects;
using Bluestone.Domain.Time;

namespace Bluestone.Presentation.Timeline;

/// <summary>Which part of the loop strip a pointer is over.</summary>
public enum LoopStripPart
{
    /// <summary>Strip space outside the loop: dragging there draws a new loop.</summary>
    Empty,

    /// <summary>The loop's body: dragging moves it with its length kept.</summary>
    Body,

    /// <summary>The loop's start edge: dragging changes where it starts.</summary>
    StartEdge,

    /// <summary>The loop's end edge: dragging changes where it ends.</summary>
    EndEdge,
}

/// <summary>
/// One drag in the loop strip above a timeline: what it grabbed and the range it makes as the pointer moves. Pure, so
/// it can be tested without a window; the control converts pixels to ticks and commits the result once, on release.
/// </summary>
/// <param name="Part">What the drag grabbed.</param>
/// <param name="PressTick">The (unsnapped) tick under the pointer when it was pressed.</param>
/// <param name="Original">The loop's range when the drag started; null when there was none.</param>
public sealed record LoopStripDrag(LoopStripPart Part, long PressTick, TickRange? Original)
{
    /// <summary>
    /// Which part of the strip is at <paramref name="x"/> (in the same pixels as the loop's edges), with edges
    /// <paramref name="edgeGrip"/> pixels wide on each side. A narrow loop keeps a middle third for its body.
    /// </summary>
    public static LoopStripPart HitTest(double x, double? startX, double? endX, double edgeGrip)
    {
        if (startX is not { } x0 || endX is not { } x1)
        {
            return LoopStripPart.Empty;
        }

        var grip = Math.Min(edgeGrip, Math.Max(2, (x1 - x0) / 3));
        if (x >= x0 - grip && x <= x0 + grip)
        {
            return LoopStripPart.StartEdge;
        }

        if (x >= x1 - grip && x <= x1 + grip)
        {
            return LoopStripPart.EndEdge;
        }

        return x > x0 && x < x1 ? LoopStripPart.Body : LoopStripPart.Empty;
    }

    /// <summary>
    /// The range for the pointer at <paramref name="tick"/>, positions passed through <paramref name="snap"/>; null
    /// while a new loop has no length yet. Edges stop one tick before crossing each other; nothing goes before tick 0.
    /// </summary>
    public TickRange? RangeAt(long tick, Func<long, long> snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        tick = Math.Max(0, tick);
        switch (Part)
        {
            case LoopStripPart.Empty:
                {
                    var anchor = Math.Max(0, snap(PressTick));
                    var end = Math.Max(0, snap(tick));
                    return end == anchor ? null : new TickRange(new Tick(Math.Min(anchor, end)), new Tick(Math.Max(anchor, end)));
                }

            case LoopStripPart.Body when Original is { } range:
                {
                    var start = Math.Max(0, snap(range.Start.Value + (tick - PressTick)));
                    return new TickRange(new Tick(start), new Tick(start + range.Length.Value));
                }

            case LoopStripPart.StartEdge when Original is { } range:
                {
                    var start = Math.Clamp(snap(tick), 0, range.End.Value - 1);
                    return new TickRange(new Tick(start), range.End);
                }

            case LoopStripPart.EndEdge when Original is { } range:
                {
                    var end = Math.Max(snap(tick), range.Start.Value + 1);
                    return new TickRange(range.Start, new Tick(end));
                }

            default:
                return Original;
        }
    }

    /// <summary>The undo label for committing this drag.</summary>
    public string Label => Part switch
    {
        LoopStripPart.Body => "Move Loop",
        LoopStripPart.StartEdge or LoopStripPart.EndEdge => "Resize Loop",
        _ => "Set Loop",
    };

    /// <summary>Whether committing turns looping on: drawing a new loop does; moving or resizing keeps the loop's state.</summary>
    public bool Enables => Part == LoopStripPart.Empty;
}
