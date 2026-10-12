using Bluestone.Domain.Projects;
using Bluestone.Domain.Time;

namespace Bluestone.Application.Editing;

/// <summary>
/// Undoable edits of the arranger loop (see <see cref="ArrangerLoop"/>). None of them deletes the loop's range:
/// turning looping off keeps it for the next time looping is turned on.
/// </summary>
public static class LoopCommands
{
    /// <summary>Bars a loop covers when it is turned on for the first time.</summary>
    public const int DefaultBars = 4;

    /// <summary>Sets the loop's range, keeping whether looping is on unless <paramref name="enable"/> turns it on.</summary>
    public static IProjectCommand SetRange(TickRange range, bool enable = false, string label = "Set Loop")
    {
        ArgumentNullException.ThrowIfNull(range);
        return new ProjectCommand(label, p =>
        {
            var loop = p.Loop.WithRange(range);
            loop = enable ? loop.WithEnabled(true) : loop;
            return loop == p.Loop ? p : p with { Loop = loop };
        });
    }

    /// <summary>
    /// Turns looping on or off. Turning it on without a range loops <see cref="DefaultBars"/> bars from the bar
    /// containing <paramref name="playhead"/>.
    /// </summary>
    public static IProjectCommand SetEnabled(bool enabled, Tick playhead) =>
        new ProjectCommand(enabled ? "Loop On" : "Loop Off", p =>
        {
            if (p.Loop.IsEnabled == enabled)
            {
                return p;
            }

            var loop = enabled && p.Loop.Range is null ? ArrangerLoop.Enabled(DefaultRange(p, playhead)) : p.Loop.WithEnabled(enabled);
            return p with { Loop = loop };
        });

    /// <summary>Turns looping on (see <see cref="SetEnabled"/>) or off.</summary>
    public static IProjectCommand Toggle(Project project, Tick playhead)
    {
        ArgumentNullException.ThrowIfNull(project);
        return SetEnabled(!project.Loop.IsEnabled, playhead);
    }

    /// <summary>Loops <paramref name="range"/>: sets the loop to it and turns looping on.</summary>
    public static IProjectCommand LoopRegion(TickRange range) => SetRange(range, enable: true, "Loop Selected Region");

    /// <summary>
    /// Moves the loop by <paramref name="ticks"/> with its length kept, stopping at tick 0. Does nothing without a range.
    /// </summary>
    public static IProjectCommand Nudge(long ticks) =>
        new ProjectCommand("Nudge Loop", p =>
        {
            var moved = p.Loop.Moved(ticks);
            return ReferenceEquals(moved, p.Loop) ? p : p with { Loop = moved };
        });

    /// <summary>
    /// Sets the loop's start and length exactly. Refused (no change) for a length below one tick or a negative start.
    /// </summary>
    public static IProjectCommand SetStartAndLength(Tick start, TickSpan length) =>
        start.Value < 0 || length.Value < 1
            ? new ProjectCommand("Set Loop", p => p)
            : SetRange(new TickRange(start, start + length));

    /// <summary>The loop a first <see cref="SetEnabled"/> creates: <see cref="DefaultBars"/> bars from the playhead's bar.</summary>
    public static TickRange DefaultRange(Project project, Tick playhead)
    {
        ArgumentNullException.ThrowIfNull(project);
        var meter = project.Sequence.MeterMap;
        var start = meter.BarStart(new Tick(Math.Max(0, playhead.Value)));
        var bar = meter.ToBarBeatTick(start).Bar;
        var end = meter.TryGetTick(new BarBeatTick(bar + DefaultBars, 1, 0), out var e)
            ? e
            : start + new TickSpan(DefaultBars * 4L * project.Sequence.Ppqn.TicksPerQuarterNote);
        return new TickRange(start, end);
    }
}
