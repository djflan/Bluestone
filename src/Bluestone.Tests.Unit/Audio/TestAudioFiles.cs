using System.Buffers.Binary;
using System.Text;

namespace Bluestone.Tests.Unit.Audio;

/// <summary>Writes small WAVE and AIFF files for decoder tests, independently of the reader under test.</summary>
internal static class TestAudioFiles
{
    public static string Directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bluestone-audio-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    // A WAVE file of interleaved samples (±1 full scale). Formats: "pcm8", "pcm16", "pcm24", "pcm32", "float32", "float64",
    // "ext16", and "ext24in32" (WAVE_FORMAT_EXTENSIBLE, 24 valid bits in 4 bytes).
    public static string Wave(string directory, string name, string format, int rate, int channels, double[] samples)
    {
        var (bytes, bits, tag, extensible) = format switch
        {
            "pcm8" => (1, 8, 1, false),
            "pcm16" => (2, 16, 1, false),
            "pcm24" => (3, 24, 1, false),
            "pcm32" => (4, 32, 1, false),
            "float32" => (4, 32, 3, false),
            "float64" => (8, 64, 3, false),
            "ext24in32" => (4, 24, 1, true),
            "ext16" => (2, 16, 1, true),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var data = Encode(samples, bytes, tag == 3, bigEndian: false, unsigned8: true, justify24: format == "ext24in32");
        writer.Write("RIFF"u8);
        writer.Write(0);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(extensible ? 40 : 16);
        writer.Write((ushort)(extensible ? 0xFFFE : tag));
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * bytes);
        writer.Write((ushort)(channels * bytes));
        writer.Write((ushort)(bytes * 8));
        if (extensible)
        {
            writer.Write((ushort)22);
            writer.Write((ushort)bits);
            writer.Write(channels == 2 ? 3 : 4);
            writer.Write((ushort)tag);
            writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
        }

        // An unknown chunk with an odd size, to check skipping and padding.
        writer.Write("junk"u8);
        writer.Write(3);
        writer.Write(new byte[] { 1, 2, 3, 0 });
        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();
        var file = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), file.Length - 8);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    // An AIFF file (compression null) or AIFF-C ("NONE", "sowt", "fl32", "fl64").
    public static string Aiff(string directory, string name, string? compression, int bits, int rate, int channels, double[] samples)
    {
        var isFloat = compression is "fl32" or "fl64";
        var bytes = compression == "fl64" ? 8 : isFloat ? 4 : (bits + 7) / 8;
        var data = Encode(samples, bytes, isFloat, bigEndian: compression != "sowt", unsigned8: false, justify24: false);
        using var body = new MemoryStream();
        using var writer = new BinaryWriter(body);
        writer.Write(compression is null ? "AIFF"u8 : "AIFC"u8);
        if (compression is not null)
        {
            Chunk(writer, "FVER", [0xA2, 0x80, 0x51, 0x40]);
        }

        var comm = new List<byte>();
        comm.AddRange(BigEndian16((short)channels));
        comm.AddRange(BigEndian32((uint)(samples.Length / channels)));
        comm.AddRange(BigEndian16((short)(isFloat ? bytes * 8 : bits)));
        comm.AddRange(Extended(rate));
        if (compression is not null)
        {
            comm.AddRange(Encoding.ASCII.GetBytes(compression));
            comm.AddRange([0, 0]); // empty Pascal string, padded
        }

        Chunk(writer, "COMM", [.. comm]);
        var ssnd = new byte[8 + data.Length];
        data.CopyTo(ssnd, 8);
        Chunk(writer, "SSND", ssnd);
        writer.Flush();
        var form = body.ToArray();
        var file = new byte[8 + form.Length];
        "FORM"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), (uint)form.Length);
        form.CopyTo(file, 8);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, file);
        return path;
    }

    public static double[] Sine(int frames, int channels, double frequency, int rate, double amplitude = 0.5)
    {
        var samples = new double[frames * channels];
        for (var n = 0; n < frames; n++)
        {
            for (var c = 0; c < channels; c++)
            {
                // Each channel gets its own phase so channel order is checked too.
                samples[(n * channels) + c] = amplitude * Math.Sin((2 * Math.PI * frequency * n / rate) + c);
            }
        }

        return samples;
    }

    private static void Chunk(BinaryWriter writer, string id, byte[] body)
    {
        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write(BigEndian32((uint)body.Length));
        writer.Write(body);
        if (body.Length % 2 == 1)
        {
            writer.Write((byte)0);
        }
    }

    private static byte[] Encode(double[] samples, int bytes, bool isFloat, bool bigEndian, bool unsigned8, bool justify24)
    {
        var data = new byte[samples.Length * bytes];
        for (var i = 0; i < samples.Length; i++)
        {
            var at = data.AsSpan(i * bytes, bytes);
            var s = samples[i];
            if (isFloat && bytes == 4)
            {
                if (bigEndian)
                {
                    BinaryPrimitives.WriteSingleBigEndian(at, (float)s);
                }
                else
                {
                    BinaryPrimitives.WriteSingleLittleEndian(at, (float)s);
                }

                continue;
            }

            if (isFloat)
            {
                if (bigEndian)
                {
                    BinaryPrimitives.WriteDoubleBigEndian(at, s);
                }
                else
                {
                    BinaryPrimitives.WriteDoubleLittleEndian(at, s);
                }

                continue;
            }

            // Integers: scale to the significant bits, then left-justify in the container.
            var significant = justify24 ? 24 : bytes * 8;
            var value = (long)Math.Round(Math.Clamp(s, -1, 1 - Math.Pow(2, 1 - significant)) * Math.Pow(2, significant - 1));
            value <<= (bytes * 8) - significant;
            if (bytes == 1 && unsigned8)
            {
                value += 128;
            }

            for (var b = 0; b < bytes; b++)
            {
                var shift = 8 * (bigEndian ? bytes - 1 - b : b);
                at[b] = (byte)(value >> shift);
            }
        }

        return data;
    }

    private static byte[] BigEndian16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] BigEndian32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    // An 80-bit extended float holding a positive integer.
    private static byte[] Extended(int value)
    {
        var bytes = new byte[10];
        var exponent = 16383 + 63;
        var mantissa = (ulong)value;
        while ((mantissa & 0x8000_0000_0000_0000) == 0)
        {
            mantissa <<= 1;
            exponent--;
        }

        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)exponent);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), mantissa);
        return bytes;
    }
}
