using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Time;

namespace Bluestone.Domain.Sequencing;

/// <summary>
/// A clip's loop: a region of its content that repeats for as long as the clip lasts, and whether it is on. Turning it
/// off keeps the region, so turning it on again restores it. Content before the region (from the clip's
/// <see cref="Clip.ContentOffset"/>) plays once first, as a pickup.
/// </summary>
/// <remarks>
/// Positions are content ticks, like <see cref="Clip.ContentOffset"/>. The region may reach past the content: that part
/// repeats as silence. Looping never copies content; repeats are produced when the clip is played or drawn.
/// </remarks>
public sealed record ClipLoop
{
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is under one tick.</exception>
    public ClipLoop(Tick start, TickSpan length, bool isEnabled = true)
    {
        Start = start;
        Length = length.Value >= 1 ? length : throw new ArgumentOutOfRangeException(nameof(length), length, "A clip loop must last at least one tick.");
        IsEnabled = isEnabled;
    }

    /// <summary>Where the repeating region starts, in content ticks.</summary>
    public Tick Start { get; }

    /// <summary>How long the repeating region is.</summary>
    public TickSpan Length { get; }

    /// <summary>Whether the region repeats; when off, the clip plays its content once.</summary>
    public bool IsEnabled { get; }

    /// <summary>The first content tick after the region.</summary>
    public Tick End => Start + Length;

    public ClipLoop WithEnabled(bool enabled) => enabled == IsEnabled ? this : new ClipLoop(Start, Length, enabled);

    /// <summary>The same loop over content moved <paramref name="ticks"/> later (content rebased by an edit).</summary>
    public ClipLoop Shifted(long ticks) => ticks == 0 ? this : new ClipLoop(new Tick(Start.Value + ticks), Length, IsEnabled);
}

/// <summary>
/// One pass of a clip over its content: the timeline ticks [<see cref="TimelineStart"/>, + <see cref="Length"/>) play
/// content ticks [<see cref="ContentStart"/>, + <see cref="Length"/>). A clip that does not loop has one pass; a looping
/// clip has a first pass (pickup included) up to the loop's end, then one per repetition, the last possibly cut short.
/// </summary>
public readonly record struct ClipPass(long TimelineStart, long ContentStart, long Length, long Index)
{
    public long ContentEnd => ContentStart + Length;

    public long TimelineEnd => TimelineStart + Length;

    /// <summary>The shift from content ticks to timeline ticks during this pass.</summary>
    public long Shift => TimelineStart - ContentStart;
}

/// <summary>The passes of a clip, in timeline order, without allocating.</summary>
public readonly struct ClipPasses(Clip clip)
{
    public Enumerator GetEnumerator() => new(clip);

    /// <summary>How many passes there are.</summary>
    public long Count
    {
        get
        {
            if (!clip.IsLooping)
            {
                return 1;
            }

            var first = clip.FirstPassLength;
            var rest = clip.Length.Value - first;
            var loop = clip.Loop!.Length.Value;
            return 1 + ((rest + loop - 1) / loop);
        }
    }

    public struct Enumerator(Clip clip)
    {
        private long _elapsed;
        private long _index = -1;

        public ClipPass Current { get; private set; }

        public bool MoveNext()
        {
            var length = clip.Length.Value;
            if (_elapsed >= length)
            {
                return false;
            }

            _index++;
            long content, span;
            if (_index == 0)
            {
                content = clip.ContentAt(0);
                span = clip.FirstPassLength;
            }
            else
            {
                content = clip.Loop!.Start.Value;
                span = Math.Min(clip.Loop.Length.Value, length - _elapsed);
            }

            Current = new ClipPass(clip.Start.Value + _elapsed, content, span, _index);
            _elapsed += span;
            return true;
        }
    }
}

