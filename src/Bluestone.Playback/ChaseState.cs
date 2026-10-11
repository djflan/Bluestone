using Bluestone.Domain.Midi;
using Bluestone.Midi.Wire;

namespace Bluestone.Playback;

/// <summary>
/// A message to send to <see cref="Slot"/> to bring a device up to date: before playback starts mid-sequence, or at
/// a loop wrap. A SysEx message (from hardware parameter automation) is a payload of the plan, at <see cref="PayloadIndex"/>.
/// </summary>
internal readonly record struct ChaseMessage(int Slot, ChannelMessage Message, int PayloadIndex = -1);

/// <summary>
/// Computes the controller state in effect at a position so starting playback mid-song sounds as it
/// would have from the top: bank select, program, continuous controllers, pitch bend, and channel
/// pressure, per output slot and channel. <see cref="AtWrap"/> does the same for a loop wrap, sending
/// only what differs and resetting what the loop first sets.
/// </summary>
/// <remarks>
/// Deliberately not chased: notes already sounding at the position, SysEx (resending device setup
/// on every seek would be surprising), RPN/NRPN selection and data entry (their meaning depends on
/// sequence), and channel mode messages. The exception is hardware parameter automation (ADR 0031): the
/// plan knows each change's whole message sequence, so the latest one per parameter is resent, after the
/// controller state.
/// </remarks>
internal static class ChaseState
{
    /// <summary>The state set by events before <paramref name="tick"/>.</summary>
    public static ChaseMessage[] Compute(PlaybackPlan plan, long tick)
    {
        var channels = new SortedDictionary<(int Slot, byte Channel), ChannelState>();
        Scan(plan, 0, plan.FirstIndexAtOrAfter(tick), channels);
        var result = new List<ChaseMessage>();
        foreach (var ((slot, _), state) in channels)
        {
            // Bank select first, then program, then the remaining controllers in number order.
            AddIfSet(result, slot, state.Controllers[0]);
            AddIfSet(result, slot, state.Controllers[32]);
            AddIfSet(result, slot, state.Program);
            for (var controller = 1; controller < state.Controllers.Length; controller++)
            {
                if (controller != 32)
                {
                    AddIfSet(result, slot, state.Controllers[controller]);
                }
            }

            AddIfSet(result, slot, state.PitchBend);
            AddIfSet(result, slot, state.Pressure);
        }

        // Then each chased hardware parameter's latest change, whole.
        foreach (var change in LatestParameters(plan, tick).Values)
        {
            result.AddRange(change.Messages);
        }

        return [.. result];
    }

    /// <summary>
    /// What to send when playback wraps from the end of <paramref name="loop"/> to its start, so held
    /// values sound as they did on the first pass: the state at the start wherever it differs from the
    /// state at the end.
    /// </summary>
    /// <remarks>
    /// A changed bank or program is sent whole (bank select, then program), since a bank takes effect
    /// only with the next program change; it is restored only when the loop start has a program, and a
    /// bank first selected inside the loop then goes back to 0. Any other value first set inside the
    /// loop goes back to its Reset All Controllers value (RP-015: pitch bend centred, pressure and
    /// modulation 0, pedals up, expression 127) where it has one, and is otherwise left as it is. Events
    /// at the loop start itself are not included: the cursor sends them right after the wrap.
    /// </remarks>
    public static ChaseMessage[] AtWrap(PlaybackPlan plan, LoopRegion loop)
    {
        // One pass: the state at the loop start, then on to the loop end.
        var channels = new SortedDictionary<(int Slot, byte Channel), ChannelState>();
        var start = plan.FirstIndexAtOrAfter(loop.Start.Value);
        Scan(plan, 0, start, channels);
        var atStart = channels.ToDictionary(c => c.Key, c => c.Value.Clone());
        var nothing = new ChannelState();
        var startTickChannels = new SortedDictionary<(int Slot, byte Channel), ChannelState>();
        Scan(plan, start, plan.FirstIndexAtOrAfter(loop.Start.Value + 1), startTickChannels);
        Scan(plan, start, plan.FirstIndexAtOrAfter(loop.End.Value), channels);

        var result = new List<ChaseMessage>();
        foreach (var ((slot, index), end) in channels)
        {
            var from = atStart.GetValueOrDefault((slot, index)) ?? new ChannelState();
            var sentAtStart = startTickChannels.GetValueOrDefault((slot, index)) ?? nothing;
            var channel = MidiChannel.FromIndex(index);
            ChannelMessage? Target(ChannelMessage? atLoopStart, ChannelMessage? atLoopEnd, ChannelMessage? reset) => atLoopStart ?? (atLoopEnd is null ? null : reset);

            var bankMsb = Target(from.Controllers[0], end.Controllers[0], Controller(channel, 0, 0));
            var bankLsb = Target(from.Controllers[32], end.Controllers[32], Controller(channel, 32, 0));
            // A bank takes effect only with a program change, so the voice is restored only when there is
            // a program to restore.
            if (from.Program is not null && (bankMsb != end.Controllers[0] || bankLsb != end.Controllers[32] || from.Program != end.Program))
            {
                AddUnlessSent(bankMsb, sentAtStart.Controllers[0]);
                AddUnlessSent(bankLsb, sentAtStart.Controllers[32]);
                AddUnlessSent(from.Program, sentAtStart.Program);
            }

            for (var controller = 1; controller < from.Controllers.Length; controller++)
            {
                var target = Target(from.Controllers[controller], end.Controllers[controller], Reset(channel, controller));
                if (controller != 32 && target != end.Controllers[controller])
                {
                    AddUnlessSent(target, sentAtStart.Controllers[controller]);
                }
            }

            var bend = Target(from.PitchBend, end.PitchBend, ChannelMessage.PitchBend(channel, new FourteenBitValue(8192)));
            if (bend != end.PitchBend)
            {
                AddUnlessSent(bend, sentAtStart.PitchBend);
            }

            var pressure = Target(from.Pressure, end.Pressure, ChannelMessage.ChannelPressure(channel, new SevenBitValue(0)));
            if (pressure != end.Pressure)
            {
                AddUnlessSent(pressure, sentAtStart.Pressure);
            }

            void AddUnlessSent(ChannelMessage? message, ChannelMessage? atStartTick)
            {
                if (atStartTick is null)
                {
                    AddIfSet(result, slot, message);
                }
            }
        }

        // A chased hardware parameter goes back to its value at the loop start. One first set inside the loop
        // is left as it is: unlike a controller, it has no reset value.
        var parametersAtEnd = LatestParameters(plan, loop.End.Value);
        foreach (var (key, first) in LatestParameters(plan, loop.Start.Value))
        {
            var last = parametersAtEnd[key];
            var setAtStart = plan.ParameterChases.Any(c => c.Key == key && c.Tick == loop.Start.Value);
            if (!setAtStart && !first.Messages.SequenceEqual(last.Messages))
            {
                result.AddRange(first.Messages);
            }
        }

        return [.. result];
    }

