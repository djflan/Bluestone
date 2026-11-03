using System.Collections.Immutable;

namespace Bluestone.Engine.Protocol;

/// <summary>Encoding and decoding of the MIDI messages (migration step 5); every count and value is checked when read.</summary>
public sealed partial class EngineChannel
{
    private static bool EncodeMidi(EngineMessage message, ref Writer writer)
    {
        switch (message)
        {
            case OpenOutput open:
                writer.Int32(open.Handle);
                writer.String(open.Provider);
                writer.String(open.Key);
                writer.String(open.Name);
                writer.Bool(open.Forwarded);
                writer.Bool(open.Scheduled);
                break;
            case CloseOutput close:
                writer.Int32(close.Handle);
                break;
            case SetSlots slots:
                WriteCount(ref writer, slots.Handles.Length, EngineLimits.MaxSlots);
                foreach (var handle in slots.Handles)
                {
                    writer.Int32(handle);
                }

                break;
            case LoadPlan load:
                writer.Int64(load.PlanId);
                writer.String(load.Path);
                break;
            case PlanLoaded loaded:
                writer.Int64(loaded.PlanId);
                break;
            case TransportPlay play:
                writer.Int64(play.From);
                writer.Bool(play.CountIn is not null);
                if (play.CountIn is { } countIn)
                {
                    writer.Int64(countIn.Duration);
                    WriteCount(ref writer, countIn.Clicks.Length, EngineLimits.MaxCountInClicks);
                    foreach (var click in countIn.Clicks)
                    {
                        writer.Int64(click.Offset);
                        writer.Bool(click.Downbeat);
                    }
                }

                writer.Int64(play.Sequence);
                break;
            case TransportStop stop:
                writer.Int64(stop.Sequence);
                break;
            case Panic:
                break;
            case TransportSeek seek:
                writer.Int64(seek.Position);
                writer.Int64(seek.Sequence);
                break;
            case SetLoop loop:
                writer.Bool(loop.Active);
                writer.Int64(loop.Start);
                writer.Int64(loop.End);
                break;
            case SetMetronome metronome:
                writer.Bool(metronome.Click is not null);
                if (metronome.Click is { } sound)
                {
                    writer.Int32(sound.Slot);
                    writer.Byte(sound.Channel);
                    writer.Byte(sound.AccentNote);
                    writer.Byte(sound.AccentVelocity);
                    writer.Byte(sound.BeatNote);
                    writer.Byte(sound.BeatVelocity);
                    writer.Int64(sound.Length);
                }

                writer.Bool(metronome.Enabled);
                break;
            case Nudge nudge:
                writer.Int64(nudge.Amount);
                break;
            case OutputSend send:
                writer.Int32(send.Handle);
                WriteMidiBytes(ref writer, send.Bytes.Span);
                break;
            case SetRoute route:
                writer.Int32(route.Route);
                writer.Guid(route.Track);
                writer.Int32(route.Output);
                writer.Int32(route.Ppqn);
                WriteCount(ref writer, route.Stages.Length, EngineLimits.MaxRouteStages);
                foreach (var stage in route.Stages)
                {
                    writer.String(stage.Device);
                    writer.Int32(stage.Channel);
                    WriteCount(ref writer, stage.Parameters.Length, EngineLimits.MaxStageParameters);
                    foreach (var parameter in stage.Parameters)
                    {
                        writer.UInt32(parameter.Id);
                        writer.UInt32(parameter.Value);
                    }

                    writer.Bool(stage.Plugin is not null);
                    if (stage.Plugin is { } plugin)
                    {
                        WritePlugin(ref writer, plugin);
                    }
                }

                writer.Bool(route.Instrument is not null);
                if (route.Instrument is { } instrument)
                {
                    writer.Guid(instrument);
                }

                break;
            case RemoveRoute remove:
                writer.Int32(remove.Route);
                break;
            case RouteInput input:
                writer.Int32(input.Route);
                writer.Int64(input.Timestamp);
                writer.Byte(input.Status);
                writer.Byte(input.Data1);
                writer.Byte(input.Data2);
                break;
            case SetThru thru:
                writer.Bool(thru.Track is not null);
                if (thru.Track is { } track)
                {
                    writer.Guid(track);
                }

                break;
            case SetInputs inputs:
                WriteCount(ref writer, inputs.Inputs.Length, EngineLimits.MaxInputs);
                foreach (var input in inputs.Inputs)
                {
                    writer.Int32(input.Handle);
                    writer.String(input.Provider);
                    writer.String(input.Key);
                    writer.String(input.Name);
                }

                break;
            case TransportStatus status:
                writer.Bool(status.Playing);
                writer.Int64(status.Position);
                writer.Int64(status.LoopWraps);
                writer.Int64(status.CountInUntil);
                WriteAnchor(ref writer, status.Current);
                WriteAnchor(ref writer, status.Previous);
                writer.Int64(status.Applied);
                break;
            case TimingStatus timing:
                writer.Int64(timing.Dispatched);
                writer.Int64(timing.Late);
                writer.Int64(timing.SkippedLateNotes);
                writer.Int64(timing.Dropped);
                writer.Int64(timing.NoteOverflow);
                writer.Int64(timing.MaxLateness);
                WriteCount(ref writer, timing.Histogram.Length, EngineLimits.MaxHistogramBuckets);
                foreach (var count in timing.Histogram)
                {
                    writer.Int64(count);
                }

                WriteCount(ref writer, timing.Fine.Length, EngineLimits.MaxHistogramBuckets);
                foreach (var bucket in timing.Fine)
                {
                    writer.Int32(bucket.Index);
                    writer.Int64(bucket.Count);
                }

                break;
            case MidiBatch batch:
                WriteCount(ref writer, batch.Events.Length, EngineLimits.MaxBatchEvents);
                foreach (var e in batch.Events)
                {
                    writer.Byte((byte)e.Kind);
                    writer.Int32(e.Handle);
                    writer.Int64(e.Timestamp);
                    WriteMidiBytes(ref writer, e.Bytes.Span);
                }

                break;
            case EngineProblem problem:
                writer.Int32(problem.Handle);
                writer.String(problem.Message);
                writer.Byte((byte)problem.Area);
                break;
            default:
                return EncodeAudio(message, ref writer);
        }

        return true;
    }

