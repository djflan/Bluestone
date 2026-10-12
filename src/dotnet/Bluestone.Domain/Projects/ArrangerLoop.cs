using Bluestone.Domain.Time;

namespace Bluestone.Domain.Projects;

/// <summary>
/// The arranger loop: a range of the timeline and whether playback loops over it. The two are separate, so turning
/// the loop off keeps its range for the next time it is turned on, and a range can be adjusted while the loop is off.
/// </summary>
/// <remarks>
/// Only <see cref="Active"/> affects playback and cycle recording. A loop engages while the playhead is before its end
/// (playback that starts after the loop's end plays on rather than jumping back).
/// </remarks>
public sealed record ArrangerLoop
{
    /// <summary>No range has been defined, and looping is off.</summary>
    public static ArrangerLoop None { get; } = new(null, false);

    /// <exception cref="ArgumentException">Looping is enabled without a range.</exception>
    public ArrangerLoop(TickRange? range, bool isEnabled)
    {
        if (isEnabled && range is null)
        {
            throw new ArgumentException("An enabled loop needs a range.", nameof(isEnabled));
        }

        Range = range;
        IsEnabled = isEnabled;
    }

    /// <summary>The loop's range, kept while looping is off; null when none has been defined.</summary>
    public TickRange? Range { get; }

    /// <summary>Whether playback wraps from the range's end to its start.</summary>
    public bool IsEnabled { get; }

    /// <summary>The range playback loops over, or null when looping is off.</summary>
    public TickRange? Active => IsEnabled ? Range : null;

    public static ArrangerLoop Enabled(TickRange range) => new(range ?? throw new ArgumentNullException(nameof(range)), true);

    /// <summary>The same range with looping on or off. Turning it on needs a range.</summary>
    /// <exception cref="InvalidOperationException">Turning on a loop that has no range.</exception>
    public ArrangerLoop WithEnabled(bool enabled) => enabled == IsEnabled
        ? this
        : enabled && Range is null
            ? throw new InvalidOperationException("A loop without a range cannot be turned on.")
            : new ArrangerLoop(Range, enabled);

    /// <summary>A new range, keeping whether looping is on.</summary>
    public ArrangerLoop WithRange(TickRange range) => new(range ?? throw new ArgumentNullException(nameof(range)), IsEnabled);

    /// <summary>
    /// The range moved by <paramref name="ticks"/> with its length kept, stopping at tick 0; unchanged when there is no range.
    /// </summary>
    public ArrangerLoop Moved(long ticks)
    {
        if (Range is not { } range)
        {
            return this;
        }

        var delta = Math.Max(ticks, -range.Start.Value);
        return delta == 0 ? this : WithRange(new TickRange(new Tick(range.Start.Value + delta), new Tick(range.End.Value + delta)));
    }
}
