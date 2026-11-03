using System.Collections.Immutable;

namespace Bluestone.Engine.Protocol;

/// <summary>Encoding and decoding of the audio messages (migration step 6); every count and value is checked when read.</summary>
public sealed partial class EngineChannel
{
    private static bool EncodeAudio(EngineMessage message, ref Writer writer)
    {
        switch (message)
        {
            case SetAudio audio:
                writer.Int64(audio.Request);
                writer.Bool(audio.Enabled);
                WriteOptional(ref writer, audio.DeviceId);
                writer.Int32(audio.SampleRate);
                writer.Int32(audio.BufferFrames);
                writer.Int32(audio.QueuedFrames);
                writer.Int32(audio.MaxBlockFrames);
                writer.Bool(audio.TestTone);
                writer.Int32(audio.LiveQueuedFrames);
                break;
            case AudioDeviceStatus status:
                writer.Int64(status.Request);
                writer.Byte((byte)status.State);
                WriteOptional(ref writer, status.Problem);
                writer.Int32(status.SampleRate);
                writer.Int32(status.Channels);
                writer.Int32(status.BlockFrames);
                writer.Int32(status.QueuedFrames);
                WriteOptional(ref writer, status.RenderThreadPolicy);
                break;
            case LoadAudioGraph load:
                writer.Int64(load.GraphId);
                writer.String(load.Path);
                writer.Int32(load.SampleRate);
                writer.Int32(load.BlockFrames);
                break;
            case AudioGraphLoaded loaded:
                writer.Int64(loaded.GraphId);
                writer.Bool(loaded.Applied);
                writer.Int32(loaded.LatencyFrames);
                WriteCount(ref writer, loaded.Problems.Length, EngineLimits.MaxGraphProblems);
                foreach (var problem in loaded.Problems)
                {
                    writer.String(problem);
                }

                break;
            case AudioStatus status:
                WriteMeter(ref writer, status.Master);
                WriteCount(ref writer, status.Strips.Length, EngineLimits.MaxMeters);
                foreach (var strip in status.Strips)
                {
                    writer.Guid(strip.Key);
                    WriteMeter(ref writer, strip.Levels);
                }

                var counters = status.Counters;
                writer.Int64(counters.Callbacks);
                writer.Int64(counters.Xruns);
                writer.Int64(counters.Discontinuities);
                writer.Int64(counters.LateRenders);
                writer.Int64(counters.QueueUnderrunFrames);
                writer.Int64(counters.RenderFaults);
                writer.Int64(counters.RenderMax);
                writer.Int64(counters.OutputLatency);
                writer.Int64(counters.PluginFailedBlocks);
                writer.Int32(counters.LatencyFrames);
                writer.Int32(counters.QueuedFrames);
                writer.Bool(counters.LiveQueue);
                writer.Int64(status.Live.Notes);
                writer.Int64(status.Live.Average);
                writer.Int64(status.Live.Min);
                writer.Int64(status.Live.Max);
                writer.Int64(status.Live.DeadlineMisses);
                break;
            case TestRender render:
                writer.Int64(render.Id);
                writer.String(render.Path);
                writer.Int32(render.Frames);
                writer.Int32(render.CallbackFrames);
                var notes = render.Notes.IsDefault ? [] : render.Notes;
                WriteCount(ref writer, notes.Length, EngineLimits.MaxTestNotes);
                foreach (var note in notes)
                {
                    writer.Guid(note.Track);
                    writer.Int32(note.Frame);
                    writer.Int64(note.Lead);
                    writer.Byte(note.Status);
                    writer.Byte(note.Data1);
                    writer.Byte(note.Data2);
                }

                break;
            case TestRendered rendered:
                writer.Int64(rendered.Id);
                WriteOptional(ref writer, rendered.Problem);
                break;
            default:
                return false;
        }

        return true;
    }

