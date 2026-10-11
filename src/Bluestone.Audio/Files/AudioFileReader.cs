using System.Buffers.Binary;
using System.Text;

namespace Bluestone.Audio.Files;

public enum AudioContainer
{
    Wave,
    Aiff,
    AiffC,
}

public enum SampleEncoding
{
    /// <summary>Signed integers (unsigned for 8-bit WAVE), in <see cref="AudioFileFormat.BytesPerSample"/> bytes.</summary>
    Pcm,
    IeeeFloat,
}

/// <summary>What an audio file holds.</summary>
/// <param name="Container">The file type.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="Channels">Interleaved channels per frame.</param>
/// <param name="Frames">Length in frames.</param>
/// <param name="Encoding">Integer or floating point samples.</param>
/// <param name="BitsPerSample">Significant bits per sample.</param>
/// <param name="BytesPerSample">Bytes each sample occupies (a 24-bit sample may sit in 4 bytes).</param>
/// <param name="BigEndian">Byte order of the samples.</param>
public sealed record AudioFileFormat(
    AudioContainer Container,
    int SampleRate,
    int Channels,
    long Frames,
    SampleEncoding Encoding,
    int BitsPerSample,
    int BytesPerSample,
    bool BigEndian)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);
}

/// <summary>The file is not an audio file Bluestone can read: the message says why.</summary>
public sealed class AudioFileException(string message) : Exception(message);

/// <summary>
/// Reads uncompressed audio files: WAVE (PCM 8/16/24/32-bit integer, 32/64-bit float, WAVE_FORMAT_EXTENSIBLE) and
/// AIFF/AIFF-C (8/16/24/32-bit integer big-endian, <c>sowt</c> little-endian, <c>fl32</c>/<c>fl64</c> float). Any
/// channel count. Compressed formats (FLAC, MP3, Ogg, ALAC, μ-law, A-law) and RF64 are not supported. Not thread-safe:
/// one reader belongs to one thread (the engine's disk streamer).
/// </summary>
public sealed class AudioFileReader : IDisposable
{
    private const int MaxChannels = 64;
    private readonly FileStream _file;
    private readonly long _dataOffset;
    private readonly int _frameBytes;
    private byte[] _raw = [];

    private AudioFileReader(FileStream file, AudioFileFormat format, long dataOffset)
    {
        _file = file;
        Format = format;
        _dataOffset = dataOffset;
        _frameBytes = format.BytesPerSample * format.Channels;
    }

    public AudioFileFormat Format { get; }

