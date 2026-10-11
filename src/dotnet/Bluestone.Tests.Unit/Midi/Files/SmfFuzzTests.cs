using Bluestone.Midi.Files;
using CsCheck;
using static Bluestone.Tests.Unit.Midi.Files.SmfBytes;

namespace Bluestone.Tests.Unit.Midi.Files;

/// <summary>Malformed input must produce diagnostics or <see cref="SmfFormatException"/>, never another failure.</summary>
public sealed class SmfFuzzTests
{
    private static readonly byte[] Seed = File(1, 96,
        MTrk(0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, 0x00, 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08),
        MTrk(0x00, 0xFF, 0x03, 0x01, 0x41, 0x00, 0xC0, 0x05, 0x00, 0x90, 0x3C, 0x64, 0x60, 0x3C, 0x00, 0x00, 0xF0, 0x03, 0x43, 0x10, 0xF7, 0x10, 0xE0, 0x00, 0x40));

    private static bool SurvivesReadAndImport(byte[] bytes)
    {
        try
        {
            var read = SmfReader.Read(bytes, new SmfReadOptions { MaxEvents = 10_000 });
            var imported = SmfImporter.Import(read.File);
            _ = SmfWriter.Write(SmfExporter.Export(imported.Sequence).File);
            return true;
        }
        catch (SmfFormatException)
        {
            return true;
        }
    }

    [Fact]
    public void RandomBytes_AreHandled() =>
        Gen.Byte.Array[0, 200].Sample(SurvivesReadAndImport, iter: 5_000);

    [Fact]
    public void RandomBytesBehindAValidHeader_AreHandled() =>
        Gen.Byte.Array[0, 300].Sample(body => SurvivesReadAndImport([.. Header(1, 2, 96), .. Chunk("MTrk", body)]), iter: 5_000);

    [Fact]
    public void MutatedValidFiles_AreHandled() =>
        Gen.Select(Gen.Int[0, Seed.Length - 1], Gen.Byte).Array[1, 6].Sample(mutations =>
        {
            var bytes = (byte[])Seed.Clone();
            foreach (var (index, value) in mutations)
            {
                bytes[index] = value;
            }

            return SurvivesReadAndImport(bytes);
        }, iter: 5_000);

    [Fact]
    public void TruncatedValidFiles_AreHandled()
    {
        for (var length = 0; length <= Seed.Length; length++)
        {
            Assert.True(SurvivesReadAndImport(Seed[..length]));
        }
    }
}