    private static EngineMessage DecodeMidi(EngineMessageType type, ref Reader reader)
    {
        switch (type)
        {
            case EngineMessageType.OpenOutput:
                return new OpenOutput(Handle(ref reader), Name(ref reader), Name(ref reader), reader.String(), reader.Bool(), reader.Bool());
            case EngineMessageType.CloseOutput:
                return new CloseOutput(Handle(ref reader));
            case EngineMessageType.SetSlots:
                {
                    var handles = ImmutableArray.CreateBuilder<int>(reader.Count(EngineLimits.MaxSlots));
                    for (var i = 0; i < handles.Capacity; i++)
                    {
                        handles.Add(reader.Int32In(-1, int.MaxValue));
                    }

                    return new SetSlots(handles.MoveToImmutable());
                }

            case EngineMessageType.LoadPlan:
                return new LoadPlan(reader.NonNegative(), Name(ref reader));
            case EngineMessageType.PlanLoaded:
                return new PlanLoaded(reader.NonNegative());
            case EngineMessageType.TransportPlay:
                {
                    var from = reader.Int64();
                    if (from < -1)
                    {
                        throw new EngineProtocolException($"Cannot play from {from}.");
                    }

                    CountInSpec? countIn = null;
                    if (reader.Bool())
                    {
                        var duration = reader.NonNegative();
                        var clicks = ImmutableArray.CreateBuilder<CountInClickSpec>(reader.Count(EngineLimits.MaxCountInClicks));
                        for (var i = 0; i < clicks.Capacity; i++)
                        {
                            clicks.Add(new CountInClickSpec(reader.NonNegative(), reader.Bool()));
                        }

                        countIn = new CountInSpec(duration, clicks.MoveToImmutable());
                    }

                    return new TransportPlay(from, countIn, reader.NonNegative());
                }

            case EngineMessageType.TransportStop:
                return new TransportStop(reader.NonNegative());
            case EngineMessageType.TransportSeek:
                return new TransportSeek(reader.NonNegative(), reader.NonNegative());
            case EngineMessageType.SetLoop:
                {
                    var active = reader.Bool();
                    var start = reader.NonNegative();
                    var end = reader.NonNegative();
                    if (active && end <= start)
                    {
                        throw new EngineProtocolException("A loop must end after it starts.");
                    }

                    return new SetLoop(active, start, end);
                }

            case EngineMessageType.SetMetronome:
                {
                    MetronomeSpec? click = reader.Bool()
                        ? new MetronomeSpec(reader.Int32In(0, EngineLimits.MaxSlots - 1), reader.ByteUpTo(15), reader.ByteUpTo(127), reader.ByteUpTo(127), reader.ByteUpTo(127), reader.ByteUpTo(127), reader.NonNegative())
                        : null;
                    return new SetMetronome(click, reader.Bool());
                }

            case EngineMessageType.Panic:
                return new Panic();
            case EngineMessageType.Nudge:
                return new Nudge(reader.Int64());
            case EngineMessageType.OutputSend:
                return new OutputSend(Handle(ref reader), reader.Bytes(1, EngineLimits.MaxMidiMessageBytes));
            case EngineMessageType.SetRoute:
                {
                    var route = Handle(ref reader);
                    var track = reader.Guid();
                    var output = reader.Int32In(-1, int.MaxValue);
                    var ppqn = reader.Int32In(1, 0x7FFF);
                    var stages = ImmutableArray.CreateBuilder<RouteStageSpec>(reader.Count(EngineLimits.MaxRouteStages));
                    for (var i = 0; i < stages.Capacity; i++)
                    {
                        var device = reader.String();
                        var channel = reader.Int32In(-1, 15);
                        var parameters = ImmutableArray.CreateBuilder<RouteParameter>(reader.Count(EngineLimits.MaxStageParameters));
                        for (var p = 0; p < parameters.Capacity; p++)
                        {
                            parameters.Add(new RouteParameter(reader.UInt32(), reader.UInt32()));
                        }

                        var plugin = reader.Bool() ? ReadPlugin(ref reader) : null;
                        var kinds = (device.Length > 0 ? 1 : 0) + (channel >= 0 ? 1 : 0) + (plugin is not null ? 1 : 0);
                        if (kinds != 1)
                        {
                            throw new EngineProtocolException("A route stage is a device, a plugin, or a channel: exactly one.");
                        }

                        stages.Add(new RouteStageSpec(device, channel, parameters.MoveToImmutable(), plugin));
                    }

                    Guid? instrument = reader.Bool() ? reader.Guid() : null;
                    if ((output < 0) != (instrument is not null))
                    {
                        throw new EngineProtocolException("A route plays to an output or into an instrument: exactly one.");
                    }

                    return new SetRoute(route, track, output, ppqn, stages.MoveToImmutable(), instrument);
                }

            case EngineMessageType.RemoveRoute:
                return new RemoveRoute(Handle(ref reader));
            case EngineMessageType.RouteInput:
                {
                    var route = Handle(ref reader);
                    var timestamp = reader.NonNegative();
                    var status = reader.Byte();
                    if (status is < 0x80 or > 0xEF)
                    {
                        throw new EngineProtocolException($"{status:X2} is not a channel message's status.");
                    }

                    return new RouteInput(route, timestamp, status, reader.ByteUpTo(127), reader.ByteUpTo(127));
                }

            case EngineMessageType.SetThru:
                return new SetThru(reader.Bool() ? reader.Guid() : null);
            case EngineMessageType.SetInputs:
                {
                    var inputs = ImmutableArray.CreateBuilder<InputSpec>(reader.Count(EngineLimits.MaxInputs));
                    for (var i = 0; i < inputs.Capacity; i++)
                    {
                        inputs.Add(new InputSpec(Handle(ref reader), Name(ref reader), Name(ref reader), reader.String()));
                    }

                    return new SetInputs(inputs.MoveToImmutable());
                }

            case EngineMessageType.TransportStatus:
                return new TransportStatus(reader.Bool(), reader.Int64(), reader.NonNegative(), reader.NonNegative(), ReadAnchor(ref reader), ReadAnchor(ref reader), reader.NonNegative());
            case EngineMessageType.TimingStatus:
                {
                    var dispatched = reader.NonNegative();
                    var late = reader.NonNegative();
                    var skipped = reader.NonNegative();
                    var dropped = reader.NonNegative();
                    var overflow = reader.NonNegative();
                    var max = reader.NonNegative();
                    var histogram = ImmutableArray.CreateBuilder<long>(reader.Count(EngineLimits.MaxHistogramBuckets));
                    for (var i = 0; i < histogram.Capacity; i++)
                    {
                        histogram.Add(reader.NonNegative());
                    }

                    var fine = ImmutableArray.CreateBuilder<FineBucket>(reader.Count(EngineLimits.MaxHistogramBuckets));
                    for (var i = 0; i < fine.Capacity; i++)
                    {
                        fine.Add(new FineBucket(reader.Int32In(0, EngineLimits.MaxHistogramBuckets - 1), reader.NonNegative()));
                    }

                    return new TimingStatus(dispatched, late, skipped, dropped, overflow, max, histogram.MoveToImmutable(), fine.MoveToImmutable());
                }

            case EngineMessageType.MidiBatch:
                {
                    var events = ImmutableArray.CreateBuilder<MidiEvent>(reader.Count(EngineLimits.MaxBatchEvents));
                    for (var i = 0; i < events.Capacity; i++)
                    {
                        var kind = reader.Byte() switch
                        {
                            (byte)MidiEventKind.Output => MidiEventKind.Output,
                            (byte)MidiEventKind.Input => MidiEventKind.Input,
                            var other => throw new EngineProtocolException($"Unknown MIDI event kind {other}."),
                        };
                        events.Add(new MidiEvent(kind, Handle(ref reader), reader.NonNegative(), reader.Bytes(1, EngineLimits.MaxMidiMessageBytes)));
                    }

                    return new MidiBatch(events.MoveToImmutable());
                }

            case EngineMessageType.EngineProblem:
                return new EngineProblem(reader.Int32In(-1, int.MaxValue), reader.String(), reader.Byte() switch
                {
                    (byte)EngineProblemArea.Midi => EngineProblemArea.Midi,
                    (byte)EngineProblemArea.Audio => EngineProblemArea.Audio,
                    var other => throw new EngineProtocolException($"Unknown problem area {other}."),
                });
            default:
                return DecodeAudio(type, ref reader);
        }
    }

