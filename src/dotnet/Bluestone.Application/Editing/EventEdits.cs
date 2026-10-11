using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;

namespace Bluestone.Application.Editing;

/// <summary>Which end of a note a resize moves.</summary>
public enum NoteEdge
{
    Start,
    End,
}

/// <param name="Strength">How far each event moves toward its grid line, 0-100 percent.</param>
/// <param name="Swing">50 is straight; up to 75 delays every second grid line by up to half a step.</param>
/// <param name="QuantizeEnds">Also snap note ends (lengths) to the grid.</param>
public sealed record QuantizeOptions(int Strength = 100, int Swing = 50, bool QuantizeEnds = false)
{
    public static readonly QuantizeOptions Default = new();
}

/// <summary>
/// Pure transformations of track events for the editors. Each returns the changed events with their
/// original IDs (so they replace the originals in one undoable command), except
/// <see cref="Copy"/>, which returns new events. Moves are clamped as a group, so a selection keeps
/// its shape at the edges of the timeline or the note range.
/// </summary>
public static class EventEdits
{
    /// <summary>Moves events in time and notes in pitch. Only notes change pitch.</summary>
    public static ImmutableArray<TrackEvent> Move(IReadOnlyCollection<TrackEvent> events, long deltaTicks, int deltaSemitones)
    {
        ArgumentNullException.ThrowIfNull(events);
        var dt = ClampTimeDelta(events, deltaTicks);
        var dp = ClampPitchDelta(events, deltaSemitones);
        if (dt == 0 && dp == 0)
        {
            return [];
        }

        return [.. events.Select(e => e switch
        {
            NoteEvent note => note with { Position = new Tick(note.Position.Value + dt), Note = new NoteNumber(note.Note.Value + dp) },
            _ => e with { Position = new Tick(e.Position.Value + dt) },
        })];
    }

