using System.Buffers.Binary;
using System.Collections.Immutable;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.Plugins.Protocol;

/// <summary>
/// Encodes and decodes control-plane frames: a uint32 little-endian body length, then the body (uint16 message type,
/// uint32 request id, payload). Decoding is strict: anything malformed, truncated, oversized, unknown, out of range,
/// or followed by trailing bytes throws <see cref="ProtocolException"/> and nothing else.
/// </summary>
public static class FrameCodec
{
    private const int GuidBytes = 16;

    public static byte[] Encode(uint requestId, ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var writer = new WireWriter();
        writer.WriteUInt32(0);
        writer.WriteUInt16((ushort)message.Type);
        writer.WriteUInt32(requestId);
        WritePayload(writer, message);

        var bodyLength = writer.Length - ProtocolLimits.LengthPrefixBytes;
        if (bodyLength > ProtocolLimits.MaxFrameBytes)
        {
            throw new ProtocolException($"Frame of {bodyLength} bytes exceeds the {ProtocolLimits.MaxFrameBytes}-byte limit.");
        }

        var frame = writer.Written.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(frame, bodyLength);
        return frame;
    }

    /// <summary>Decodes one complete frame, including its length prefix. The span must hold exactly one frame.</summary>
    public static Frame Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < ProtocolLimits.LengthPrefixBytes)
        {
            throw new ProtocolException("Truncated frame: no length prefix.");
        }

        var length = ValidateBodyLength(BinaryPrimitives.ReadUInt32LittleEndian(frame));
        if (frame.Length - ProtocolLimits.LengthPrefixBytes != length)
        {
            throw new ProtocolException($"Frame declares {length} body bytes but {frame.Length - ProtocolLimits.LengthPrefixBytes} are present.");
        }

        return DecodeBody(frame[ProtocolLimits.LengthPrefixBytes..]);
    }

    /// <summary>
    /// Reads one frame. Returns null on a clean end of stream before the first byte of a frame. A stream that ends
    /// inside a frame throws <see cref="ProtocolException"/>; transport failures surface as <see cref="IOException"/>.
    /// </summary>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[ProtocolLimits.LengthPrefixBytes];
        var read = 0;
        while (read < prefix.Length)
        {
            var n = await stream.ReadAsync(prefix.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? null : throw new ProtocolException("Truncated frame: the stream ended inside the length prefix.");
            }

            read += n;
        }

        var length = ValidateBodyLength(BinaryPrimitives.ReadUInt32LittleEndian(prefix));
        var body = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new ProtocolException("Truncated frame: the stream ended inside the frame body.", ex);
        }

        return DecodeBody(body);
    }

    public static async Task WriteAsync(Stream stream, uint requestId, ProtocolMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = Encode(requestId, message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int ValidateBodyLength(uint length)
    {
        if (length < ProtocolLimits.FrameHeaderBytes)
        {
            throw new ProtocolException($"Frame body of {length} bytes is shorter than the frame header.");
        }

        if (length > ProtocolLimits.MaxFrameBytes)
        {
            throw new ProtocolException($"Frame body of {length} bytes exceeds the {ProtocolLimits.MaxFrameBytes}-byte limit.");
        }

        return (int)length;
    }

    private static Frame DecodeBody(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new WireReader(body);
            var type = (MessageType)reader.ReadUInt16();
            var requestId = reader.ReadUInt32();
            var message = ReadPayload(type, ref reader);
            reader.EnsureEnd();
            return new Frame(requestId, message);
        }
        catch (ArgumentException ex)
        {
            // Constructors validate their arguments; on the wire that is a protocol violation.
            throw new ProtocolException($"Invalid message content: {ex.Message}", ex);
        }
    }

    private static void WritePayload(WireWriter w, ProtocolMessage message)
    {
        switch (message)
        {
            case Hello m:
                w.WriteInt32(m.ProtocolVersion);
                w.WriteString(m.Token);
                w.WriteInt32(m.ProcessId);
                w.WriteByte((byte)m.Mode);
                break;
            case HelloAck m:
                w.WriteInt32(m.ProtocolVersion);
                break;
            case Ping m:
                w.WriteInt64(m.Sequence);
                break;
            case Pong m:
                w.WriteInt64(m.Sequence);
                break;
            case CreateInstance m:
                w.WriteGuid(m.InstanceId.Value);
                WriteIdentity(w, m.Plugin);
                w.WriteDouble(m.SampleRate);
                WriteExchangeOptions(w, m.Exchange);
                w.WriteUInt32(m.Generation);
                w.WriteString(m.DataPlaneDirectory);
                WriteOptionalState(w, m.State);
                w.WriteOptionalString(m.ModulePath);
                break;
            case InstanceCreated m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteInt32(m.LatencyFrames);
                w.WriteUInt32((uint)m.Parameters.Length);
                foreach (var p in m.Parameters)
                {
                    w.WriteUInt32(p.Id);
                    w.WriteString(p.Name);
                    w.WriteDouble(p.DefaultValue);
                    w.WriteInt32(p.StepCount);
                    w.WriteString(p.Units);
                    w.WriteByte((byte)p.Traits);
                }

                w.WriteString(m.DataPlanePath);
                w.WriteUInt32(m.Generation);
                w.WriteOptionalString(m.StateRestoreError);
                break;
            case DestroyInstance m:
                w.WriteGuid(m.InstanceId.Value);
                break;
            case CaptureState m:
                w.WriteGuid(m.InstanceId.Value);
                break;
            case StateResult m:
                w.WriteGuid(m.InstanceId.Value);
                WriteState(w, m.State);
                break;
            case RestoreState m:
                w.WriteGuid(m.InstanceId.Value);
                WriteState(w, m.State);
                break;
            case RestoreStateResult m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteBool(m.Succeeded);
                w.WriteOptionalString(m.Message);
                break;
            case SetParameter m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteUInt32(m.ParameterId);
                w.WriteDouble(m.Value);
                break;
            case GetParameters m:
                w.WriteGuid(m.InstanceId.Value);
                break;
            case ParameterValues m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteUInt32((uint)m.Values.Length);
                foreach (var v in m.Values)
                {
                    w.WriteUInt32(v.Id);
                    w.WriteDouble(v.Value);
                }

                break;
            case Ack:
            case Shutdown:
                break;
            case ErrorReply m:
                w.WriteUInt16((ushort)m.Code);
                w.WriteString(m.Message);
                break;
            case ScanModule m:
                w.WriteString(m.ModulePath);
                break;
            case ScanResult m:
                w.WriteString(m.ModulePath);
                w.WriteUInt32((uint)m.Plugins.Length);
                foreach (var plugin in m.Plugins)
                {
                    WriteIdentity(w, plugin);
                }

                w.WriteByte(m.Failure is { } failure ? (byte)failure : (byte)0);
                w.WriteOptionalString(m.Message);
                break;
            case InduceTestFault m:
                w.WriteByte((byte)m.Fault);
                break;
            case RenderEvents m:
                WriteIdentity(w, m.Plugin);
                WriteOptionalState(w, m.State);
                w.WriteUInt32((uint)m.Parameters.Length);
                foreach (var v in m.Parameters)
                {
                    w.WriteUInt32(v.Id);
                    w.WriteDouble(v.Value);
                }

                w.WriteDouble(m.SampleRate);
                w.WriteInt32(m.BlockFrames);
                w.WriteInt64(m.EndFrame);
                w.WriteUInt32((uint)m.Tempo.Length);
                foreach (var t in m.Tempo)
                {
                    w.WriteInt64(t.Frame);
                    w.WriteDouble(t.BeatsPerMinute);
                }

                WriteTimelineEvents(w, m.Events);
                w.WriteUInt32((uint)m.Changes.Length);
                foreach (var c in m.Changes)
                {
                    w.WriteInt64(c.Frame);
                    w.WriteUInt32(c.ParameterId);
                    w.WriteDouble(c.Value);
                }

                w.WriteOptionalString(m.ModulePath);
                break;
            case RenderedEvents m:
                WriteTimelineEvents(w, m.Events);
                w.WriteInt32(m.Dropped);
                w.WriteOptionalString(m.StateRestoreError);
                break;
            case OpenEditor m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteString(m.Title);
                break;
            case CloseEditor m:
                w.WriteGuid(m.InstanceId.Value);
                break;
            case SetEditorVisible m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteBool(m.Visible);
                break;
            case SetEditorKeyboard m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteBool(m.ToHost);
                break;
            case PluginNotification m:
                w.WriteGuid(m.InstanceId.Value);
                w.WriteByte((byte)m.Kind);
                w.WriteUInt32(m.ParameterId);
                w.WriteDouble(m.Value);
                break;
            default:
                throw new ProtocolException($"Cannot encode message {message.GetType().Name}.");
        }
    }

    private static ProtocolMessage ReadPayload(MessageType type, ref WireReader r)
    {
        switch (type)
        {
            case MessageType.Hello:
                {
                    var version = r.ReadInt32();
                    var token = r.ReadString();
                    var processId = r.ReadInt32();
                    var mode = (WorkerMode)r.ReadByte();
                    if (!Enum.IsDefined(mode))
                    {
                        throw new ProtocolException($"Unknown worker mode {(byte)mode}.");
                    }

                    return new Hello(version, token, processId, mode);
                }

            case MessageType.HelloAck:
                return new HelloAck(r.ReadInt32());
            case MessageType.Ping:
                return new Ping(r.ReadInt64());
            case MessageType.Pong:
                return new Pong(r.ReadInt64());
            case MessageType.CreateInstance:
                {
                    var id = ReadInstanceId(ref r);
                    var plugin = ReadIdentity(ref r);
                    var sampleRate = r.ReadDouble();
                    if (!(sampleRate is >= ProtocolLimits.MinSampleRate and <= ProtocolLimits.MaxSampleRate))
                    {
                        throw new ProtocolException($"Sample rate {sampleRate} is out of range.");
                    }

                    var exchange = ReadExchangeOptions(ref r);
                    var generation = ReadGeneration(ref r);
                    var directory = r.ReadString();
                    var state = ReadOptionalState(ref r);
                    var modulePath = r.ReadOptionalString();
                    return new CreateInstance(id, plugin, sampleRate, exchange, generation, directory, state, modulePath);
                }

            case MessageType.InstanceCreated:
                {
                    var id = ReadInstanceId(ref r);
                    var latency = r.ReadInt32InRange(0, 1 << 24, "Latency");
                    var count = r.ReadCount(ProtocolLimits.MaxParameters, minElementBytes: 4 + 2 + 8 + 4 + 2 + 1);
                    var parameters = ImmutableArray.CreateBuilder<ParameterDescriptor>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var parameterId = r.ReadUInt32();
                        var name = r.ReadString();
                        var defaultValue = r.ReadNormalized("Default value");
                        var steps = r.ReadInt32InRange(0, int.MaxValue, "Step count");
                        var units = r.ReadString();
                        var traits = (ParameterTraits)r.ReadByte();
                        if ((traits & ~ParameterDescriptor.AllTraits) != 0)
                        {
                            throw new ProtocolException($"Unknown parameter traits {(byte)traits}.");
                        }

                        parameters.Add(new ParameterDescriptor(parameterId, name, defaultValue, steps, units, traits));
                    }

                    var path = r.ReadString();
                    var generation = ReadGeneration(ref r);
                    var error = r.ReadOptionalString();
                    return new InstanceCreated(id, latency, parameters.MoveToImmutable(), path, generation, error);
                }

            case MessageType.DestroyInstance:
                return new DestroyInstance(ReadInstanceId(ref r));
            case MessageType.CaptureState:
                return new CaptureState(ReadInstanceId(ref r));
            case MessageType.StateResult:
                return new StateResult(ReadInstanceId(ref r), ReadState(ref r));
            case MessageType.RestoreState:
                return new RestoreState(ReadInstanceId(ref r), ReadState(ref r));
            case MessageType.RestoreStateResult:
                return new RestoreStateResult(ReadInstanceId(ref r), r.ReadBool(), r.ReadOptionalString());
            case MessageType.SetParameter:
                return new SetParameter(ReadInstanceId(ref r), r.ReadUInt32(), r.ReadNormalized("Parameter value"));
            case MessageType.GetParameters:
                return new GetParameters(ReadInstanceId(ref r));
            case MessageType.ParameterValues:
                {
                    var id = ReadInstanceId(ref r);
                    var count = r.ReadCount(ProtocolLimits.MaxParameters, minElementBytes: 12);
                    var values = ImmutableArray.CreateBuilder<ParameterValue>(count);
                    for (var i = 0; i < count; i++)
                    {
                        values.Add(new ParameterValue(r.ReadUInt32(), r.ReadNormalized("Parameter value")));
                    }

                    return new ParameterValues(id, values.MoveToImmutable());
                }

            case MessageType.Ack:
                return new Ack();
            case MessageType.Shutdown:
                return new Shutdown();
            case MessageType.Error:
                {
                    var code = (ErrorCode)r.ReadUInt16();
                    if (!Enum.IsDefined(code))
                    {
                        throw new ProtocolException($"Unknown error code {(ushort)code}.");
                    }

                    return new ErrorReply(code, r.ReadString());
                }

            case MessageType.ScanModule:
                return new ScanModule(r.ReadString());
            case MessageType.ScanResult:
                {
                    var path = r.ReadString();
                    var count = r.ReadCount(ProtocolLimits.MaxPluginsPerModule, minElementBytes: (6 * 2) + 1);
                    var plugins = ImmutableArray.CreateBuilder<PluginIdentity>(count);
                    for (var i = 0; i < count; i++)
                    {
                        plugins.Add(ReadIdentity(ref r));
                    }

                    var failureByte = r.ReadByte();
                    ScanFailureKind? failure = null;
                    if (failureByte != 0)
                    {
                        var kind = (ScanFailureKind)failureByte;
                        if (!Enum.IsDefined(kind))
                        {
                            throw new ProtocolException($"Unknown scan failure {failureByte}.");
                        }

                        failure = kind;
                    }

                    return new ScanResult(path, plugins.MoveToImmutable(), failure, r.ReadOptionalString());
                }

            case MessageType.InduceTestFault:
                {
                    var fault = (TestFault)r.ReadByte();
                    if (!Enum.IsDefined(fault))
                    {
                        throw new ProtocolException($"Unknown test fault {(byte)fault}.");
                    }

                    return new InduceTestFault(fault);
                }

            case MessageType.RenderEvents:
                return ReadRenderEvents(ref r);
            case MessageType.RenderedEvents:
                return new RenderedEvents(ReadTimelineEvents(ref r), r.ReadInt32InRange(0, int.MaxValue, "Dropped count"), r.ReadOptionalString());
            case MessageType.OpenEditor:
                return new OpenEditor(ReadInstanceId(ref r), r.ReadString());
            case MessageType.CloseEditor:
                return new CloseEditor(ReadInstanceId(ref r));
            case MessageType.SetEditorVisible:
                return new SetEditorVisible(ReadInstanceId(ref r), r.ReadBool());
            case MessageType.SetEditorKeyboard:
                return new SetEditorKeyboard(ReadInstanceId(ref r), r.ReadBool());
            case MessageType.PluginNotification:
                return ReadNotification(ref r);
            default:
                throw new ProtocolException($"Unknown message type {(ushort)type}.");
        }
    }

    private static RenderEvents ReadRenderEvents(ref WireReader r)
    {
        var plugin = ReadIdentity(ref r);
        var state = ReadOptionalState(ref r);
        var parameterCount = r.ReadCount(ProtocolLimits.MaxParameters, minElementBytes: 12);
        var parameters = ImmutableArray.CreateBuilder<ParameterValue>(parameterCount);
        for (var i = 0; i < parameterCount; i++)
        {
            parameters.Add(new ParameterValue(r.ReadUInt32(), r.ReadNormalized("Parameter value")));
        }

        var sampleRate = r.ReadDouble();
        if (!(sampleRate is >= ProtocolLimits.MinSampleRate and <= ProtocolLimits.MaxSampleRate))
        {
            throw new ProtocolException($"Sample rate {sampleRate} is out of range.");
        }

        var blockFrames = r.ReadInt32InRange(1, ProtocolLimits.MaxBlockFrames, "Block size");
        var endFrame = ReadFrame(ref r, "End frame");
        var tempoCount = r.ReadCount(ProtocolLimits.MaxRenderTempoChanges, minElementBytes: 16);
        var tempo = ImmutableArray.CreateBuilder<TimelineTempo>(tempoCount);
        for (var i = 0; i < tempoCount; i++)
        {
            var frame = ReadFrame(ref r, "Tempo frame");
            var bpm = r.ReadDouble();
            if (!(bpm is > 0 and <= 10_000))
            {
                throw new ProtocolException($"Tempo {bpm} is out of range.");
            }

            tempo.Add(new TimelineTempo(frame, bpm));
        }

        var events = ReadTimelineEvents(ref r);
        var changeCount = r.ReadCount(ProtocolLimits.MaxRenderParameterChanges, minElementBytes: 20);
        var changes = ImmutableArray.CreateBuilder<TimelineParameterChange>(changeCount);
        for (var i = 0; i < changeCount; i++)
        {
            changes.Add(new TimelineParameterChange(ReadFrame(ref r, "Change frame"), r.ReadUInt32(), r.ReadNormalized("Parameter value")));
        }

        var modulePath = r.ReadOptionalString();
        return new RenderEvents(plugin, state, parameters.MoveToImmutable(), sampleRate, blockFrames, endFrame, tempo.MoveToImmutable(), events, changes.MoveToImmutable(), modulePath);
    }

    private static PluginNotification ReadNotification(ref WireReader r)
    {
        var id = ReadInstanceId(ref r);
        var kind = (NotificationKind)r.ReadByte();
        if (!Enum.IsDefined(kind))
        {
            throw new ProtocolException($"Unknown notification kind {(byte)kind}.");
        }

        var parameterId = r.ReadUInt32();
        var value = r.ReadDouble();
        var valid = kind switch
        {
            NotificationKind.ParameterEdited => NormalizedValue.IsValid(value),
            NotificationKind.LatencyChanged => parameterId == 0 && value is >= 0 and <= 1 << 24 && value == Math.Floor(value),
            NotificationKind.EditorKey => EditorKey.IsValid(parameterId, value),
            _ => parameterId == 0 && value == 0,
        };
        return valid ? new PluginNotification(id, kind, parameterId, value) : throw new ProtocolException($"Invalid {kind} notification.");
    }

    private static long ReadFrame(ref WireReader r, string what)
    {
        var frame = r.ReadInt64();
        return frame is >= 0 and <= ProtocolLimits.MaxRenderFrames ? frame : throw new ProtocolException($"{what} {frame} is out of range.");
    }

    // Compact form: frame (int64), kind, channel, data 1, data 2; system exclusive adds a uint16 length and the bytes.
    private static void WriteTimelineEvents(WireWriter w, ImmutableArray<TimelineEvent> events)
    {
        w.WriteUInt32((uint)events.Length);
        foreach (var e in events)
        {
            w.WriteInt64(e.Frame);
            w.WriteByte((byte)e.Event.Kind);
            w.WriteByte(e.Event.Channel);
            w.WriteByte(e.Event.Data1);
            w.WriteByte(e.Event.Data2);
            if (e.Event.Kind == PluginEventKind.SystemExclusive)
            {
                var data = e.Event.SystemExclusiveData;
                w.WriteUInt16((ushort)data.Length);
                foreach (var b in data)
                {
                    w.WriteByte(b);
                }
            }
        }
    }

    private static ImmutableArray<TimelineEvent> ReadTimelineEvents(ref WireReader r)
    {
        var count = r.ReadCount(ProtocolLimits.MaxRenderEvents, minElementBytes: 12);
        var events = ImmutableArray.CreateBuilder<TimelineEvent>(count);
        Span<byte> record = stackalloc byte[PluginEvent.RecordBytes];
        for (var i = 0; i < count; i++)
        {
            var frame = ReadFrame(ref r, "Event frame");
            record.Clear();
            record[4] = r.ReadByte();
            record[5] = r.ReadByte();
            record[6] = r.ReadByte();
            record[7] = r.ReadByte();
            if ((PluginEventKind)record[4] == PluginEventKind.SystemExclusive)
            {
                var length = r.ReadUInt16();
                if (length > PluginEvent.MaxSystemExclusiveBytes || length > r.Remaining)
                {
                    throw new ProtocolException($"System exclusive length {length} is out of range.");
                }

                BinaryPrimitives.WriteUInt16LittleEndian(record[8..], length);
                for (var b = 0; b < length; b++)
                {
                    record[16 + b] = r.ReadByte();
                }
            }

            var e = default(PluginEvent);
            if (!PluginEvent.TryReadFrom(record, 1, ref e))
            {
                throw new ProtocolException("A timeline event is out of contract.");
            }

            events.Add(new TimelineEvent(frame, e));
        }

        return events.MoveToImmutable();
    }

    private static PluginInstanceId ReadInstanceId(ref WireReader r)
    {
        if (r.Remaining < GuidBytes)
        {
            throw new ProtocolException("Truncated instance id.");
        }

        var value = r.ReadGuid();
        return value == Guid.Empty ? throw new ProtocolException("The instance id is empty.") : new PluginInstanceId(value);
    }

    private static uint ReadGeneration(ref WireReader r)
    {
        var generation = r.ReadUInt32();
        return generation == 0 ? throw new ProtocolException("Generation 0 is reserved.") : generation;
    }

    private static void WriteIdentity(WireWriter w, PluginIdentity identity)
    {
        w.WriteString(identity.Format);
        w.WriteString(identity.ModuleId);
        w.WriteString(identity.PluginId);
        w.WriteString(identity.DisplayName);
        w.WriteString(identity.Vendor);
        w.WriteByte((byte)identity.Kind);
        w.WriteString(identity.Version);
    }

    private static PluginIdentity ReadIdentity(ref WireReader r)
    {
        var format = r.ReadString();
        var module = r.ReadString();
        var plugin = r.ReadString();
        var name = r.ReadString();
        var vendor = r.ReadString();
        var kind = (PluginKind)r.ReadByte();
        var version = r.ReadString();
        return new PluginIdentity(format, module, plugin, name, vendor, kind, version);
    }

    private static void WriteExchangeOptions(WireWriter w, ExchangeOptions options)
    {
        w.WriteInt32(options.MaxFrames);
        w.WriteInt32(options.InputChannels);
        w.WriteInt32(options.OutputChannels);
        w.WriteInt32(options.SlotCount);
        w.WriteInt32(options.EventCapacity);
        w.WriteInt32(options.ParameterChangeCapacity);
    }

    private static ExchangeOptions ReadExchangeOptions(ref WireReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    private static void WriteState(WireWriter w, PluginStateData state)
    {
        w.WriteString(state.Format);
        w.WriteBytes(state.Data);
    }

    private static PluginStateData ReadState(ref WireReader r)
    {
        var format = r.ReadString();
        return new PluginStateData(format, r.ReadBytes(ProtocolLimits.MaxStateBytes));
    }

    private static void WriteOptionalState(WireWriter w, PluginStateData? state)
    {
        w.WriteBool(state is not null);
        if (state is not null)
        {
            WriteState(w, state);
        }
    }

    private static PluginStateData? ReadOptionalState(ref WireReader r) => r.ReadBool() ? ReadState(ref r) : null;
}
