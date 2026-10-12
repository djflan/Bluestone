using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>
/// A span of a track's timeline that holds content. The clip's type decides what the content is; a
/// track can hold clips of any type (ADR 0020). Clips are immutable records.
/// </summary>
/// <remarks>
/// Content has its own positions, starting at the content origin. Without a loop, the clip shows content ticks
/// [<see cref="ContentOffset"/>, <see cref="ContentOffset"/> + <see cref="Length"/>) from <see cref="Start"/> onward;
/// content outside that window is kept but neither played nor drawn. With a <see cref="Loop"/> on, content from
/// <see cref="ContentOffset"/> plays up to the loop's end, and the loop's region then repeats until the clip ends (see
/// <see cref="ContentAt"/> and <see cref="Passes"/>).
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

    /// <summary>The clip's loop, on or off; null when it has never had one.</summary>
    public ClipLoop? Loop { get; init; }

    /// <summary>Whether the clip's loop is on.</summary>
    public bool IsLooping => Loop is { IsEnabled: true };

    /// <summary>The first tick after the clip.</summary>
    public Tick End => Start + Length;

    /// <summary>The clip's passes over its content, in timeline order (one when it does not loop).</summary>
    public ClipPasses Passes => new(this);

    /// <summary>
    /// The timeline tick where repetition <paramref name="index"/> (1 or later) starts, computed directly so a view can
    /// draw only the repeats it shows. Meaningful only while the clip loops.
    /// </summary>
    public long RepeatStart(long index) => Start.Value + FirstPassLength + ((index - 1) * Loop!.Length.Value);

    /// <summary>
    /// The content tick heard <paramref name="clipTicks"/> after the clip's start: <see cref="ContentOffset"/> onward,
    /// and with the loop on, wrapped into the loop's region once past its end. <see cref="ContentOffset"/> itself is
    /// never wrapped, so trimming keeps what the clip hides.
    /// </summary>
    public long ContentAt(long clipTicks)
    {
        var content = ContentOffset.Value + clipTicks;
        if (Loop is not { IsEnabled: true } loop || content < loop.End.Value)
        {
            return content;
        }

        return loop.Start.Value + ((content - loop.End.Value) % loop.Length.Value);
    }

    /// <summary>The length of the first pass: up to the loop's end (when looping), at most the clip's length.</summary>
    public long FirstPassLength => Math.Min(Length.Value, IsLooping ? Loop!.End.Value - ContentAt(0) : Length.Value);

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
    /// <summary>
    /// The most events a looping clip may play. Repeats are produced when a project is prepared for playback, not on
    /// a real-time thread, but a very short loop over a long clip could still make millions; an edit that would go past
    /// this is refused (<see cref="ClipLoopLimitException"/>), so nothing ever silently stops playing.
    /// </summary>
    public const int MaxArrangedEvents = 1 << 18;

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
    /// The events that play, at timeline positions: those starting inside the clip, with notes cut at its end. A
    /// looping clip plays each pass's content, notes cut at the end of their pass (a note starting inside the loop
    /// sounds again on every pass; one crossing the loop's end stops there), and restores the controller state at each
    /// repetition (<see cref="ClipLoopChase"/>). Repeats keep the IDs of the content events they come from.
    /// </summary>
    public IEnumerable<TrackEvent> Arrange()
    {
        if (!IsLooping)
        {
            return ArrangePass(new ClipPass(Start.Value, ContentOffset.Value, Length.Value, 0));
        }

        // Repeats of a loop that holds nothing add nothing: only the first pass can play anything (and a one-tick empty
        // loop over a very long clip must not be walked pass by pass).
        if (CountStarting(Loop!.Start.Value, Loop.End.Value) == 0)
        {
            return ArrangePass(new ClipPass(Start.Value, ContentAt(0), FirstPassLength, 0));
        }

        var chase = ClipLoopChase.AtRepeat(Id, Content, Loop!);
        var events = new List<TrackEvent>();
        foreach (var pass in Passes)
        {
            if (pass.Index > 0)
            {
                foreach (var e in chase)
                {
                    events.Add(Shifted(e, pass.TimelineStart));
                }
            }

            events.AddRange(ArrangePass(pass));
        }

        // Chase events go before the content at a pass's first tick, in phase order.
        return events.Order(Comparer<TrackEvent>.Create(EventOrder.Compare));
    }

    /// <summary>How many events <see cref="Arrange"/> yields, counted without producing them; <see cref="long.MaxValue"/> when that overflows.</summary>
    public long ArrangedEventCount()
    {
        try
        {
            return CountArranged();
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private long CountArranged()
    {
        checked
        {
            if (!IsLooping)
            {
                return CountStarting(ContentOffset.Value, ContentOffset.Value + Length.Value);
            }

            var loop = Loop!;
            var passes = Passes.Count;
            var first = FirstPassLength;
            var start = ContentAt(0);
            var count = CountStarting(start, start + first);
            if (passes > 1 && CountStarting(loop.Start.Value, loop.End.Value) > 0)
            {
                var perLoop = CountStarting(loop.Start.Value, loop.End.Value) + ClipLoopChase.AtRepeat(Id, Content, loop).Length;
                var lastLength = Length.Value - first - ((passes - 2) * loop.Length.Value);
                count += ((passes - 2) * perLoop) + CountStarting(loop.Start.Value, loop.Start.Value + lastLength) + ClipLoopChase.AtRepeat(Id, Content, loop).Length;
            }

            return count;
        }
    }

    /// <summary>
    /// The clip with what it plays as ordinary content: repeats written out, chase values included, notes cut where
    /// they stop, and no loop. Playback is the same; the copy can be edited pass by pass.
    /// </summary>
    public NoteClip Consolidated()
    {
        var shift = -Start.Value;
        var played = Arrange().Select(e => Shifted(e, shift) with { Id = EventId.New() });
        return this with { ContentOffset = TickSpan.Zero, Content = new EventList(played), Loop = null };
    }

    private IEnumerable<TrackEvent> ArrangePass(ClipPass pass)
    {
        var from = pass.ContentStart;
        var to = pass.ContentEnd;
        var shift = pass.Shift;
        var items = Content.Items;
        for (var i = FirstAtOrAfter(from); i < items.Length; i++)
        {
            var e = items[i];
            var position = e.Position.Value;
            if (position >= to)
            {
                yield break;
            }

            yield return e is NoteEvent note && note.EndPosition.Value > to
                ? note with { Position = new Tick(position + shift), Duration = new TickSpan(to - position) }
                : Shifted(e, shift);
        }
    }

    // Events starting in content ticks [from, to).
    private long CountStarting(long from, long to) => to <= from ? 0 : FirstAtOrAfter(to) - FirstAtOrAfter(from);

    private int FirstAtOrAfter(long position)
    {
        var items = Content.Items;
        int low = 0, high = items.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (items[mid].Position.Value < position)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
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

    /// <summary>Whether <paramref name="e"/>, at its content position, plays on some pass of the clip.</summary>
    public bool Shows(TrackEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var position = e.Position.Value;
        var start = ContentAt(0);
        if (position >= start && position < start + FirstPassLength)
        {
            return true;
        }

        if (!IsLooping || Passes.Count < 2)
        {
            return false;
        }

        var reach = Math.Min(Loop!.Length.Value, Length.Value - FirstPassLength);
        return position >= Loop.Start.Value && position < Loop.Start.Value + reach;
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
            Loop = Loop?.Shifted(rebase),
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
                Loop = Loop?.Shifted(-offset),
            };
    }

    /// <remarks>
    /// A clip with a loop (on or off) keeps all its content in both parts, so each can still repeat its loop; the right
    /// part's content offset is the left part's moved by the cut (never wrapped, see <see cref="Clip.ContentAt"/>), and
    /// its events get new IDs. A clip without a loop is cut in two: events before <paramref name="at"/> stay left.
    /// </remarks>
    public override (Clip Left, Clip Right) SplitAt(Tick at, ClipId rightId)
    {
        EnsureInside(at);
        if (Loop is not null)
        {
            var copy = CopyTo(at);
            return (this with { Length = at - Start }, copy with { Id = rightId, Length = End - at, ContentOffset = new TickSpan(ContentOffset.Value + (at.Value - Start.Value)) });
        }

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