    private static EngineMessage DecodeAudio(EngineMessageType type, ref Reader reader)
    {
        switch (type)
        {
            case EngineMessageType.SetAudio:
                return new SetAudio(
                    reader.NonNegative(),
                    reader.Bool(),
                    ReadOptionalName(ref reader),
                    reader.Int32In(0, EngineLimits.MaxSampleRate),
                    reader.Int32In(0, EngineLimits.MaxBufferFrames),
                    reader.Int32In(0, EngineLimits.MaxQueuedFrames),
                    reader.Int32In(1, EngineLimits.MaxBufferFrames),
                    reader.Bool(),
                    reader.Int32In(0, EngineLimits.MaxQueuedFrames));
            case EngineMessageType.AudioDeviceStatus:
                {
                    var request = reader.NonNegative();
                    var state = reader.Byte() switch
                    {
                        (byte)AudioDeviceState.Off => AudioDeviceState.Off,
                        (byte)AudioDeviceState.Running => AudioDeviceState.Running,
                        (byte)AudioDeviceState.Fallback => AudioDeviceState.Fallback,
                        (byte)AudioDeviceState.Waiting => AudioDeviceState.Waiting,
                        var other => throw new EngineProtocolException($"Unknown audio device state {other}."),
                    };
                    return new AudioDeviceStatus(
                        request,
                        state,
                        ReadOptionalName(ref reader),
                        reader.Int32In(0, EngineLimits.MaxSampleRate),
                        reader.Int32In(0, 1024),
                        reader.Int32In(0, EngineLimits.MaxBufferFrames),
                        reader.Int32In(0, EngineLimits.MaxQueuedFrames),
                        ReadOptionalName(ref reader));
                }

            case EngineMessageType.LoadAudioGraph:
                return new LoadAudioGraph(reader.NonNegative(), Name(ref reader), reader.Int32In(1, EngineLimits.MaxSampleRate), reader.Int32In(1, EngineLimits.MaxBufferFrames));
            case EngineMessageType.AudioGraphLoaded:
                {
                    var id = reader.NonNegative();
                    var applied = reader.Bool();
                    var latency = reader.Int32In(0, int.MaxValue);
                    var problems = ImmutableArray.CreateBuilder<string>(reader.Count(EngineLimits.MaxGraphProblems));
                    for (var i = 0; i < problems.Capacity; i++)
                    {
                        problems.Add(Name(ref reader));
                    }

                    return new AudioGraphLoaded(id, applied, latency, problems.MoveToImmutable());
                }

            case EngineMessageType.AudioStatus:
                {
                    var master = ReadMeter(ref reader);
                    var strips = ImmutableArray.CreateBuilder<StripMeter>(reader.Count(EngineLimits.MaxMeters));
                    for (var i = 0; i < strips.Capacity; i++)
                    {
                        strips.Add(new StripMeter(reader.Guid(), ReadMeter(ref reader)));
                    }

                    var counters = new AudioCounters(
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.NonNegative(),
                        reader.Int32In(0, int.MaxValue),
                        reader.Int32In(0, EngineLimits.MaxQueuedFrames),
                        reader.Bool());
                    var live = new LiveLatencySpec(reader.NonNegative(), reader.Int64(), reader.Int64(), reader.Int64(), reader.NonNegative());
                    return new AudioStatus(master, strips.MoveToImmutable(), counters, live);
                }

            case EngineMessageType.TestRender:
                {
                    var id = reader.NonNegative();
                    var path = Name(ref reader);
                    var frames = reader.Int32In(1, EngineLimits.MaxTestRenderFrames);
                    var callback = reader.Int32In(1, EngineLimits.MaxBufferFrames);
                    var notes = ImmutableArray.CreateBuilder<TestNote>(reader.Count(EngineLimits.MaxTestNotes));
                    for (var i = 0; i < notes.Capacity; i++)
                    {
                        var track = reader.Guid();
                        var frame = reader.Int32In(0, frames - 1);
                        var lead = reader.NonNegative();
                        var status = reader.Byte();
                        if (status is < 0x80 or > 0xEF)
                        {
                            throw new EngineProtocolException($"{status:X2} is not a channel message's status.");
                        }

                        notes.Add(new TestNote(track, frame, lead, status, reader.ByteUpTo(127), reader.ByteUpTo(127)));
                    }

                    return new TestRender(id, path, frames, callback, notes.MoveToImmutable());
                }
            case EngineMessageType.TestRendered:
                return new TestRendered(reader.NonNegative(), ReadOptionalName(ref reader));
            default:
                throw new EngineProtocolException($"Unknown message type {(ushort)type}.");
        }
    }

    private static void WriteOptional(ref Writer writer, string? value)
    {
        writer.Bool(value is not null);
        if (value is not null)
        {
            writer.String(value);
        }
    }

    private static string? ReadOptionalName(ref Reader reader) => reader.Bool() ? Name(ref reader) : null;

    private static void WriteMeter(ref Writer writer, in MeterSpec meter)
    {
        writer.Single(meter.PeakLeft);
        writer.Single(meter.PeakRight);
        writer.Single(meter.RmsLeft);
        writer.Single(meter.RmsRight);
    }

    private static MeterSpec ReadMeter(ref Reader reader) => new(reader.Level(), reader.Level(), reader.Level(), reader.Level());
}
