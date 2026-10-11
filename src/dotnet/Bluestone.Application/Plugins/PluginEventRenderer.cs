using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Midi;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Plugins;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.Signal;
using ParameterChange = Bluestone.Domain.Sequencing.ParameterChange;
using WorkerParameterValue = Bluestone.Plugins.Protocol.ParameterValue;

namespace Bluestone.Application.Plugins;

/// <summary>
/// Runs plugin MIDI effects in their workers when the plan is compiled, so what they put out is routed downstream like
/// any other device's output (ADR 0025). Each render is a fresh copy of the plugin in the device's own worker, started
/// from the state and parameter values the project stores; the live instance is not touched. Positions travel as
/// sample frames on the project's tempo map and come back as ticks: an event the plugin puts out in answer to an input
/// takes that input's exact tick, even when several ticks share a frame. A failure (worker gone or not answering, plugin error, request over the protocol's limits) leaves the
/// events unchanged and is reported; it never fails the plan.
/// </summary>
internal sealed class PluginEventRenderer(PluginDeviceHost host) : IOutOfProcessRenderer
{
    public bool CanRender(DeviceInstance device, DeviceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return host.PluginOf(definition.Id) is { Kind: PluginKind.MidiEffect };
    }

    public OutOfProcessResult Render(in OutOfProcessBlock block, ReadOnlySpan<SignalEvent> input, ReadOnlySpan<ParameterChange> changes)
    {
        if (host.InstanceOf(block.Device.Id) is not { } instance || instance.Status.State != PluginInstanceState.Running)
        {
            return OutOfProcessResult.Failed("is not running, so its output could not be worked out for playback.");
        }

        var timeline = new Timeline(block.Tempo, instance.Request.SampleRate);
        var sent = Encode(input, timeline, out var around);
        var parameterChanges = changes.ToArray().Select(c => new TimelineParameterChange(timeline.FrameAt(c.Position), c.Parameter.Value, c.Value.ToFraction())).ToImmutableArray();
        var lastFrame = Math.Max(sent.Count == 0 ? 0 : sent[^1].Frame, parameterChanges.IsEmpty ? 0 : parameterChanges.Max(c => c.Frame));
        var endFrame = Math.Min(lastFrame + instance.Request.MaxBlockFrames, ProtocolLimits.MaxRenderFrames);
        if (sent.Count > ProtocolLimits.MaxRenderEvents || parameterChanges.Length > ProtocolLimits.MaxRenderParameterChanges || lastFrame >= ProtocolLimits.MaxRenderFrames)
        {
            return OutOfProcessResult.Failed("has more to process than one render request can carry.");
        }

        var tempo = block.Tempo.Changes.Take(ProtocolLimits.MaxRenderTempoChanges).Select(c => new TimelineTempo(timeline.FrameAt(c.Position), c.Tempo.BeatsPerMinute)).ToImmutableArray();
        var device = block.Device;
        var state = device.State is { } saved ? new PluginStateData(saved.Format, [.. saved.Data.Span]) : null;
        var parameters = device.Parameters.Select(p => new WorkerParameterValue(p.Id.Value, p.Value.ToFraction())).ToImmutableArray();
        var events = sent.Select(s => new TimelineEvent(s.Frame, s.Event)).ToImmutableArray();
        RenderedEvents reply;
        try
        {
            // The plugin layer awaits without a captured context; running it on the pool keeps a UI thread out of it.
            reply = Task.Run(() => instance.RenderEventsAsync(state, parameters, endFrame, tempo, events, parameterChanges)).GetAwaiter().GetResult();
        }
        catch (PluginHostException ex)
        {
            return OutOfProcessResult.Failed($"stopped while working out its output for playback ({ex.Message}).");
        }
        catch (ProtocolException ex)
        {
            return OutOfProcessResult.Failed($"could not be asked to work out its output for playback ({ex.Message}).");
        }

        var messages = ImmutableArray.CreateBuilder<string>();
        if (reply.StateRestoreError is { } stateError)
        {
            messages.Add($"its saved state was rejected ({stateError}); it ran with default settings.");
        }

        if (reply.Dropped > 0)
        {
            messages.Add(string.Create(CultureInfo.InvariantCulture, $"{reply.Dropped} events it put out did not fit and were dropped."));
        }

        var output = Decode(reply.Events, sent, input, timeline, new EventId(device.Id.Value), out var hanging);
        if (hanging > 0)
        {
            messages.Add(string.Create(CultureInfo.InvariantCulture, $"{hanging} notes it started never ended; they end with its last input."));
        }

        output.AddRange(around);
        return new OutOfProcessResult(output.ToImmutable(), messages.ToImmutable());
    }

