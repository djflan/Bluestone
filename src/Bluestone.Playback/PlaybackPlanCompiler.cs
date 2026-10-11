using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Midi.Wire;
using Bluestone.Signal;

namespace Bluestone.Playback;

/// <summary>Encodes processed event streams into an immutable <see cref="PlaybackPlan"/>.</summary>
/// <remarks>
/// What plays, and on which channel, is decided before this: tracks are rendered, muted or soloed,
/// processed by their device chains, and mapped by their connections in the signal graph. The compiler
/// only encodes MIDI 1.0 at the edge and orders everything canonically (ADR 0003): by tick, phase, origin
/// track, index within the origin, then part.
/// </remarks>
public static class PlaybackPlanCompiler
{
    /// <summary>
    /// Compiles <paramref name="parts"/>. Meta events are not transmitted. SysEx split across raw events is
    /// joined per part and origin track. <paramref name="diagnostics"/> (from routing and the graph) are
    /// kept in the plan with the compiler's own, ordered by track. <paramref name="parameters"/> (translated
    /// hardware parameter automation, ADR 0031) come after <paramref name="parts"/> in part order.
    /// </summary>
    public static PlaybackPlan Compile(Sequence sequence, IReadOnlyList<PlanPart> parts, IEnumerable<PlanDiagnostic>? diagnostics = null, IReadOnlyList<ParameterPart>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(parts);

        var entries = new List<(long Tick, EventPhase Phase, int Origin, int Index, int Part, PlanEvent Event)>();
        var payloads = new List<ByteBlock>();
        var found = new List<PlanDiagnostic>(diagnostics ?? []);

        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            ArgumentNullException.ThrowIfNull(part, nameof(parts));
            ArgumentOutOfRangeException.ThrowIfNegative(part.OutputSlot, nameof(parts));
            var pending = new Dictionary<int, (List<byte> Bytes, long Tick)>();

            foreach (var initial in part.InitialEvents)
            {
                if (initial.Event is ChannelEvent channel)
                {
                    AddChannel(0, channel, initial);
                }
            }

            foreach (var e in part.Events)
            {
                var tick = e.Event.Position.Value;
                switch (e.Event)
                {
                    case ChannelEvent channel:
                        AddChannel(tick, channel, e);
                        break;
                    case SysExEvent sysEx:
                        AddPayload(tick, e, sysEx.Message.Bytes);
                        break;
                    case RawMidiEvent raw:
                        AddRaw(raw, e);
                        break;
                }
            }

            foreach (var (origin, (bytes, _)) in pending)
            {
                if (bytes.Count > 0)
                {
                    Report(origin, "A SysEx message split across events never ended and was not sent.");
                }
            }

            void Add(long tick, EventPhase phase, in SignalEvent key, PlanEvent planEvent) => entries.Add((tick, phase, key.Origin, key.Sequence, partIndex, planEvent));

            void AddPayload(long tick, in SignalEvent key, ByteBlock bytes)
            {
                payloads.Add(bytes);
                Add(tick, EventPhase.SystemExclusive, key, new PlanEvent(tick, part.OutputSlot, default, payloads.Count - 1, 0));
            }

            // Channel events are sent as MIDI 1.0; a note's release is scheduled by the engine.
            void AddChannel(long tick, ChannelEvent e, in SignalEvent key)
            {
                if (e is NoteEvent note)
                {
                    Add(tick, EventPhase.NoteOn, key, new PlanEvent(tick, part.OutputSlot, Midi1Encoder.NoteOn(note), -1, note.Duration.Value, note.ReleaseVelocity));
                    return;
                }

                foreach (var message in Midi1Encoder.Encode(e))
                {
                    Add(tick, e.Phase, key, new PlanEvent(tick, part.OutputSlot, message, -1, 0));
                }
            }

            // Raw events are sent when they are a complete message on their own. SysEx split across
            // several events is joined and sent at the time of its first packet.
            void AddRaw(RawMidiEvent raw, in SignalEvent key)
            {
                if (!pending.TryGetValue(key.Origin, out var state))
                {
                    state = ([], 0);
                }

                var (buffer, startTick) = state;
                var bytes = raw.Bytes.Span;
                if (buffer.Count == 0 && MidiWire.Classify(bytes) != MidiMessageClass.Invalid)
                {
                    AddPayload(raw.Position.Value, key, raw.Bytes);
                    return;
                }

                if (bytes.Length > 0 && bytes[0] == SysExMessage.Start)
                {
                    if (buffer.Count > 0)
                    {
                        Report(key.Origin, "A SysEx message split across events was interrupted by another and was not sent.");
                    }

                    buffer.Clear();
                    startTick = raw.Position.Value;
                }
                else if (buffer.Count == 0)
                {
                    Report(key.Origin, "Raw bytes that are not a complete MIDI message were not sent.");
                    return;
                }

                buffer.AddRange(bytes);
                pending[key.Origin] = (buffer, startTick);
                if (buffer.Count > SysExMessage.MaxLength)
                {
                    Report(key.Origin, "A split SysEx message exceeded the size limit and was not sent.");
                    buffer.Clear();
                    return;
                }

                if (buffer[^1] == SysExMessage.End)
                {
                    var joined = buffer.ToArray();
                    buffer.Clear();
                    if (MidiWire.Classify(joined) == MidiMessageClass.SystemExclusive)
                    {
                        AddPayload(startTick, key, ByteBlock.Copy(joined));
                    }
                    else
                    {
                        Report(key.Origin, "A split SysEx message was malformed and was not sent.");
                    }
                }
            }
        }