    /// <summary>The latest change of each chased hardware parameter before <paramref name="tick"/>, by key.</summary>
    private static SortedDictionary<int, ParameterChase> LatestParameters(PlaybackPlan plan, long tick)
    {
        var latest = new SortedDictionary<int, ParameterChase>();
        foreach (var change in plan.ParameterChases)
        {
            if (change.Tick >= tick)
            {
                break;
            }

            latest[change.Key] = change;
        }

        return latest;
    }

    private static void Scan(PlaybackPlan plan, int from, int to, SortedDictionary<(int Slot, byte Channel), ChannelState> channels)
    {
        for (var i = from; i < to; i++)
        {
            var e = plan.Events[i];
            if (e.PayloadIndex >= 0 || e.IsNote)
            {
                continue;
            }

            var message = e.Message;
            var key = (e.Slot, message.Channel.Index);
            if (!channels.TryGetValue(key, out var state))
            {
                state = new ChannelState();
                channels[key] = state;
            }

            switch (message.Kind)
            {
                case ChannelMessageKind.ControlChange when IsChased(message.Data1):
                    state.Controllers[message.Data1] = message;
                    break;
                case ChannelMessageKind.ProgramChange:
                    state.Program = message;
                    break;
                case ChannelMessageKind.PitchBend:
                    state.PitchBend = message;
                    break;
                case ChannelMessageKind.ChannelPressure:
                    state.Pressure = message;
                    break;
            }
        }
    }

    private static void AddIfSet(List<ChaseMessage> result, int slot, ChannelMessage? message)
    {
        if (message is { } m)
        {
            result.Add(new ChaseMessage(slot, m));
        }
    }

    private static ChannelMessage Controller(MidiChannel channel, int controller, int value) =>
        ChannelMessage.ControlChange(channel, new ControllerNumber(controller), new SevenBitValue(value));

    /// <summary>The value Reset All Controllers (RP-015) gives <paramref name="controller"/>, if it resets it.</summary>
    private static ChannelMessage? Reset(MidiChannel channel, int controller) => controller switch
    {
        1 or 64 or 65 or 66 or 67 => Controller(channel, controller, 0),
        11 => Controller(channel, controller, 127),
        _ => null,
    };

    private static bool IsChased(byte controller) =>
        controller is not (6 or 38) and not (>= 96 and <= 101) and < 120;

    private sealed class ChannelState
    {
        public ChannelMessage?[] Controllers { get; private init; } = new ChannelMessage?[120];

        public ChannelMessage? Program { get; set; }

        public ChannelMessage? PitchBend { get; set; }

        public ChannelMessage? Pressure { get; set; }

        public ChannelState Clone() => new() { Controllers = [.. Controllers], Program = Program, PitchBend = PitchBend, Pressure = Pressure };
    }
}
