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
                WriteOptional(ref writer, audio.Host);
                break;
            case SetMixerMeterSettings settings:
                if (settings.Mode > EngineLimits.MaxMixerMeterMode || settings.VuReferenceDbfs is < -36 or > 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(message), "Mixer meter settings are outside the protocol's limits.");
                }

                writer.Byte(settings.Mode);
                writer.Int32(settings.VuReferenceDbfs);
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
                writer.Int32(status.DeviceBufferFrames);
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
                writer.Int32(counters.CallbackThreadPolicy);
                writer.Int64(status.Live.Notes);
                writer.Int64(status.Live.Average);
                writer.Int64(status.Live.Min);
                writer.Int64(status.Live.Max);
                writer.Int64(status.Live.DeadlineMisses);
                var junctions = status.Junctions.IsDefault ? [] : status.Junctions;
                WriteCount(ref writer, junctions.Length, EngineLimits.MaxWatchedJunctions);
                foreach (var junction in junctions)
                {
                    writer.Guid(junction.Key);
                    WriteMeter(ref writer, junction.Levels);
                }

                break;
            case WatchJunctions watch:
                var keys = watch.Keys.IsDefault ? [] : watch.Keys;
                WriteCount(ref writer, keys.Length, EngineLimits.MaxWatchedJunctions);
                foreach (var key in keys)
                {
                    writer.Guid(key);
                }

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
                    reader.Int32In(0, EngineLimits.MaxQueuedFrames),
                    ReadOptionalName(ref reader));
            case EngineMessageType.SetMixerMeterSettings:
                return new SetMixerMeterSettings(reader.ByteUpTo(EngineLimits.MaxMixerMeterMode), reader.Int32In(-36, 0));
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
                        ReadOptionalName(ref reader),
                        reader.Int32In(0, EngineLimits.MaxDeviceBufferFrames));
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
                        reader.Bool(),
                        reader.Int32In(0, EngineLimits.MaxCallbackThreadPolicy));
                    var live = new LiveLatencySpec(reader.NonNegative(), reader.Int64(), reader.Int64(), reader.Int64(), reader.NonNegative());
                    var junctions = ImmutableArray.CreateBuilder<StripMeter>(reader.Count(EngineLimits.MaxWatchedJunctions));
                    for (var i = 0; i < junctions.Capacity; i++)
                    {
                        junctions.Add(new StripMeter(reader.Guid(), ReadMeter(ref reader)));
                    }

                    return new AudioStatus(master, strips.MoveToImmutable(), counters, live) { Junctions = junctions.MoveToImmutable() };
                }

            case EngineMessageType.WatchJunctions:
                {
                    var keys = ImmutableArray.CreateBuilder<Guid>(reader.Count(EngineLimits.MaxWatchedJunctions));
                    for (var i = 0; i < keys.Capacity; i++)
                    {
                        keys.Add(reader.Guid());
                    }

                    return new WatchJunctions(keys.MoveToImmutable());
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
        if (meter.DisplayMode > EngineLimits.MaxMixerMeterMode
            || !ValidMeterDisplayValue(meter.LeftValue, meter.DisplayMode, meter.IsReady)
            || !ValidMeterDisplayValue(meter.RightValue, meter.DisplayMode, meter.IsReady))
        {
            throw new ArgumentException("A mixer-meter display value is outside the protocol's limits.", nameof(meter));
        }

        writer.Single(meter.PeakLeft);
        writer.Single(meter.PeakRight);
        writer.Single(meter.RmsLeft);
        writer.Single(meter.RmsRight);
        writer.Byte(meter.DisplayMode);
        writer.Double(meter.LeftValue);
        writer.Double(meter.RightValue);
        writer.Bool(meter.IsReady);
    }

    private static MeterSpec ReadMeter(ref Reader reader)
    {
        var peakLeft = reader.Level();
        var peakRight = reader.Level();
        var rmsLeft = reader.Level();
        var rmsRight = reader.Level();
        var mode = reader.ByteUpTo(EngineLimits.MaxMixerMeterMode);
        var left = reader.Double();
        var right = reader.Double();
        var ready = reader.Bool();
        if (!ValidMeterDisplayValue(left, mode, ready) || !ValidMeterDisplayValue(right, mode, ready))
        {
            throw new EngineProtocolException("A mixer-meter display value is invalid.");
        }

        return new MeterSpec(peakLeft, peakRight, rmsLeft, rmsRight)
        {
            DisplayMode = mode,
            LeftValue = left,
            RightValue = right,
            IsReady = ready,
        };
    }

    private static bool ValidMeterDisplayValue(double value, byte mode, bool ready) =>
        !double.IsPositiveInfinity(value)
        && (double.IsNaN(value)
            ? mode == EngineLimits.MaxMixerMeterMode && !ready
            : mode != EngineLimits.MaxMixerMeterMode || ready);
}