    // Input as plugin events in timeline order (note-offs before note-ons at one frame), and what cannot be sent.
    private static List<Sent> Encode(ReadOnlySpan<SignalEvent> input, Timeline timeline, out List<SignalEvent> around)
    {
        var sent = new List<Sent>(input.Length * 2);
        around = [];
        for (var i = 0; i < input.Length; i++)
        {
            var e = input[i].Event;
            var frame = timeline.FrameAt(e.Position);
            var at = e.Position;
            switch (e)
            {
                case NoteEvent n:
                    // A note shorter than a frame still ends a frame after it starts, so the plugin sees it start first.
                    sent.Add(new Sent(frame, 1, PluginEvent.NoteOn(0, n.Channel.Index, n.Note.Value, n.Velocity.Value), i, at));
                    sent.Add(new Sent(Math.Max(frame + 1, timeline.FrameAt(n.EndPosition)), 0, PluginEvent.NoteOff(0, n.Channel.Index, n.Note.Value, n.ReleaseVelocity.Value), i, n.EndPosition));
                    break;
                case NoteOffEvent n:
                    sent.Add(new Sent(frame, 0, PluginEvent.NoteOff(0, n.Channel.Index, n.Note.Value, n.ReleaseVelocity.Value), i, at));
                    break;
                case PolyPressureEvent p:
                    sent.Add(new Sent(frame, 1, PluginEvent.PolyPressure(0, p.Channel.Index, p.Note.Value, p.Pressure.ToSevenBit()), i, at));
                    break;
                case ControllerEvent c:
                    sent.Add(new Sent(frame, 1, PluginEvent.ControlChange(0, c.Channel.Index, c.Controller.Value, c.Value.ToSevenBit()), i, at));
                    break;
                case ChannelPressureEvent c:
                    sent.Add(new Sent(frame, 1, PluginEvent.ChannelPressure(0, c.Channel.Index, c.Pressure.ToSevenBit()), i, at));
                    break;
                case PitchBendEvent b:
                    sent.Add(new Sent(frame, 1, PluginEvent.PitchBend(0, b.Channel.Index, b.Value.ToFourteenBit()), i, at));
                    break;
                case ProgramEvent { Selection: { BankMsb: null, BankLsb: null } } program:
                    sent.Add(new Sent(frame, 1, PluginEvent.ProgramChange(0, program.Channel.Index, program.Selection.Program.Value), i, at));
                    break;
                case SysExEvent s when PluginEvent.TryCreateSystemExclusive(0, s.Message.Bytes.Span, out var sysEx):
                    sent.Add(new Sent(frame, 1, sysEx, i, at));
                    break;
                default:
                    around.Add(input[i]);
                    break;
            }
        }

        // A stable sort: same frame and rank keep the input's canonical order.
        return [.. sent.OrderBy(s => s.Frame).ThenBy(s => s.Rank)];
    }

    private static ImmutableArray<SignalEvent>.Builder Decode(ImmutableArray<TimelineEvent> rendered, List<Sent> sent, ReadOnlySpan<SignalEvent> input, Timeline timeline, EventId seed, out int hanging)
    {
        var output = ImmutableArray.CreateBuilder<SignalEvent>(rendered.Length);
        var pending = new Dictionary<(int Channel, int Note), Queue<Started>>();
        var frames = sent.Select(s => s.Frame).ToArray();
        var seenAtFrame = new Dictionary<long, int>();
        var lastTick = Tick.Zero;
        foreach (var e in input)
        {
            lastTick = Max(lastTick, e.Event is NoteEvent n ? n.EndPosition : e.Event.Position);
        }

        foreach (var (frame, e) in rendered)
        {
            var ordinal = seenAtFrame.GetValueOrDefault(frame);
            seenAtFrame[frame] = ordinal + 1;
            var (source, id, matched) = Origin(frame, ordinal, e, sent, frames, input, seed);
            var tick = matched ?? timeline.TickAt(frame);
            var channel = MidiChannel.FromIndex(e.Channel);
            switch (e.Kind)
            {
                case PluginEventKind.NoteOn when e.Data2 > 0:
                    {
                        var key = (e.Channel, e.Data1);
                        if (!pending.TryGetValue(key, out var queue))
                        {
                            pending[key] = queue = new Queue<Started>();
                        }

                        queue.Enqueue(new Started(tick, e.Data2, source, id));
                        break;
                    }

                case PluginEventKind.NoteOn:
                case PluginEventKind.NoteOff:
                    if (pending.TryGetValue((e.Channel, e.Data1), out var started) && started.TryDequeue(out var on))
                    {
                        output.Add(Note(on, tick, channel, e.Data1, e.Kind == PluginEventKind.NoteOff ? e.Data2 : Velocity.DefaultRelease.Value));
                    }
                    else
                    {
                        output.Add(source.With(new NoteOffEvent(id, tick, channel, new NoteNumber(e.Data1), new Velocity(e.Data2))));
                    }

                    break;
                case PluginEventKind.PolyPressure:
                    output.Add(source.With(new PolyPressureEvent(id, tick, channel, new NoteNumber(e.Data1), ControlValue.FromSevenBit(e.Data2))));
                    break;
                case PluginEventKind.ControlChange:
                    output.Add(source.With(new ControllerEvent(id, tick, channel, new ControllerNumber(e.Data1), ControlValue.FromSevenBit(e.Data2))));
                    break;
                case PluginEventKind.ProgramChange:
                    output.Add(source.With(new ProgramEvent(id, tick, channel, new ProgramSelection(new ProgramNumber(e.Data1)))));
                    break;
                case PluginEventKind.ChannelPressure:
                    output.Add(source.With(new ChannelPressureEvent(id, tick, channel, ControlValue.FromSevenBit(e.Data1))));
                    break;
                case PluginEventKind.PitchBend:
                    output.Add(source.With(new PitchBendEvent(id, tick, channel, ControlValue.FromFourteenBit(e.Data1 | (e.Data2 << 7)))));
                    break;
                case PluginEventKind.SystemExclusive when SysExMessage.TryCreate(e.SystemExclusiveData, out var message, out _):
                    output.Add(source.With(new SysExEvent(id, tick, message)));
                    break;
                default:
                    break;
            }
        }

        hanging = 0;
        foreach (var ((channelIndex, note), queue) in pending)
        {
            foreach (var on in queue)
            {
                hanging++;
                output.Add(Note(on, Max(lastTick, on.Position + new TickSpan(1)), MidiChannel.FromIndex(channelIndex), note, Velocity.DefaultRelease.Value));
            }
        }

        return output;
    }