    public string Path => _file.Name;

    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="AudioFileException">The file is not a supported audio file.</exception>
    public static AudioFileReader Open(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.RandomAccess);
        try
        {
            var (format, dataOffset) = ReadHeader(file);
            return new AudioFileReader(file, format, dataOffset);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Reads only the header.</summary>
    public static AudioFileFormat Probe(string path)
    {
        using var reader = Open(path);
        return reader.Format;
    }

    /// <summary>
    /// Reads up to <paramref name="frames"/> frames from <paramref name="frame"/> as interleaved floats (full scale ±1)
    /// into <paramref name="destination"/>, which must hold frames × channels samples.
    /// </summary>
    /// <returns>Frames read: fewer at the end of the file, 0 past it.</returns>
    public int Read(long frame, int frames, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        var channels = Format.Channels;
        if (destination.Length < frames * channels)
        {
            throw new ArgumentException("The destination must hold frames × channels samples.", nameof(destination));
        }

        var count = (int)Math.Clamp(Format.Frames - frame, 0, frames);
        if (count == 0)
        {
            return 0;
        }

        var bytes = count * _frameBytes;
        if (_raw.Length < bytes)
        {
            _raw = new byte[bytes];
        }

        _file.Position = _dataOffset + (frame * _frameBytes);
        _file.ReadExactly(_raw, 0, bytes);
        Convert(_raw.AsSpan(0, bytes), destination[..(count * channels)]);
        return count;
    }

    public void Dispose() => _file.Dispose();

    private void Convert(ReadOnlySpan<byte> raw, Span<float> output)
    {
        var size = Format.BytesPerSample;
        var big = Format.BigEndian;
        if (Format.Encoding == SampleEncoding.IeeeFloat)
        {
            for (var i = 0; i < output.Length; i++)
            {
                var at = raw.Slice(i * size, size);
                output[i] = size == 4
                    ? (big ? BinaryPrimitives.ReadSingleBigEndian(at) : BinaryPrimitives.ReadSingleLittleEndian(at))
                    : (float)(big ? BinaryPrimitives.ReadDoubleBigEndian(at) : BinaryPrimitives.ReadDoubleLittleEndian(at));
            }

            return;
        }

        switch (size)
        {
            case 1:
                // WAVE stores 8-bit samples unsigned, AIFF signed.
                var unsigned = Format.Container == AudioContainer.Wave;
                for (var i = 0; i < output.Length; i++)
                {
                    output[i] = (unsigned ? raw[i] - 128 : (sbyte)raw[i]) / 128f;
                }

                break;
            case 2:
                for (var i = 0; i < output.Length; i++)
                {
                    var at = raw.Slice(i * 2, 2);
                    output[i] = (big ? BinaryPrimitives.ReadInt16BigEndian(at) : BinaryPrimitives.ReadInt16LittleEndian(at)) / 32768f;
                }

                break;
            case 3:
                for (var i = 0; i < output.Length; i++)
                {
                    var at = raw.Slice(i * 3, 3);
                    var value = big ? (at[0] << 24) | (at[1] << 16) | (at[2] << 8) : (at[2] << 24) | (at[1] << 16) | (at[0] << 8);
                    output[i] = value / 2147483648f;
                }

                break;
            default:
                for (var i = 0; i < output.Length; i++)
                {
                    var at = raw.Slice(i * 4, 4);
                    output[i] = (big ? BinaryPrimitives.ReadInt32BigEndian(at) : BinaryPrimitives.ReadInt32LittleEndian(at)) / 2147483648f;
                }

                break;
        }
    }

    private static (AudioFileFormat Format, long DataOffset) ReadHeader(FileStream file)
    {
        Span<byte> head = stackalloc byte[12];
        if (file.Read(head) < 12)
        {
            throw new AudioFileException("The file is too short to be an audio file.");
        }

        var id = Encoding.ASCII.GetString(head[..4]);
        var type = Encoding.ASCII.GetString(head[8..12]);
        return (id, type) switch
        {
            ("RIFF", "WAVE") => ReadWave(file),
            ("FORM", "AIFF") => ReadAiff(file, compressed: false),
            ("FORM", "AIFC") => ReadAiff(file, compressed: true),
            ("RF64", _) or ("BW64", _) => throw new AudioFileException("RF64 (WAVE files over 4 GB) is not supported."),
            ("fLaC", _) => throw new AudioFileException("FLAC files are not supported; convert to WAVE or AIFF."),
            ("OggS", _) => throw new AudioFileException("Ogg files are not supported; convert to WAVE or AIFF."),
            _ when head[0] == 0xFF || id.StartsWith("ID3", StringComparison.Ordinal) => throw new AudioFileException("MP3 files are not supported; convert to WAVE or AIFF."),
            _ => throw new AudioFileException("Not a WAVE or AIFF file."),
        };
    }

    private static (AudioFileFormat, long) ReadWave(FileStream file)
    {
        AudioFileFormat? format = null;
        while (ReadChunk(file, bigEndian: false) is var (id, size, start))
        {
            if (id == "fmt ")
            {
                var body = ReadBody(file, size, 16, 64);
                int tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                var rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(body[12..]);
                int bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (tag == 0xFFFE)
                {
                    if (body.Length < 40)
                    {
                        throw new AudioFileException("The WAVE_FORMAT_EXTENSIBLE header is truncated.");
                    }

                    var valid = BinaryPrimitives.ReadUInt16LittleEndian(body[18..]);
                    bits = valid is > 0 ? valid : bits;
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);
                }

                var encoding = tag switch
                {
                    1 => SampleEncoding.Pcm,
                    3 => SampleEncoding.IeeeFloat,
                    _ => throw new AudioFileException($"WAVE format tag 0x{tag:X4} is compressed or unknown; only PCM and IEEE float are supported."),
                };
                var bytesPerSample = channels == 0 ? 0 : blockAlign / channels;
                format = Checked(new AudioFileFormat(AudioContainer.Wave, rate, channels, 0, encoding, bits, bytesPerSample, BigEndian: false));
            }
            else if (id == "data")
            {
                if (format is null)
                {
                    throw new AudioFileException("The WAVE data chunk comes before its format chunk.");
                }

                // A size beyond the file (streams written without a final size) is cut to what is there.
                var available = Math.Min(size, file.Length - start);
                return (format with { Frames = available / (format.BytesPerSample * format.Channels) }, start);
            }

            Skip(file, start, size);
        }

        throw new AudioFileException(format is null ? "The WAVE file has no format chunk." : "The WAVE file has no data chunk.");
    }

