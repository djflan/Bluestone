using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Infrastructure.Projects;
using Bluestone.Midi.Files;
using Bluestone.Midi.SysEx;
using Bluestone.Tests.Unit.Playback;
using static Bluestone.Tests.Unit.Midi.Files.SmfBytes;

namespace Bluestone.Tests.Unit.Midi.SysEx;

/// <summary>SysEx survives import, saving, export, and playback byte for byte, whether or not Bluestone understands it.</summary>
public sealed class SysExPreservationTests
{
    private static readonly byte[] Unknown = [0xF0, 0x00, 0x21, 0x09, 0x12, 0x34, 0x56, 0xF7];

    // A recognized dialect with a wrong checksum: interpretation flags it, but must not repair or drop it.
    private static readonly byte[] DamagedGsReset = [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x42, 0xF7];

    private static readonly byte[] Song = File(1, 96, MTrk(
        0x00, 0xF0, 0x07, 0x00, 0x21, 0x09, 0x12, 0x34, 0x56, 0xF7,
        0x00, 0xF0, 0x0A, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x42, 0xF7,
        0x00, 0x90, 0x3C, 0x64,
        0x10, 0xF0, 0x03, 0x7D, 0x01, 0x02,
        0x10, 0xF7, 0x02, 0x03, 0xF7,
        0x40, 0x80, 0x3C, 0x40));

    private static string[] SysExIn(SmfFile file) =>
        [.. file.Tracks.SelectMany(t => t.Events).Select(e => e switch
        {
            SmfSysExEvent s => $"{s.Tick} F0 {Convert.ToHexString(s.Data.Span)}",
            SmfEscapeEvent x => $"{x.Tick} F7 {Convert.ToHexString(x.Data.Span)}",
            _ => null,
        }).OfType<string>()];

    private static SmfFile Reexport(Sequence sequence) => SmfReader.Read(SmfWriter.Write(SmfExporter.Export(sequence).File)).File;

    [Fact]
    public void ImportThenExport_KeepsEverySysExByte()
    {
        var original = SmfReader.Read(Song).File;

        var exported = Reexport(SmfImporter.Import(original).Sequence);

        Assert.Equal(4, SysExIn(original).Length);
        Assert.Equal(SysExIn(original), SysExIn(exported));
    }

    [Fact]
    public void ImportSaveLoadExport_KeepsEverySysExByte()
    {
        var original = SmfReader.Read(Song).File;
        var project = Project.CreateNew() with { Sequence = SmfImporter.Import(original).Sequence };

        var serializer = ProjectSerializer.Default;
        var reloaded = serializer.Deserialize(serializer.Serialize(new ProjectDocument(project))).Project;

        Assert.Equal(SysExIn(original), SysExIn(Reexport(reloaded.Sequence)));
    }

    [Fact]
    public void Playback_SendsSysExVerbatimWhetherOrNotItIsUnderstood()
    {
        var sequence = SmfImporter.Import(SmfReader.Read(Song).File).Sequence;
        var track = Assert.Single(sequence.Tracks);

        var plan = PlanDump.Compile(PlanDump.Routed(sequence));
        var sent = plan.Payloads.Select(p => Convert.ToHexString(p.Span)).ToList();

        Assert.Null(SysExInterpreter.Interpret(Unknown));
        Assert.False(Assert.IsType<GsDataSet>(SysExInterpreter.Interpret(DamagedGsReset)).ChecksumValid);
        Assert.Equal([Convert.ToHexString(Unknown), Convert.ToHexString(DamagedGsReset), "F07D010203F7"], sent);
        Assert.Empty(plan.Diagnostics);
    }
}