    /// <summary>The largest part of <paramref name="delta"/> that keeps every event at or after tick 0.</summary>
    public static long ClampTimeDelta(IReadOnlyCollection<TrackEvent> events, long delta)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Count == 0 ? 0 : Math.Max(delta, -events.Min(e => e.Position.Value));
    }

    /// <summary>The largest part of <paramref name="delta"/> that keeps every note within 0-127.</summary>
    public static int ClampPitchDelta(IReadOnlyCollection<TrackEvent> events, int delta)
    {
        ArgumentNullException.ThrowIfNull(events);
        var notes = events.OfType<NoteEvent>().ToList();
        if (notes.Count == 0)
        {
            return 0;
        }

        return Math.Clamp(delta, -notes.Min(n => n.Note.Value), 127 - notes.Max(n => n.Note.Value));
    }

    /// <summary>
    /// Moves one edge of each note by <paramref name="deltaTicks"/>. Notes never become shorter than
    /// <paramref name="minimumLength"/> ticks, and starts never move before tick 0.
    /// </summary>
    public static ImmutableArray<TrackEvent> Resize(IEnumerable<NoteEvent> notes, NoteEdge edge, long deltaTicks, long minimumLength = 1)
    {
        ArgumentNullException.ThrowIfNull(notes);
        minimumLength = Math.Max(1, minimumLength);
        var result = ImmutableArray.CreateBuilder<TrackEvent>();
        foreach (var note in notes)
        {
            NoteEvent resized;
            if (edge == NoteEdge.End)
            {
                var length = Math.Max(Math.Min(minimumLength, note.Duration.Value), note.Duration.Value + deltaTicks);
                resized = note with { Duration = new TickSpan(length) };
            }
            else
            {
                var end = note.EndPosition.Value;
                var start = Math.Clamp(note.Position.Value + deltaTicks, 0, Math.Max(note.Position.Value, end - minimumLength));
                resized = note with { Position = new Tick(start), Duration = new TickSpan(end - start) };
            }

            if (resized != note)
            {
                result.Add(resized);
            }
        }

        return result.ToImmutable();
    }

    /// <summary>Sets every note's length to <paramref name="ticks"/> (at least one).</summary>
    public static ImmutableArray<TrackEvent> SetLength(IEnumerable<NoteEvent> notes, long ticks)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var length = new TickSpan(Math.Max(1, ticks));
        return [.. notes.Where(n => n.Duration != length).Select(n => n with { Duration = length })];
    }

    /// <summary>Moves event starts (and optionally note ends) toward the grid.</summary>
    public static ImmutableArray<TrackEvent> Quantize(IEnumerable<TrackEvent> events, MeterMap meter, GridDivision grid, QuantizeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(meter);
        options ??= QuantizeOptions.Default;
        var strength = Math.Clamp(options.Strength, 0, 100) / 100.0;
        var swing = (Math.Clamp(options.Swing, 50, 75) - 50) / 50.0;

        long Target(long tick) => tick + (long)Math.Round((MusicalGrid.NearestLine(tick, grid, meter, swing) - tick) * strength);

        var result = ImmutableArray.CreateBuilder<TrackEvent>();
        foreach (var e in events)
        {
            var start = Math.Max(0, Target(e.Position.Value));
            TrackEvent changed = e switch
            {
                NoteEvent note when options.QuantizeEnds => QuantizedNote(note, start, Math.Max(start + 1, Target(note.EndPosition.Value))),
                NoteEvent note => note with { Position = new Tick(start) },
                _ => e with { Position = new Tick(start) },
            };

            if (changed != e)
            {
                result.Add(changed);
            }
        }

        return result.ToImmutable();

        static NoteEvent QuantizedNote(NoteEvent note, long start, long end) =>
            note with { Position = new Tick(start), Duration = new TickSpan(end - start) };
    }

    /// <summary>Sets every note's velocity to <paramref name="velocity"/>, clamped to 1-127.</summary>
    public static ImmutableArray<TrackEvent> SetVelocity(IEnumerable<NoteEvent> notes, int velocity)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var value = new Velocity(Math.Clamp(velocity, 1, 127));
        return [.. notes.Where(n => n.Velocity != value).Select(n => n with { Velocity = value })];
    }

    /// <summary>Adds <paramref name="delta"/> to every note's velocity, clamped to 1-127.</summary>
    public static ImmutableArray<TrackEvent> AddVelocity(IEnumerable<NoteEvent> notes, int delta) =>
        MapVelocity(notes, v => v + delta);

    /// <summary>Scales every note's velocity by <paramref name="percent"/>, clamped to 1-127.</summary>
    public static ImmutableArray<TrackEvent> ScaleVelocity(IEnumerable<NoteEvent> notes, int percent) =>
        MapVelocity(notes, v => (int)Math.Round(v * percent / 100.0));

    /// <summary>
    /// Sets velocities along a line from (<paramref name="fromTick"/>, <paramref name="fromVelocity"/>)
    /// to (<paramref name="toTick"/>, <paramref name="toVelocity"/>), for crescendos drawn in the velocity lane.
    /// </summary>
    public static ImmutableArray<TrackEvent> VelocityRamp(IEnumerable<NoteEvent> notes, long fromTick, int fromVelocity, long toTick, int toVelocity)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var span = toTick - fromTick;
        return MapVelocity(notes.Where(n => n.Position.Value >= Math.Min(fromTick, toTick) && n.Position.Value <= Math.Max(fromTick, toTick)), (n, _) =>
            span == 0 ? toVelocity : (int)Math.Round(fromVelocity + ((toVelocity - fromVelocity) * (n.Position.Value - fromTick) / (double)span)));
    }

    /// <summary>Extends each note to the start of the next later note (on any pitch), leaving the last note unchanged.</summary>
    public static ImmutableArray<TrackEvent> Legato(IEnumerable<NoteEvent> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var ordered = notes.OrderBy(n => n.Position).ToList();
        var starts = ordered.Select(n => n.Position.Value).Distinct().ToArray();
        var result = ImmutableArray.CreateBuilder<TrackEvent>();
        foreach (var note in ordered)
        {
            var next = Array.BinarySearch(starts, note.Position.Value) + 1;
            if (next < starts.Length)
            {
                var length = new TickSpan(starts[next] - note.Position.Value);
                if (length != note.Duration)
                {
                    result.Add(note with { Duration = length });
                }
            }
        }

        return result.ToImmutable();
    }

    /// <summary>Re-addresses notes and other channel events to <paramref name="channel"/>.</summary>
    public static ImmutableArray<TrackEvent> SetChannel(IEnumerable<TrackEvent> events, MidiChannel channel)
    {
        ArgumentNullException.ThrowIfNull(events);
        var result = ImmutableArray.CreateBuilder<TrackEvent>();
        foreach (var e in events)
        {
            switch (e)
            {
                case ChannelEvent addressed when addressed.Channel != channel:
                    result.Add(addressed with { Channel = channel });
                    break;
            }
        }

        return result.ToImmutable();
    }

    /// <summary>Copies events with fresh IDs, shifted by <paramref name="deltaTicks"/> (clamped at tick 0 as a group).</summary>
    public static ImmutableArray<TrackEvent> Copy(IReadOnlyCollection<TrackEvent> events, long deltaTicks)
    {
        ArgumentNullException.ThrowIfNull(events);
        var dt = ClampTimeDelta(events, deltaTicks);
        return [.. events.Select(e => e with { Id = EventId.New(), Position = new Tick(e.Position.Value + dt) })];
    }

    /// <summary>The span from the first event's start to the last event's end, or zero when empty.</summary>
    public static TickSpan Extent(IReadOnlyCollection<TrackEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Count == 0 ? TickSpan.Zero : new TickSpan(events.Max(e => e.EndPosition.Value) - events.Min(e => e.Position.Value));
    }

    private static ImmutableArray<TrackEvent> MapVelocity(IEnumerable<NoteEvent> notes, Func<int, int> map) =>
        MapVelocity(notes, (_, v) => map(v));

    private static ImmutableArray<TrackEvent> MapVelocity(IEnumerable<NoteEvent> notes, Func<NoteEvent, int, int> map)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var result = ImmutableArray.CreateBuilder<TrackEvent>();
        foreach (var note in notes)
        {
            var value = new Velocity(Math.Clamp(map(note, note.Velocity.Value), 1, 127));
            if (value != note.Velocity)
            {
                result.Add(note with { Velocity = value });
            }
        }

        return result.ToImmutable();
    }
}
