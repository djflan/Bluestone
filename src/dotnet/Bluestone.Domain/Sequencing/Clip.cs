using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>
/// A span of a track's timeline that holds content. The clip's type decides what the content is; a
/// track can hold clips of any type (ADR 0020). Clips are immutable records.
/// </summary>
/// <remarks>
/// Content has its own positions, starting at the content origin. The clip shows content ticks
/// [<see cref="ContentOffset"/>, <see cref="ContentOffset"/> + <see cref="Length"/>) from
/// <see cref="Start"/> onward; content outside that window is kept but neither played nor drawn.
/// </remarks>
public abstract record Clip
{
    public const int MaxNameLength = 256;

    private readonly TickSpan _length;
    private readonly string _name = string.Empty;

    protected Clip(ClipId id, Tick start, TickSpan length, TickSpan contentOffset, string name)
    {
        Id = id;
        Start = start;
        Length = length;
        ContentOffset = contentOffset;
        Name = name;
    }

    public ClipId Id { get; init; }

    public Tick Start { get; init; }

    /// <summary>The clip's length on the timeline, at least one tick.</summary>
    public TickSpan Length
    {
        get => _length;
        init => _length = value.Value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Length), value, "A clip must last at least one tick.");
    }

    /// <summary>Content ticks trimmed off the clip's left edge.</summary>
    public TickSpan ContentOffset { get; init; }

    /// <summary>The clip's own name, or empty to show the track's name.</summary>
    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength
                ? value
                : throw new ArgumentException($"Clip names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    /// <summary>The first tick after the clip.</summary>
    public Tick End => Start + Length;

    /// <summary>
    /// Where content tick 0 falls on the timeline. Negative when trimmed content would start before the
    /// sequence does.
    /// </summary>
    public long Origin => Start.Value - ContentOffset.Value;

    public bool Contains(Tick position) => position >= Start && position < End;

    /// <summary>Whether the clip shares any tick with [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public bool Overlaps(Tick from, Tick to) => Start < to && from < End;

    /// <summary>
    /// Moves the clip's edges to <paramref name="start"/> and <paramref name="until"/> while its content
    /// stays where it is on the timeline, revealing or hiding content.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="until"/> is not after <paramref name="start"/>.</exception>
    public abstract Clip WithBounds(Tick start, Tick until);

    /// <summary>
    /// Splits the clip at <paramref name="at"/>. Content before that tick goes to the left clip and the rest
    /// to the right one, which gets <paramref name="rightId"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="at"/> is not strictly inside the clip.</exception>
    public abstract (Clip Left, Clip Right) SplitAt(Tick at, ClipId rightId);

    /// <summary>A copy at <paramref name="start"/> with a new <see cref="ClipId"/> and new IDs for its content.</summary>
    public abstract Clip CopyTo(Tick start);

    protected void EnsureInside(Tick at)
    {
        if (at <= Start || at >= End)
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "A clip can only be split strictly inside it.");
        }
    }

    protected static void EnsureBounds(Tick start, Tick until)
    {
        if (until <= start)
        {
            throw new ArgumentException("A clip must end after it starts.", nameof(until));
        }
    }
}

/// <summary>A clip of channel and system events: notes, controllers, programs, SysEx, and the rest.</summary>
public sealed record NoteClip : Clip
{
    private readonly EventList _content = EventList.Empty;

    /// <summary>A clip whose <paramref name="content"/> is at content positions (see <see cref="Clip"/>).</summary>
    public NoteClip(ClipId id, Tick start, TickSpan length, TickSpan contentOffset, EventList content, string name = "")
        : base(id, start, length, contentOffset, name) => Content = content;

    /// <summary>Events at content positions, in canonical order.</summary>
    public EventList Content
    {
        get => _content;
        init => _content = value ?? throw new ArgumentNullException(nameof(Content));
    }

    public static NoteClip Create(Tick start, TickSpan length) => new(ClipId.New(), start, length, TickSpan.Zero, EventList.Empty);

    /// <summary>
    /// A clip holding <paramref name="events"/> (at timeline positions) from the bar of the first event to
    /// the bar line after the latest end, or null if there are none. The clip ends strictly after every
    /// event's position, so every event is visible and no note is cut short.
    /// </summary>
    public static NoteClip? Enclosing(IReadOnlyCollection<TrackEvent> events, MeterMap meter, ClipId? id = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return null;
        }

