using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using Bluestone.Domain.Midi;
using Bluestone.Midi.Wire;

namespace Bluestone.Midi.Files;

/// <summary>The outcome of reading a file: its structure plus anything unusual found on the way.</summary>
public sealed record SmfReadResult(SmfFile File, IReadOnlyList<SmfDiagnostic> Diagnostics);

/// <summary>
/// Reads Standard MIDI Files defensively. Structural damage inside a track (truncation, bad bytes)
/// stops that track and is reported as a diagnostic; only an unreadable header or an exceeded limit
/// throws <see cref="SmfFormatException"/>. The source bytes are never modified.
/// </summary>
public static class SmfReader
{
    private const int ChunkHeaderLength = 8;
    private const int MaxVariableLengthBytes = 4;

    public static SmfReadResult Read(Stream stream, SmfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= SmfReadOptions.Default;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk)) > 0)
        {
            if (buffer.Length + read > options.MaxFileBytes)
            {
                throw new SmfFormatException(string.Create(CultureInfo.InvariantCulture, $"The file is larger than the {options.MaxFileBytes}-byte limit."));
            }

            buffer.Write(chunk, 0, read);
        }

        return Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), options);
    }

    /// <exception cref="SmfFormatException">The data is not a readable MIDI file or exceeds a limit.</exception>
    public static SmfReadResult Read(ReadOnlySpan<byte> data, SmfReadOptions? options = null)
    {
        options ??= SmfReadOptions.Default;
        if (data.Length > options.MaxFileBytes)
        {
            throw new SmfFormatException(string.Create(CultureInfo.InvariantCulture, $"The file is larger than the {options.MaxFileBytes}-byte limit."));
        }

        var diagnostics = new DiagnosticBag();
        if (data.Length < ChunkHeaderLength + 6 || !data.StartsWith("MThd"u8))
        {
            throw new SmfFormatException("The data does not start with a MIDI file header (MThd).");
        }

        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (headerLength < 6 || headerLength > (uint)(data.Length - ChunkHeaderLength))
        {
            throw new SmfFormatException("The MIDI file header has an invalid length.");
        }

        var header = data.Slice(ChunkHeaderLength, (int)headerLength);
        var formatValue = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (formatValue > 2)
        {
            throw new SmfFormatException(string.Create(CultureInfo.InvariantCulture, $"MIDI file format {formatValue} is not supported."));
        }

        var declaredTracks = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        var division = ReadDivision(BinaryPrimitives.ReadUInt16BigEndian(header[4..]));
        if (headerLength > 6)
        {
            diagnostics.Info(SmfDiagnosticCodes.LongHeader, "The header is longer than six bytes; the extra bytes were ignored.");
        }

        var chunks = ImmutableArray.CreateBuilder<SmfChunk>();
        var trackCount = 0;
        var eventCount = 0;
        var position = ChunkHeaderLength + (int)headerLength;
        while (position < data.Length)
        {
            var remaining = data.Length - position;
            if (remaining < ChunkHeaderLength || !IsChunkType(data.Slice(position, 4)))
            {
                diagnostics.Warn(SmfDiagnosticCodes.TrailingBytes, string.Create(CultureInfo.InvariantCulture, $"{remaining} unrecognized bytes after the last chunk were ignored."));
                break;
            }

            var type = System.Text.Encoding.ASCII.GetString(data.Slice(position, 4));
            var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            var available = remaining - ChunkHeaderLength;
            var length = declaredLength > (uint)available ? available : (int)declaredLength;
            var body = data.Slice(position + ChunkHeaderLength, length);

            if (type == "MTrk")
            {
                if (trackCount >= options.MaxTracks)
                {
                    throw new SmfFormatException(string.Create(CultureInfo.InvariantCulture, $"The file has more than {options.MaxTracks} tracks."));
                }

                if (declaredLength > (uint)available)
                {
                    diagnostics.Warn(SmfDiagnosticCodes.TruncatedChunk, "The track is shorter than its header declares; it was read up to the end of the file.", trackCount);
                }

                chunks.Add(ReadTrack(body, trackCount, options, diagnostics, ref eventCount));
                trackCount++;
            }
            else
            {
                if (declaredLength > (uint)available)
                {
                    diagnostics.Warn(SmfDiagnosticCodes.TruncatedChunk, $"Chunk '{type}' is shorter than its header declares.");
                }

                diagnostics.Info(SmfDiagnosticCodes.UnknownChunk, $"Chunk '{type}' is not a track; it was preserved but not interpreted.");
                chunks.Add(new SmfUnknownChunk(type, ByteBlock.Copy(body)));
            }

            position += ChunkHeaderLength + length;
        }

        if (trackCount != declaredTracks)
        {
            diagnostics.Warn(SmfDiagnosticCodes.TrackCountMismatch, string.Create(CultureInfo.InvariantCulture, $"The header declares {declaredTracks} tracks but {trackCount} were found."));
        }

        var format = (SmfFormat)formatValue;
        if (format == SmfFormat.SingleTrack && trackCount > 1)
        {
            diagnostics.Warn(SmfDiagnosticCodes.SingleTrackFormatWithManyTracks, "A format 0 file contains more than one track; all were read.");
        }

        return new SmfReadResult(new SmfFile(format, division, chunks.ToImmutable()), diagnostics.ToList());
    }

    private static SmfDivision ReadDivision(ushort value)
    {
        if ((value & 0x8000) == 0)
        {
            return value != 0
                ? SmfDivision.Metrical(value)
                : throw new SmfFormatException("The header declares zero ticks per quarter note.");
        }

        var frames = -(sbyte)(value >> 8);
        var ticksPerFrame = value & 0xFF;
        if (frames is not (24 or 25 or 29 or 30) || ticksPerFrame == 0)
        {
            throw new SmfFormatException("The header declares an invalid SMPTE timing division.");
        }

        return SmfDivision.Smpte(frames, ticksPerFrame);
    }

    private static SmfTrack ReadTrack(ReadOnlySpan<byte> body, int trackIndex, SmfReadOptions options, DiagnosticBag diagnostics, ref int eventCount)
    {
        var events = new List<SmfEvent>();
        long tick = 0;
        byte runningStatus = 0;
        var lastWasSystem = false;
        var index = 0;
        var endedProperly = false;

        while (index < body.Length)
        {
            if (!TryReadVariableLength(body, ref index, out var delta))
            {
                Malformed("An event's delta time is malformed or truncated.");
                break;
            }

            tick += delta;
            if (index >= body.Length)
            {
                Malformed("The track ends in the middle of an event.");
                break;
            }

            if (++eventCount > options.MaxEvents)
            {
                throw new SmfFormatException(string.Create(CultureInfo.InvariantCulture, $"The file has more than {options.MaxEvents} events."));
            }

            var status = body[index];
            if (status == 0xFF)
            {
                index++;
                if (index >= body.Length || body[index] > 0x7F)
                {
                    Malformed("A meta event has a missing or invalid type.");
                    break;
                }

                var type = body[index++];
                if (!TryReadPayload(body, ref index, options, out var payload))
                {
                    Malformed("A meta event's length is malformed or exceeds the data available.");
                    break;
                }

                if (type == SmfMetaType.EndOfTrack)
                {
                    endedProperly = true;
                    if (index < body.Length)
                    {
                        diagnostics.Warn(SmfDiagnosticCodes.DataAfterEndOfTrack, "Data after the end-of-track event was ignored.", trackIndex);
                    }

                    break;
                }

                events.Add(new SmfMetaEvent(tick, type, ByteBlock.Copy(payload)));
                lastWasSystem = true;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                index++;
                if (!TryReadPayload(body, ref index, options, out var payload))
                {
                    Malformed("A SysEx event's length is malformed or exceeds the data available.");
                    break;
                }

                events.Add(status == 0xF0
                    ? new SmfSysExEvent(tick, ByteBlock.Copy(payload))
                    : new SmfEscapeEvent(tick, ByteBlock.Copy(payload)));
                lastWasSystem = true;
                continue;
            }

            if (status > 0xF0)
            {
                Malformed(string.Create(CultureInfo.InvariantCulture, $"Status byte {status:X2} is not valid in a MIDI file."));
                break;
            }

            if (status >= 0x80)
            {
                runningStatus = status;
                index++;
            }
            else if (runningStatus == 0)
            {
                Malformed("A data byte appears with no preceding status byte.");
                break;
            }
            else if (lastWasSystem)
            {
                // Strictly, meta and SysEx events cancel running status; many writers rely on it surviving.
                diagnostics.Info(SmfDiagnosticCodes.RunningStatusAfterSystemEvent, "Running status continued across a meta or SysEx event.", trackIndex);
            }

            var dataLength = ChannelMessage.DataLength(runningStatus);
            if (index + dataLength > body.Length)
            {
                Malformed("The track ends in the middle of a channel message.");
                break;
            }

            var data1 = body[index];
            var data2 = dataLength == 2 ? body[index + 1] : (byte)0;
            if (!ChannelMessage.TryCreate(runningStatus, data1, data2, out var message))
            {
                Malformed("A channel message contains a data byte above 127.");
                break;
            }

            index += dataLength;
            events.Add(new SmfChannelEvent(tick, message));
            lastWasSystem = false;
        }

        if (!endedProperly)
        {
            diagnostics.Warn(SmfDiagnosticCodes.MissingEndOfTrack, "The track has no end-of-track event.", trackIndex);
        }

        return new SmfTrack(events, tick);

        void Malformed(string message) =>
            diagnostics.Warn(SmfDiagnosticCodes.MalformedEvent, message + " The rest of the track was skipped.", trackIndex);
    }

    private static bool TryReadPayload(ReadOnlySpan<byte> body, ref int index, SmfReadOptions options, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (!TryReadVariableLength(body, ref index, out var length) || length > options.MaxEventDataBytes || length > body.Length - index)
        {
            return false;
        }

        payload = body.Slice(index, (int)length);
        index += (int)length;
        return true;
    }

    internal static bool TryReadVariableLength(ReadOnlySpan<byte> data, ref int index, out long value)
    {
        value = 0;
        for (var i = 0; i < MaxVariableLengthBytes; i++)
        {
            if (index >= data.Length)
            {
                return false;
            }

            var b = data[index++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChunkType(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
