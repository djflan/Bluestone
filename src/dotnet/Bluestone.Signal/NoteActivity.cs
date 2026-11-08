using System.Collections.Immutable;
using Bluestone.Domain.Sequencing;

namespace Bluestone.Signal;

/// <summary>
/// When notes are held at one point of a chain (a junction, ADR 0037) over the song: merged, sorted tick ranges, start
/// inclusive and end exclusive. A Device View lights a junction's note lamp while the playhead is inside one.
/// </summary>
public sealed class NoteActivity
{
    private readonly long[] _starts;
    private readonly long[] _ends;

    private NoteActivity(long[] starts, long[] ends)
    {
        _starts = starts;
        _ends = ends;
    }

    public static NoteActivity None { get; } = new([], []);

    /// <summary>The number of separate ranges.</summary>
    public int Count => _starts.Length;

    /// <summary>The held ranges of the notes among <paramref name="events"/>, merged where they touch or overlap.</summary>
    public static NoteActivity Of(ReadOnlySpan<SignalEvent> events)
    {
        var ranges = new List<(long Start, long End)>();
        foreach (var e in events)
        {
            if (e.Event is NoteEvent note)
            {
                ranges.Add((note.Position.Value, note.Position.Value + Math.Max(1, note.Duration.Value)));
            }
        }

        if (ranges.Count == 0)
        {
            return None;
        }

        ranges.Sort();
        var starts = new List<long>();
        var ends = new List<long>();
        foreach (var (start, end) in ranges)
        {
            if (ends.Count > 0 && start <= ends[^1])
            {
                ends[^1] = Math.Max(ends[^1], end);
            }
            else
            {
                starts.Add(start);
                ends.Add(end);
            }
        }

        return new NoteActivity([.. starts], [.. ends]);
    }

    /// <summary>Whether a note is held at <paramref name="tick"/>. Allocates nothing.</summary>
    public bool IsActiveAt(long tick)
    {
        // The last range starting at or before the tick.
        var index = Array.BinarySearch(_starts, tick);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return index >= 0 && tick < _ends[index];
    }

    /// <summary>The ranges, for tests and diagnostics.</summary>
    public ImmutableArray<(long Start, long End)> Ranges => [.. _starts.Zip(_ends)];
}