        var chases = new List<ParameterChase>();
        parameters ??= [];
        for (var p = 0; p < parameters.Count; p++)
        {
            var part = parameters[p];
            ArgumentNullException.ThrowIfNull(part, nameof(parameters));
            ArgumentOutOfRangeException.ThrowIfNegative(part.OutputSlot, nameof(parameters));
            var partIndex = parts.Count + p;
            foreach (var send in part.Sends)
            {
                var tick = send.Position.Value;
                var messages = new List<ChaseMessage>(send.Events.Length);
                for (var k = 0; k < send.Events.Length; k++)
                {
                    switch (send.Events[k])
                    {
                        case ChannelEvent channel and not NoteEvent:
                            foreach (var message in Midi1Encoder.Encode(channel))
                            {
                                entries.Add((tick, channel.Phase, part.Origin, send.Sequence + k, partIndex, new PlanEvent(tick, part.OutputSlot, message, -1, 0)));
                                messages.Add(new ChaseMessage(part.OutputSlot, message));
                            }

                            break;
                        case SysExEvent sysEx:
                            payloads.Add(sysEx.Message.Bytes);
                            entries.Add((tick, EventPhase.SystemExclusive, part.Origin, send.Sequence + k, partIndex, new PlanEvent(tick, part.OutputSlot, default, payloads.Count - 1, 0)));
                            messages.Add(new ChaseMessage(part.OutputSlot, default, payloads.Count - 1));
                            break;
                        default:
                            throw new ArgumentException("Parameter sends hold only controller-style channel events and SysEx.", nameof(parameters));
                    }
                }

                if (part.Chased)
                {
                    chases.Add(new ParameterChase(tick, p, [.. messages]));
                }
            }
        }

        var ordered = entries
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Phase)
            .ThenBy(e => e.Origin)
            .ThenBy(e => e.Index)
            .ThenBy(e => e.Part)
            .Select(e => e.Event)
            .ToArray();

        var trackOrder = sequence.Tracks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var ordering = found.OrderBy(d => trackOrder.GetValueOrDefault(d.Track, int.MaxValue)).ToList();
        return new PlaybackPlan(sequence.TempoMap, sequence.MeterMap, ordered, [.. payloads], ordering, [.. chases.OrderBy(c => c.Tick)]);

        void Report(int origin, string message) =>
            found.Add(new PlanDiagnostic(origin >= 0 && origin < sequence.Tracks.Length ? sequence.Tracks[origin].Id : default, message));
    }
}