    private static int Handle(ref Reader reader) => reader.Int32In(0, int.MaxValue);

    private static void WritePlugin(ref Writer writer, PluginStageSpec plugin)
    {
        writer.Guid(plugin.Device);
        writer.String(plugin.Name);
        writer.Int32(plugin.SampleRate);
        writer.Int32(plugin.MaxFrames);
        writer.Int32(plugin.InputChannels);
        writer.Int32(plugin.OutputChannels);
        writer.Int32(plugin.SlotCount);
        writer.Int32(plugin.EventCapacity);
        writer.Int32(plugin.ParameterCapacity);
        writer.Int32(plugin.PipelineDepth);
        writer.Int64(plugin.CollectWait);
        writer.Int32(plugin.LatencyFrames);
        WriteOptional(ref writer, plugin.ExchangePath);
        writer.UInt32(plugin.Generation);
    }

    // Shapes are checked again where the exchange is opened; here every number is kept within the protocol's limits.
    private static PluginStageSpec ReadPlugin(ref Reader reader)
    {
        var device = reader.Guid();
        var name = reader.String();
        var rate = reader.Int32In(1, EngineLimits.MaxSampleRate);
        var maxFrames = reader.Int32In(1, EngineLimits.MaxBufferFrames);
        var inputs = reader.Int32In(0, 64);
        var outputs = reader.Int32In(0, 64);
        var slots = reader.Int32In(1, 64);
        var events = reader.Int32In(0, 65_536);
        var parameters = reader.Int32In(0, 65_536);
        var depth = reader.Int32In(1, slots);
        var wait = reader.NonNegative();
        var latency = reader.Int32In(0, int.MaxValue);
        var path = ReadOptionalName(ref reader);
        return new PluginStageSpec(device, name, rate, maxFrames, inputs, outputs, slots, events, parameters, depth, wait, latency, path, reader.UInt32());
    }

    private static string Name(ref Reader reader)
    {
        var value = reader.String();
        return value.Length > 0 ? value : throw new EngineProtocolException("A name or path cannot be empty.");
    }

    private static AnchorSpec ReadAnchor(ref Reader reader) =>
        new(reader.Bool(), reader.Int64(), reader.Int64(), reader.Int64(), reader.NonNegative(), reader.NonNegative());

    private static void WriteAnchor(ref Writer writer, in AnchorSpec anchor)
    {
        writer.Bool(anchor.Playing);
        writer.Int64(anchor.Time);
        writer.Int64(anchor.SongTime);
        writer.Int64(anchor.PlanId);
        writer.Int64(anchor.LoopStart);
        writer.Int64(anchor.LoopEnd);
    }

    private static void WriteCount(ref Writer writer, int count, int max)
    {
        if (count > max)
        {
            throw new ArgumentException($"{count} items is more than the protocol's {max}.");
        }

        writer.Int32(count);
    }

    private static void WriteMidiBytes(ref Writer writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > EngineLimits.MaxMidiMessageBytes)
        {
            throw new ArgumentException($"A MIDI message of {bytes.Length} bytes is outside the protocol's limits.");
        }

        writer.Bytes(bytes);
    }
}
