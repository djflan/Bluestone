using Bluestone.Audio.Files;

namespace Bluestone.Tests.Unit.Audio;

public sealed class AudioFileReaderTests : IDisposable
{
    private const int Rate = 44_100;
    private readonly string _directory = TestAudioFiles.Directory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("pcm8", 1, 1.0 / 128)]
    [InlineData("pcm16", 2, 1.0 / 32768)]
    [InlineData("pcm24", 2, 1.0 / 8388608)]
    [InlineData("pcm32", 1, 1e-7)]
    [InlineData("float32", 2, 1e-7)]
    [InlineData("float64", 1, 1e-7)]
    [InlineData("ext24in32", 2, 1.0 / 8388608)]
    [InlineData("ext16", 6, 1.0 / 32768)]
    public void Wave_DecodesEverySupportedEncoding_ToTheWrittenSamples(string format, int channels, double tolerance)
    {
        var samples = TestAudioFiles.Sine(1000, channels, 440, Rate);
        var path = TestAudioFiles.Wave(_directory, $"{format}-{channels}.wav", format, Rate, channels, samples);

        AssertDecodes(path, AudioContainer.Wave, channels, samples, tolerance);
    }

    [Theory]
    [InlineData(null, 8, 1, 1.0 / 128)]
    [InlineData(null, 16, 2, 1.0 / 32768)]
    [InlineData(null, 24, 2, 1.0 / 8388608)]
    [InlineData(null, 32, 1, 1e-7)]
    [InlineData("NONE", 16, 2, 1.0 / 32768)]
    [InlineData("sowt", 16, 2, 1.0 / 32768)]
    [InlineData("sowt", 24, 1, 1.0 / 8388608)]
    [InlineData("fl32", 32, 2, 1e-7)]
    [InlineData("fl64", 64, 4, 1e-7)]
    public void Aiff_DecodesEverySupportedEncoding_ToTheWrittenSamples(string? compression, int bits, int channels, double tolerance)
    {
        var samples = TestAudioFiles.Sine(1000, channels, 440, Rate);
        var path = TestAudioFiles.Aiff(_directory, $"{compression ?? "aiff"}-{bits}-{channels}.aif", compression, bits, Rate, channels, samples);

        AssertDecodes(path, compression is null ? AudioContainer.Aiff : AudioContainer.AiffC, channels, samples, tolerance);
    }

    [Fact]
    public void Read_FromTheMiddle_AndPastTheEnd_ReturnsWhatIsThere()
    {
        var samples = TestAudioFiles.Sine(100, 2, 440, Rate);
        using var reader = AudioFileReader.Open(TestAudioFiles.Wave(_directory, "a.wav", "float32", Rate, 2, samples));
        var buffer = new float[50 * 2];

        Assert.Equal(20, reader.Read(80, 50, buffer));
        Assert.Equal(samples[160], buffer[0], 6);
        Assert.Equal(0, reader.Read(100, 50, buffer));
    }

    [Fact]
    public void Probe_ReportsRateChannelsAndLength()
    {
        var path = TestAudioFiles.Aiff(_directory, "b.aiff", null, 16, 96_000, 2, TestAudioFiles.Sine(12_345, 2, 440, 96_000));

        var format = AudioFileReader.Probe(path);

        Assert.Equal(96_000, format.SampleRate);
        Assert.Equal(12_345, format.Frames);
        Assert.Equal(2, format.Channels);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0, 0, 0, 0, 0, 0, 0, 0 }, "FLAC")]
    [InlineData(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 0, 0, 0 }, "MP3")]
    [InlineData(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 0, 0, 0, 0, 0, 0, 0 }, "Ogg")]
    [InlineData(new byte[] { 1, 2, 3 }, "too short")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0, (byte)'W', (byte)'A', (byte)'V', (byte)'E' }, "no format chunk")]
    public void UnsupportedOrBrokenFiles_FailWithAReason(byte[] content, string reason)
    {
        var path = Path.Combine(_directory, "bad.bin");
        File.WriteAllBytes(path, content);

        var error = Assert.Throws<AudioFileException>(() => AudioFileReader.Open(path));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompressedWaveFormat_IsRefused()
    {
        var path = TestAudioFiles.Wave(_directory, "c.wav", "pcm16", Rate, 1, new double[10]);
        var bytes = File.ReadAllBytes(path);
        bytes[20] = 2; // format tag 2: Microsoft ADPCM
        File.WriteAllBytes(path, bytes);

        Assert.Contains("compressed", Assert.Throws<AudioFileException>(() => AudioFileReader.Open(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtendedFloat_DecodesStandardRates()
    {
        // 44100 as an 80-bit extended float, as AIFF stores it.
        Assert.Equal(44_100, AudioFileReader.ExtendedToDouble([0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0]));
    }

    private static void AssertDecodes(string path, AudioContainer container, int channels, double[] samples, double tolerance)
    {
        using var reader = AudioFileReader.Open(path);
        Assert.Equal(container, reader.Format.Container);
        Assert.Equal(Rate, reader.Format.SampleRate);
        Assert.Equal(channels, reader.Format.Channels);
        Assert.Equal(samples.Length / channels, reader.Format.Frames);

        var decoded = new float[samples.Length];
        Assert.Equal(samples.Length / channels, reader.Read(0, samples.Length / channels, decoded));
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(samples[i] - decoded[i]) <= tolerance, $"Sample {i}: wrote {samples[i]}, read {decoded[i]}.");
        }
    }
}
