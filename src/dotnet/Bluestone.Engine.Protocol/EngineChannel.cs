using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bluestone.Engine.Protocol;

/// <summary>The protocol's version and limits.</summary>
public static class EngineLimits
{
    /// <summary>Bumped on any incompatible change; both sides must speak the same version.</summary>
    public const int Version = 2;

    /// <summary>The largest frame body either side accepts.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>The longest string (UTF-8 bytes) in a message.</summary>
    public const int MaxStringBytes = 4096;

    /// <summary>The most output slots a plan may use.</summary>
    public const int MaxSlots = 1024;

    /// <summary>The most inputs the engine listens to at once.</summary>
    public const int MaxInputs = 256;

    /// <summary>The most stages in one live route, and parameters in one stage.</summary>
    public const int MaxRouteStages = 64;

    public const int MaxStageParameters = 256;

    /// <summary>The most clicks in a count-in.</summary>
    public const int MaxCountInClicks = 1024;

    /// <summary>The longest single MIDI message carried (a SysEx dump; anything longer is not sent).</summary>
    public const int MaxMidiMessageBytes = 16 * 1024;

    /// <summary>The most events in one <see cref="MidiBatch"/>.</summary>
    public const int MaxBatchEvents = 4096;

    /// <summary>The most buckets in a timing histogram.</summary>
    public const int MaxHistogramBuckets = 4096;

    /// <summary>How often a running engine sends a heartbeat.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>An engine that sends nothing for this long has stopped (ADR 0044).</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(1);

    /// <summary>A fresh random token for one engine launch.</summary>
    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>Whether <paramref name="presented"/> is <paramref name="expected"/>, in constant time.</summary>
    public static bool TokensMatch(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}

/// <summary>Something on the control channel broke the protocol. The connection is not usable afterwards.</summary>
public sealed class EngineProtocolException : Exception
{
    public EngineProtocolException()
        : base("The engine protocol was broken.")
    {
    }

    public EngineProtocolException(string message)
        : base(message)
    {
    }

    public EngineProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Reads and writes control-channel frames: a little-endian uint32 body length, then the body (a uint16 message type
/// and the message's fields). Decoding is strict: a malformed, truncated, oversized, or unknown frame, or one with bytes
/// left over, is an <see cref="EngineProtocolException"/>. One reader and one writer per stream; each may be used from
/// one thread at a time, and reuses its buffer. Disposing does not close the stream.
/// </summary>
public sealed partial class EngineChannel(Stream stream) : IDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly byte[] _readBuffer = new byte[4 + EngineLimits.MaxFrameBytes];
    private readonly byte[] _writeBuffer = new byte[4 + EngineLimits.MaxFrameBytes];
    private readonly SemaphoreSlim _writing = new(1, 1);

    public void Dispose() => _writing.Dispose();

    /// <summary>Writes one message. Safe from several threads: writes are serialized.</summary>
    public async Task WriteAsync(EngineMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var length = Encode(message, _writeBuffer.AsSpan(4));
            BinaryPrimitives.WriteUInt32LittleEndian(_writeBuffer, (uint)length);
            await _stream.WriteAsync(_writeBuffer.AsMemory(0, 4 + length), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>The next message, or null when the other side closed the stream between messages.</summary>
    public async Task<EngineMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!await FillAsync(_readBuffer.AsMemory(0, 4), allowEnd: true, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(_readBuffer);
        if (length is < 2 or > EngineLimits.MaxFrameBytes)
        {
            throw new EngineProtocolException($"A frame of {length} bytes is outside the protocol's limits.");
        }

        await FillAsync(_readBuffer.AsMemory(4, (int)length), allowEnd: false, cancellationToken).ConfigureAwait(false);
        return Decode(_readBuffer.AsSpan(4, (int)length));
    }

    /// <summary>Encodes <paramref name="message"/>'s body into <paramref name="body"/>; returns its length.</summary>
    internal static int Encode(EngineMessage message, Span<byte> body)
    {
        var writer = new Writer(body);
        writer.UInt16((ushort)message.Type);
        switch (message)
        {
            case Hello hello:
                writer.Int32(hello.Version);
                writer.String(hello.Token);
                writer.Int32(hello.ProcessId);
                writer.Bool(hello.Restarted);
                break;
            case Welcome welcome:
                writer.Int32(welcome.Version);
                break;
            case Ready or Shutdown:
                break;
            case Heartbeat heartbeat:
                writer.Int64(heartbeat.Sequence);
                writer.Int32(heartbeat.Telemetry.Collections);
                writer.Int64(heartbeat.Telemetry.PauseTotal.Ticks);
                writer.Int64(heartbeat.Telemetry.AllocatedBytes);
                writer.Int64(heartbeat.Telemetry.WorkingSetBytes);
                writer.Int64(heartbeat.Telemetry.JitCompiledMethods);
                break;
            case Ping ping:
                writer.Int64(ping.Id);
                break;
            case Pong pong:
                writer.Int64(pong.Id);
                break;
            case Fault fault:
                writer.String(fault.Message);
                break;
            case TestCommand test:
                writer.Byte((byte)test.Action);
                break;
            default:
                if (EncodeMidi(message, ref writer))
                {
                    break;
                }

                throw new ArgumentException($"Cannot encode {message.GetType().Name}.", nameof(message));
        }

        return writer.Position;
    }

    /// <summary>Decodes one frame body, consuming all of it.</summary>
    internal static EngineMessage Decode(ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body);
        var type = (EngineMessageType)reader.UInt16();
        EngineMessage message = type switch
        {
            EngineMessageType.Hello => new Hello(reader.Int32(), reader.String(), reader.Int32(), reader.Bool()),
            EngineMessageType.Welcome => new Welcome(reader.Int32()),
            EngineMessageType.Ready => new Ready(),
            EngineMessageType.Heartbeat => new Heartbeat(reader.Int64(), new EngineTelemetry(reader.Int32(), TimeSpan.FromTicks(reader.NonNegative()), reader.NonNegative(), reader.NonNegative(), reader.NonNegative())),
            EngineMessageType.Ping => new Ping(reader.Int64()),
            EngineMessageType.Pong => new Pong(reader.Int64()),
            EngineMessageType.Shutdown => new Shutdown(),
            EngineMessageType.Fault => new Fault(reader.String()),
            EngineMessageType.TestCommand => new TestCommand(reader.Byte() switch
            {
                (byte)TestAction.Crash => TestAction.Crash,
                (byte)TestAction.Hang => TestAction.Hang,
                (byte)TestAction.Fault => TestAction.Fault,
                var other => throw new EngineProtocolException($"Unknown test action {other}."),
            }),
            _ => DecodeMidi(type, ref reader),
        };
        if (reader.Remaining != 0)
        {
            throw new EngineProtocolException($"{reader.Remaining} bytes left over after {type}.");
        }

        return message;
    }

    private async Task<bool> FillAsync(Memory<byte> buffer, bool allowEnd, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (allowEnd && read == 0)
                {
                    return false;
                }

                throw new EngineProtocolException("The control channel closed in the middle of a frame.");
            }

            read += count;
        }

        return true;
    }