/// <summary>
/// The controller state a looping note clip restores at the start of each repetition, so every pass sounds like the
/// first. The rule is the playback engine's at an arranger-loop wrap (<c>ChaseState.AtWrap</c>): where the state at
/// the loop's start (set by any content before it) differs from the state at its end, the start's value is sent; a value
/// first set inside the loop goes back to its Reset All Controllers value (RP-015: pitch bend centred, pressure and
/// modulation 0, pedals up, expression full) where it has one, and is otherwise left. A program is restored with its
/// bank. Values the content sets exactly at the loop start are not repeated (that event plays anyway). Not chased:
/// notes, SysEx, RPN/NRPN and data entry, bank select on its own, and channel mode messages.
/// </summary>
public static class ClipLoopChase
{
    /// <summary>
    /// The events to send at each repetition's start, at position 0 (the caller moves them), in canonical order. Their
    /// IDs are stable for <paramref name="clip"/>, so the same clip always arranges the same events.
    /// </summary>
    public static ImmutableArray<TrackEvent> AtRepeat(ClipId clip, EventList content, ClipLoop loop)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(loop);
        var atStart = new Dictionary<(byte Channel, int Key), ChannelEvent>();
        var atLoopStartTick = new HashSet<(byte, int)>();
        var atEnd = new Dictionary<(byte Channel, int Key), ChannelEvent>();
        foreach (var e in content.Items)
        {
            if (e.Position >= loop.End)
            {
                break;
            }

            if (e is not ChannelEvent channel || KeyOf(channel) is not { } key)
            {
                continue;
            }

            var at = (channel.Channel.Index, key);
            if (e.Position < loop.Start)
            {
                atStart[at] = channel;
            }
            else if (e.Position == loop.Start)
            {
                atLoopStartTick.Add(at);
            }

            atEnd[at] = channel;
        }

        var result = new List<TrackEvent>();
        foreach (var (at, end) in atEnd.OrderBy(p => p.Key.Channel).ThenBy(p => p.Key.Key))
        {
            if (atLoopStartTick.Contains(at))
            {
                continue;
            }

            var start = atStart.GetValueOrDefault(at);
            if (start is not null && SameValue(start, end))
            {
                continue;
            }

            var restore = start ?? Reset(end);
            if (restore is not null)
            {
                result.Add(restore with { Id = SyntheticId(clip, at), Position = Tick.Zero });
            }
        }

        return [.. result.OrderBy(e => e.Phase)];
    }

    // Program, pitch bend, pressure, and each chased controller have their own key.
    private static int? KeyOf(ChannelEvent e) => e switch
    {
        ProgramEvent => 1000,
        PitchBendEvent => 1001,
        ChannelPressureEvent => 1002,
        ControllerEvent c when IsChased(c.Controller) => c.Controller.Value,
        _ => null,
    };

    private static bool IsChased(ControllerNumber controller) =>
        controller.Value is not (0 or 32 or 6 or 38) and not (>= 96 and <= 101) && !controller.IsChannelMode;

    private static bool SameValue(ChannelEvent a, ChannelEvent b) => (a, b) switch
    {
        (ProgramEvent x, ProgramEvent y) => x.Selection == y.Selection,
        (PitchBendEvent x, PitchBendEvent y) => x.Value == y.Value,
        (ChannelPressureEvent x, ChannelPressureEvent y) => x.Pressure == y.Pressure,
        (ControllerEvent x, ControllerEvent y) => x.Value == y.Value,
        _ => false,
    };

    private static ChannelEvent? Reset(ChannelEvent e) => e switch
    {
        PitchBendEvent bend => bend with { Value = ControlValue.Center },
        ChannelPressureEvent pressure => pressure with { Pressure = ControlValue.Min },
        ControllerEvent { Controller.Value: 1 or 64 or 65 or 66 or 67 } c => c with { Value = ControlValue.Min },
        ControllerEvent { Controller.Value: 11 } c => c with { Value = ControlValue.Max },
        _ => null,
    };

    private static EventId SyntheticId(ClipId clip, (byte Channel, int Key) at)
    {
        Span<byte> bytes = stackalloc byte[16];
        clip.Value.TryWriteBytes(bytes);
        bytes[13] ^= 0x5A;
        bytes[14] ^= at.Channel;
        bytes[15] ^= (byte)at.Key;
        bytes[12] ^= (byte)(at.Key >> 8);
        return new EventId(new Guid(bytes));
    }
}

/// <summary>A clip's loop would make it play more than <see cref="NoteClip.MaxArrangedEvents"/> events. The edit is refused.</summary>
public sealed class ClipLoopLimitException : ArgumentException
{
    public ClipLoopLimitException()
    {
    }

    public ClipLoopLimitException(string message)
        : base(message)
    {
    }

    public ClipLoopLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
