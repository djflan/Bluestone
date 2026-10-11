using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Bluestone.Midi.Files;

public sealed record SmfWriteOptions
{
    public static readonly SmfWriteOptions Default = new();

    /// <summary>Omit repeated channel status bytes. Meta and SysEx events always reset running status.</summary>
    public bool UseRunningStatus { get; init; } = true;
}

/// <summary>Writes an <see cref="SmfFile"/> as a Standard MIDI File.</summary>
public static class SmfWriter
{
    private const long MaxVariableLength = 0x0FFFFFFF;

    public static byte[] Write(SmfFile file, SmfWriteOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Write(file, buffer, options);
        return buffer.WrittenSpan.ToArray();
    }

    public static void Write(SmfFile file, Stream stream, SmfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new ArrayBufferWriter<byte>();
        Write(file, buffer, options);
        stream.Write(buffer.WrittenSpan);
    }

    /// <exception cref="ArgumentException">The file cannot be represented (for example, too many tracks).</exception>
    public static void Write(SmfFile file, IBufferWriter<byte> output, SmfWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(output);
        options ??= SmfWriteOptions.Default;

        var trackCount = file.Tracks.Count();
        if (trackCount > ushort.MaxValue)
        {
            throw new ArgumentException("A MIDI file can hold at most 65535 tracks.", nameof(file));
        }

        if (file.Format == SmfFormat.SingleTrack && trackCount != 1)
        {
            throw new ArgumentException("A format 0 file must contain exactly one track.", nameof(file));
        }

        WriteAscii(output, "MThd");
        WriteUInt32(output, 6);
        WriteUInt16(output, (ushort)file.Format);
        WriteUInt16(output, (ushort)trackCount);
        WriteUInt16(output, EncodeDivision(file.Division));

        var body = new ArrayBufferWriter<byte>();
        foreach (var chunk in file.Chunks)
        {
            body.ResetWrittenCount();
            string type;
            switch (chunk)
            {
                case SmfTrack track:
                    type = "MTrk";
                    WriteTrack(track, body, options);
                    break;
                case SmfUnknownChunk unknown:
                    type = unknown.Type.Length == 4 && Ascii.IsValid(unknown.Type)
                        ? unknown.Type
                        : throw new ArgumentException("Chunk types must be four ASCII characters.", nameof(file));
                    body.Write(unknown.Data.Span);
                    break;
                default:
                    throw new ArgumentException($"Unsupported chunk type {chunk.GetType().Name}.", nameof(file));
            }

            WriteAscii(output, type);
            WriteUInt32(output, (uint)body.WrittenCount);
            output.Write(body.WrittenSpan);
        }
    }

    private static void WriteTrack(SmfTrack track, ArrayBufferWriter<byte> output, SmfWriteOptions options)
    {
        long tick = 0;
        byte runningStatus = 0;
        Span<byte> message = stackalloc byte[3];
        foreach (var e in track.Events)
        {
            WriteDelta(output, e.Tick - tick);
            tick = e.Tick;
            switch (e)
            {
                case SmfChannelEvent channel:
                    var length = channel.Message.CopyTo(message);
                    var skipStatus = options.UseRunningStatus && message[0] == runningStatus;
                    output.Write(skipStatus ? message[1..length] : message[..length]);
                    runningStatus = message[0];
                    break;
                case SmfSysExEvent sysEx:
                    WriteByte(output, 0xF0);
                    WritePayload(output, sysEx.Data.Span);
                    runningStatus = 0;
                    break;
                case SmfEscapeEvent escape:
                    WriteByte(output, 0xF7);
                    WritePayload(output, escape.Data.Span);
                    runningStatus = 0;
                    break;
                case SmfMetaEvent meta:
                    WriteMeta(output, meta.Type, meta.Data.Span);
                    runningStatus = 0;
                    break;
                default:
                    throw new ArgumentException($"Unsupported event type {e.GetType().Name}.", nameof(track));
            }
        }

        WriteDelta(output, track.EndTick - tick);
        WriteMeta(output, SmfMetaType.EndOfTrack, []);
    }

    private static void WriteMeta(IBufferWriter<byte> output, byte type, ReadOnlySpan<byte> data)
    {
        WriteByte(output, 0xFF);
        WriteByte(output, type);
        WritePayload(output, data);
    }

    private static void WritePayload(IBufferWriter<byte> output, ReadOnlySpan<byte> data)
    {
        WriteVariableLength(output, data.Length);
        output.Write(data);
    }

    private static ushort EncodeDivision(SmfDivision division) =>
        division.IsSmpte
            ? (ushort)(((byte)(sbyte)-division.FramesPerSecond << 8) | division.TicksPerFrame)
            : (ushort)division.TicksPerQuarterNote;

    /// <summary>
    /// Writes a delta time. A delta beyond the 28-bit variable-length limit is split by inserting empty
    /// text meta events as spacers, which preserves timing.
    /// </summary>
    private static void WriteDelta(IBufferWriter<byte> output, long delta)
    {
        while (delta > MaxVariableLength)
        {
            WriteVariableLength(output, MaxVariableLength);
            WriteMeta(output, SmfMetaType.Text, []);
            delta -= MaxVariableLength;
        }

        WriteVariableLength(output, delta);
    }

    internal static void WriteVariableLength(IBufferWriter<byte> output, long value)
    {
        if (value is < 0 or > MaxVariableLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Variable-length quantities are limited to 28 bits.");
        }

        Span<byte> bytes = stackalloc byte[4];
        var count = 0;
        do
        {
            bytes[count++] = (byte)(value & 0x7F);
            value >>= 7;
        }
        while (value > 0);

        var span = output.GetSpan(count);
        for (var i = 0; i < count; i++)
        {
            span[i] = (byte)(bytes[count - 1 - i] | (i < count - 1 ? 0x80 : 0));
        }

        output.Advance(count);
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private static void WriteUInt16(IBufferWriter<byte> output, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(output.GetSpan(2), value);
        output.Advance(2);
    }

    private static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(output.GetSpan(4), value);
        output.Advance(4);
    }

    private static void WriteAscii(IBufferWriter<byte> output, string text)
    {
        var count = Encoding.ASCII.GetBytes(text, output.GetSpan(text.Length));
        output.Advance(count);
    }
}