    private static (AudioFileFormat, long) ReadAiff(FileStream file, bool compressed)
    {
        AudioFileFormat? format = null;
        long frames = 0;
        long? dataOffset = null;
        while (ReadChunk(file, bigEndian: true) is var (id, size, start))
        {
            if (id == "COMM")
            {
                var body = ReadBody(file, size, 18, 1024);
                var channels = BinaryPrimitives.ReadInt16BigEndian(body);
                frames = BinaryPrimitives.ReadUInt32BigEndian(body[2..]);
                var bits = BinaryPrimitives.ReadInt16BigEndian(body[6..]);
                var rate = ExtendedToDouble(body.Slice(8, 10));
                var compression = compressed && body.Length >= 22 ? Encoding.ASCII.GetString(body.Slice(18, 4)) : "NONE";
                var (encoding, bigEndian, bytes) = compression switch
                {
                    "NONE" or "twos" => (SampleEncoding.Pcm, true, (bits + 7) / 8),
                    "sowt" => (SampleEncoding.Pcm, false, (bits + 7) / 8),
                    "fl32" or "FL32" => (SampleEncoding.IeeeFloat, true, 4),
                    "fl64" or "FL64" => (SampleEncoding.IeeeFloat, true, 8),
                    _ => throw new AudioFileException($"AIFF-C compression '{compression}' is not supported."),
                };
                if (rate is < 1000 or > 768_000 || double.IsNaN(rate))
                {
                    throw new AudioFileException($"The sample rate {rate} is not supported.");
                }

                format = Checked(new AudioFileFormat(compressed ? AudioContainer.AiffC : AudioContainer.Aiff, (int)Math.Round(rate), channels, 0, encoding, encoding == SampleEncoding.IeeeFloat ? bytes * 8 : bits, bytes, bigEndian));
            }
            else if (id == "SSND")
            {
                var body = ReadBody(file, size, 8, 8);
                dataOffset = start + 8 + BinaryPrimitives.ReadUInt32BigEndian(body);
            }

            Skip(file, start, size);
        }

        if (format is null || dataOffset is not { } offset)
        {
            throw new AudioFileException(format is null ? "The AIFF file has no COMM chunk." : "The AIFF file has no SSND chunk.");
        }

        var available = Math.Max(0, (file.Length - offset) / (format.BytesPerSample * format.Channels));
        return (format with { Frames = Math.Min(frames, available) }, offset);
    }

    private static AudioFileFormat Checked(AudioFileFormat format)
    {
        if (format.Channels is < 1 or > MaxChannels)
        {
            throw new AudioFileException($"{format.Channels} channels are not supported (1 to {MaxChannels}).");
        }

        if (format.SampleRate is < 1000 or > 768_000)
        {
            throw new AudioFileException($"The sample rate {format.SampleRate} Hz is not supported.");
        }

        var valid = format.Encoding == SampleEncoding.IeeeFloat
            ? format.BytesPerSample is 4 or 8
            : format.BytesPerSample is >= 1 and <= 4 && format.BitsPerSample <= format.BytesPerSample * 8 && format.BitsPerSample > 0;
        return valid ? format : throw new AudioFileException($"{format.BitsPerSample}-bit {format.Encoding} samples in {format.BytesPerSample} bytes are not supported.");
    }

    // The next chunk's id, body size, and body offset; null at the end of the file.
    private static (string Id, long Size, long Start)? ReadChunk(FileStream file, bool bigEndian)
    {
        Span<byte> header = stackalloc byte[8];
        if (file.Read(header) < 8)
        {
            return null;
        }

        var size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(header[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        return (Encoding.ASCII.GetString(header[..4]), size, file.Position);
    }

    private static Span<byte> ReadBody(FileStream file, long size, int minimum, int maximum)
    {
        if (size < minimum)
        {
            throw new AudioFileException("A header chunk is truncated.");
        }

        var body = new byte[Math.Min(size, maximum)];
        if (file.Read(body) < body.Length)
        {
            throw new AudioFileException("A header chunk is truncated.");
        }

        return body;
    }

    // Chunks are padded to an even length in both formats.
    private static void Skip(FileStream file, long start, long size) => file.Position = start + size + (size & 1);

    // An IEEE 754 80-bit extended float (AIFF's sample rate): sign and 15-bit exponent, then a 64-bit mantissa with an explicit integer bit.
    internal static double ExtendedToDouble(ReadOnlySpan<byte> bytes)
    {
        var exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        var mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0 && mantissa == 0)
        {
            return 0;
        }

        var value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (bytes[0] & 0x80) != 0 ? -value : value;
    }
}