    private static SignalEvent Note(Started on, Tick end, MidiChannel channel, int note, int release) =>
        on.Source.With(new NoteEvent(on.Id, on.Position, new TickSpan(Math.Max(1, end.Value - on.Position.Value)), channel, new NoteNumber(note), new Velocity(on.Velocity), new Velocity(release)));

    // The input an output event belongs to, for its place in the order, its tick, and its identity. The n-th event out at
    // a frame matches the n-th event in at that frame and takes its exact tick; it keeps that input's ID when it is the
    // same kind of event (a note moved, say) and gets an ID derived from it otherwise. An event at a frame with no input
    // belongs to the last input before it, and its tick comes from the tempo map.
    private static (SignalEvent Source, EventId Id, Tick? Tick) Origin(long frame, int ordinal, in PluginEvent e, List<Sent> sent, long[] frames, ReadOnlySpan<SignalEvent> input, EventId seed)
    {
        if (sent.Count == 0)
        {
            return (default, DerivedId(seed, frame, ordinal), null);
        }

        var first = LowerBound(frames, frame);
        var count = 0;
        while (first + count < frames.Length && frames[first + count] == frame)
        {
            count++;
        }

        if (count > 0)
        {
            var match = sent[first + Math.Min(ordinal, count - 1)];
            var source = input[match.Source];
            return ordinal < count && match.Event.Kind == e.Kind
                ? (source, source.Event.Id, match.Tick)
                : (source, DerivedId(source.Event.Id, frame, ordinal), match.Tick);
        }

        var before = sent[Math.Max(0, first - 1)];
        return (input[before.Source], DerivedId(input[before.Source].Event.Id, frame, ordinal), null);
    }

    private static int LowerBound(long[] frames, long frame)
    {
        int low = 0, high = frames.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (frames[middle] < frame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // The source's ID with the frame and ordinal mixed into its last eight bytes: stable across compiles.
    private static EventId DerivedId(EventId source, long frame, int ordinal)
    {
        Span<byte> bytes = stackalloc byte[16];
        source.Value.TryWriteBytes(bytes);
        var mixed = BitConverter.ToUInt64(bytes[8..]) ^ ((ulong)frame * 0x9E3779B97F4A7C15UL) ^ ((ulong)(ordinal + 1) * 0xC2B2AE3D27D4EB4FUL) ^ 0x5A5A_5A5A_5A5A_5A5AUL;
        BitConverter.TryWriteBytes(bytes[8..], mixed);
        return new EventId(new Guid(bytes));
    }

    private static Tick Max(Tick a, Tick b) => a >= b ? a : b;

    private readonly record struct Sent(long Frame, int Rank, PluginEvent Event, int Source, Tick Tick);

    private readonly record struct Started(Tick Position, int Velocity, SignalEvent Source, EventId Id);

    // Ticks to sample frames on the tempo map, and back (for events that answer no input).
    private sealed class Timeline(TempoMap tempo, double sampleRate)
    {
        public long FrameAt(Tick position) => (long)Math.Round(tempo.TimeAt(position).Ticks * sampleRate / TimeSpan.TicksPerSecond);

        public Tick TickAt(long frame) => tempo.TickAt(TimeSpan.FromTicks((long)Math.Round(frame * TimeSpan.TicksPerSecond / sampleRate)));
    }
}