    private ref struct Writer(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        public int Position { get; private set; }

        public void Byte(byte value) => Take(1)[0] = value;

        public void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

        public void UInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

        public void Int32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

        public void Int64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);

        public void UInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

        public void Guid(Guid value)
        {
            if (!value.TryWriteBytes(Take(16)))
            {
                throw new ArgumentException("A GUID did not fit.");
            }
        }

        public void Bytes(ReadOnlySpan<byte> value)
        {
            Int32(value.Length);
            value.CopyTo(Take(value.Length));
        }

        public void String(string value)
        {
            var length = Encoding.UTF8.GetByteCount(value);
            if (length > EngineLimits.MaxStringBytes)
            {
                throw new ArgumentException($"A string of {length} bytes is longer than the protocol allows.", nameof(value));
            }

            UInt16((ushort)length);
            Encoding.UTF8.GetBytes(value, Take(length));
        }

        private Span<byte> Take(int count)
        {
            if (Position + count > _buffer.Length)
            {
                throw new ArgumentException("The message is larger than a frame.");
            }

            var span = _buffer.Slice(Position, count);
            Position += count;
            return span;
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> buffer)
    {
        private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);
        private readonly ReadOnlySpan<byte> _buffer = buffer;
        private int _position;

        public readonly int Remaining => _buffer.Length - _position;

        public byte Byte() => Take(1)[0];

        public bool Bool() => Byte() switch
        {
            0 => false,
            1 => true,
            var other => throw new EngineProtocolException($"{other} is not a boolean."),
        };

        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public Guid Guid() => new(Take(16));

        /// <summary>A count or length from 0 to <paramref name="max"/>.</summary>
        public int Count(int max)
        {
            var value = Int32();
            return value >= 0 && value <= max ? value : throw new EngineProtocolException($"{value} is outside 0 to {max}.");
        }

        /// <summary>An int from <paramref name="min"/> to <paramref name="max"/>.</summary>
        public int Int32In(int min, int max)
        {
            var value = Int32();
            return value >= min && value <= max ? value : throw new EngineProtocolException($"{value} is outside {min} to {max}.");
        }

        /// <summary>A byte from 0 to <paramref name="max"/>.</summary>
        public byte ByteUpTo(byte max)
        {
            var value = Byte();
            return value <= max ? value : throw new EngineProtocolException($"{value} is above {max}.");
        }

        /// <summary>A copy of a length-prefixed run of bytes, from <paramref name="min"/> to <paramref name="max"/> long.</summary>
        public byte[] Bytes(int min, int max)
        {
            var length = Count(max);
            return length >= min ? Take(length).ToArray() : throw new EngineProtocolException($"{length} bytes is fewer than {min}.");
        }

        public long NonNegative()
        {
            var value = Int64();
            return value >= 0 ? value : throw new EngineProtocolException($"{value} cannot be negative.");
        }

        public string String()
        {
            var length = UInt16();
            if (length > EngineLimits.MaxStringBytes)
            {
                throw new EngineProtocolException($"A string of {length} bytes is longer than the protocol allows.");
            }

            try
            {
                return Strict.GetString(Take(length));
            }
            catch (DecoderFallbackException ex)
            {
                throw new EngineProtocolException("A string is not UTF-8.", ex);
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > Remaining)
            {
                throw new EngineProtocolException("The frame ends before its message does.");
            }

            var span = _buffer.Slice(_position, count);
            _position += count;
            return span;
        }
    }
}