        var (start, end) = EnclosingBounds(
            events.Min(e => e.Position.Value), events.Max(e => e.Position.Value), events.Max(e => e.EndPosition.Value), meter);
        return new NoteClip(id ?? ClipId.New(), start, end - start, TickSpan.Zero, new EventList(events.Select(e => Shifted(e, -start.Value))));
    }

    /// <summary>
    /// The bounds of <see cref="Enclosing"/> for events whose positions run from <paramref name="firstPosition"/>
    /// to <paramref name="lastPosition"/> and whose latest end is <paramref name="latestEnd"/>.
    /// </summary>
    public static (Tick Start, Tick End) EnclosingBounds(long firstPosition, long lastPosition, long latestEnd, MeterMap meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        var start = meter.BarStart(new Tick(firstPosition));
        var reach = new Tick(Math.Max(latestEnd, lastPosition + 1));
        var bar = meter.BarStart(reach);
        return (start, bar == reach ? reach : meter.NextBarStart(reach));
    }

    /// <summary>
    /// The events that play, at timeline positions: those starting inside the clip, with notes cut at
    /// its end.
    /// </summary>
    public IEnumerable<TrackEvent> Arrange()
    {
        var from = ContentOffset.Value;
        var to = from + Length.Value;
        var shift = Origin;
        foreach (var e in Content.Items)
        {
            var position = e.Position.Value;
            if (position < from)
            {
                continue;
            }

            if (position >= to)
            {
                yield break;
            }

            yield return e is NoteEvent note && note.EndPosition.Value > to
                ? note with { Position = new Tick(position + shift), Duration = new TickSpan(to - position) }
                : Shifted(e, shift);
        }
    }

    /// <summary>
    /// All content that falls on or after the sequence start, at timeline positions and uncut, including
    /// content the clip hides. This is what an editor shows.
    /// </summary>
    public IEnumerable<TrackEvent> TimelineContent()
    {
        var shift = Origin;
        foreach (var e in Content.Items)
        {
            if (e.Position.Value + shift >= 0)
            {
                yield return Shifted(e, shift);
            }
        }
    }

    /// <summary>Whether <paramref name="e"/>, at its content position, is inside the clip's window.</summary>
    public bool Shows(TrackEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.Position >= new Tick(ContentOffset.Value) && e.Position.Value < ContentOffset.Value + Length.Value;
    }

    /// <summary>
    /// Removes <paramref name="remove"/> and adds <paramref name="add"/> (at timeline positions) in one step.
    /// An event in both is replaced. The clip's bounds do not change; content added before the content
    /// origin moves the origin back.
    /// </summary>
    public NoteClip EditTimeline(IEnumerable<EventId> remove, IReadOnlyCollection<TrackEvent> add)
    {
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(add);
        var removed = remove.ToHashSet();
        removed.UnionWith(add.Select(e => e.Id));
        var earliest = add.Count == 0 ? 0 : add.Min(e => e.Position.Value) - Origin;
        var rebase = Math.Max(0, -earliest);
        var kept = Content.Items.Where(e => !removed.Contains(e.Id)).Select(e => Shifted(e, rebase));
        var added = add.Select(e => Shifted(e, rebase - Origin));
        return this with
        {
            Content = new EventList(kept.Concat(added)),
            ContentOffset = new TickSpan(ContentOffset.Value + rebase),
        };
    }

    public override NoteClip WithBounds(Tick start, Tick until)
    {
        EnsureBounds(start, until);
        var offset = start.Value - Origin;
        return offset >= 0
            ? this with { Start = start, Length = until - start, ContentOffset = new TickSpan(offset) }
            : this with
            {
                Start = start,
                Length = until - start,
                ContentOffset = TickSpan.Zero,
                Content = new EventList(Content.Items.Select(e => Shifted(e, -offset))),
            };
    }

    public override (Clip Left, Clip Right) SplitAt(Tick at, ClipId rightId)
    {
        EnsureInside(at);
        var cut = at.Value - Origin;
        var left = this with { Length = at - Start, Content = new EventList(Content.Items.Where(e => e.Position.Value < cut)) };
        var right = this with
        {
            Id = rightId,
            Start = at,
            Length = End - at,
            ContentOffset = new TickSpan(cut),
            Content = new EventList(Content.Items.Where(e => e.Position.Value >= cut)),
        };
        return (left, right);
    }

    public override NoteClip CopyTo(Tick start) => this with
    {
        Id = ClipId.New(),
        Start = start,
        Content = new EventList(Content.Items.Select(e => e with { Id = EventId.New() })),
    };

    private static TrackEvent Shifted(TrackEvent e, long ticks) => ticks == 0 ? e : e with { Position = new Tick(e.Position.Value + ticks) };
}
