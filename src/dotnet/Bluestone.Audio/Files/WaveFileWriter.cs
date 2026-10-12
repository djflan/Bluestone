using System.Buffers.Binary;

namespace Bluestone.Audio.Files;

/// <summary>
/// Writes a WAVE file of 32-bit float samples (format tag 3), which <see cref="AudioFileReader"/> reads back exactly.
/// Frames are appended in blocks; the header's sizes are written on <see cref="Dispose"/>. Not for the audio thread.
/// </summary>
public sealed class WaveFileWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private readonly FileStream _file;
    private readonly int _channels;
    private byte[] _buffer = [];
    private long _frames;

    /// <exception cref="ArgumentOutOfRangeException">The rate or channel count is out of range.</exception>
    public WaveFileWriter(string path, int sampleRate, int channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 64);
        SampleRate = sampleRate;
        _channels = channels;
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _file.Write(new byte[HeaderBytes]);
    }

    public int SampleRate { get; }

    /// <summary>Appends interleaved frames (frames × channels samples).</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length % _channels != 0)
        {
            throw new ArgumentException("Samples must be whole frames.", nameof(interleaved));
        }

        var bytes = interleaved.Length * 4;
        if (_buffer.Length < bytes)
        {
            _buffer = new byte[bytes];
        }

        for (var i = 0; i < interleaved.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(i * 4), interleaved[i]);
        }

        _file.Write(_buffer, 0, bytes);
        _frames += interleaved.Length / _channels;
    }

    public void Dispose()
    {
        var data = _frames * _channels * 4;
        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)Math.Min(uint.MaxValue, 36 + data));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)_channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * _channels * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(_channels * 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 32);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)Math.Min(uint.MaxValue, data));
        _file.Position = 0;
        _file.Write(header);
        _file.Dispose();
    }
}
